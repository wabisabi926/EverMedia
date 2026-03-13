// Events/EverMediaEventListener.cs
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EverMedia.Services;

namespace EverMedia.Events;

public class EverMediaEventListener : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly EverMediaService _everMediaService;
    private readonly IFileSystem _fileSystem;

    // --- 去抖动与重试追踪 ---
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)> _probeFailureTracker = new();
    private readonly TimeSpan _shortTermRetryDelay = TimeSpan.FromSeconds(10);

    // --- 队列处理系统 ---
    private readonly Channel<BaseItem> _processingChannel;
    private readonly ConcurrentDictionary<Guid, byte> _queuedItems = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<Task> _workerTasks = new();

    public EverMediaEventListener(
        ILogger logger,
        EverMediaService everMediaService,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _everMediaService = everMediaService;
        _fileSystem = fileSystem;

        _processingChannel = Channel.CreateUnbounded<BaseItem>();

        var config = Plugin.Instance.Configuration;
        int concurrency = config?.TaskConfig?.MaxConcurrency ?? 1;
        if (concurrency < 1) concurrency = 1;

        _logger.Info($"[EverMedia] Queue: Starting {concurrency} worker threads.");

        for (int i = 0; i < concurrency; i++)
        {
            int workerId = i + 1;
            _workerTasks.Add(Task.Run(() => ProcessQueueAsync(workerId)));
        }
    }

    // --- 消费者循环 ---
    private async Task ProcessQueueAsync(int workerId)
    {
        while (!_disposeCts.Token.IsCancellationRequested)
        {
            try
            {
                var item = await _processingChannel.Reader.ReadAsync(_disposeCts.Token);
                _queuedItems.TryRemove(item.Id, out _);

                var config = Plugin.Instance.Configuration;
                
                await ProcessItemInternalAsync(item, workerId, config);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"[EverMedia] Worker-{workerId}: Error: {ex.Message}");
            }
        }
    }

    private async Task ProcessItemInternalAsync(BaseItem item, int workerId, EverMediaConfig? config)
    {
        try
        {
            if (config == null || !config.EnablePlugin) return;
            if (item == null) return;

            string medInfoPath = _everMediaService.GetMedInfoPath(item);
            bool medInfoExists = _fileSystem.FileExists(medInfoPath);
            
            var mediaStreams = item.GetMediaStreams();
            var hasVideoOrAudio = mediaStreams?.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio) == true;

            // --- 场景 0: 原盘媒体文件处理 ---
            if (config.EnableDiscMediaInfoExtract && _everMediaService.IsDiscMediaFile(item))
            {
                _logger.Info($"[EverMedia] Worker-{workerId}: Processing disc media file {item.Name}.");
                await _everMediaService.ExtractDiscMediaInfoAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
                return;
            }

            // --- 场景 1: 恢复 (本地操作，全速执行) ---
            if (!hasVideoOrAudio && medInfoExists)
            {
                _logger.Info($"[EverMedia] Worker-{workerId}: Metadata missing but .medinfo found for {item.Name}. Restoring directly.");
                await _everMediaService.RestoreAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
            // --- 场景 2: 备份 (本地操作，全速执行) ---
            else if (hasVideoOrAudio && !medInfoExists)
            {
                _logger.Info($"[EverMedia] Worker-{workerId}: Backup {item.Name}.");
                await _everMediaService.BackupAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
            // --- 场景 3: 探测 (远程操作，需要限流) ---
            else if (!hasVideoOrAudio && !medInfoExists)
            {
                 // 将限流逻辑封装在 HandleProbeWithRetryAsync 内部
                 await HandleProbeWithRetryAsync(item, config, workerId);
            }
            else
            {
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Worker-{workerId}: Error processing {item.Name}: {ex.Message}");
        }
    }

    // --- 将限流逻辑移到这里 ---
    private async Task HandleProbeWithRetryAsync(BaseItem item, EverMediaConfig? config, int workerId)
    {
        // 1. 安全获取限流时间 (默认 2 秒)
        int rateLimitSeconds = config?.TaskConfig?.BootstrapTaskRateLimitSeconds ?? 2;

        // 2. 在发起远程请求前，执行等待
        if (rateLimitSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(rateLimitSeconds), _disposeCts.Token);
        }

        var now = DateTime.UtcNow;

        int maxRetries = config?.FailureConfig?.MaxProbeRetries ?? 3;
        int resetMinutes = config?.FailureConfig?.ProbeFailureResetMinutes ?? 30;

        TimeSpan resetInterval = TimeSpan.FromMinutes(resetMinutes);
        
        (int currentCount, DateTime lastAttempt) = _probeFailureTracker.GetValueOrDefault(item.Id, (0, DateTime.MinValue));

        if (currentCount >= maxRetries && (now - lastAttempt > resetInterval))
        {
            currentCount = 0;
            _logger.Info($"[EverMedia] Worker-{workerId}: Reset failure count for {item.Name}.");
        }

        if (currentCount >= maxRetries)
        {
            _logger.Debug($"[EverMedia] Worker-{workerId}: {item.Name} hit max retries. Skipping.");
            return;
        }

        // 短期重试保护
        if (now - lastAttempt < _shortTermRetryDelay)
        {
            await Task.Delay(_shortTermRetryDelay - (now - lastAttempt));
            now = DateTime.UtcNow;
        }

        currentCount++;
        _probeFailureTracker.AddOrUpdate(item.Id, (currentCount, now), (key, old) => (currentCount, now));

        _logger.Info($"[EverMedia] Worker-{workerId}: Triggering FFProbe for {item.Name} (Attempt {currentCount}/{maxRetries}).");
        
        await TriggerFullProbeAsync(item);
    }

    // --- 以下保持不变 ---
    private void EnqueueItem(BaseItem item)
    {
        if (_queuedItems.TryAdd(item.Id, 0))
        {
            if (!_processingChannel.Writer.TryWrite(item))
            {
                _logger.Warn($"[EverMedia] Queue: Failed to enqueue {item.Name}.");
                _queuedItems.TryRemove(item.Id, out _);
            }
        }
    }

    public void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is BaseItem item && item.Path != null)
        {
            // 处理 .strm 文件
            if (item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueItem(item);
            }
            // 处理原盘媒体文件
            else if (_everMediaService.IsDiscMediaFile(item))
            {
                EnqueueItem(item);
            }
        }
    }

    public async void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is BaseItem item && item.Path != null)
        {
            // 处理 .strm 文件
            if (item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                var itemId = item.Id;
                if (_debounceTokens.TryGetValue(itemId, out var existingCts))
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                }

                var newCts = new CancellationTokenSource();
                _debounceTokens[itemId] = newCts;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), newCts.Token);
                    EnqueueItem(item);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _debounceTokens.TryRemove(itemId, out _);
                    newCts.Dispose();
                }
            }
            // 处理原盘媒体文件
            else if (_everMediaService.IsDiscMediaFile(item))
            {
                EnqueueItem(item);
            }
        }
    }

    private async Task TriggerFullProbeAsync(BaseItem item)
    {
        var directoryService = new DirectoryService(_logger, _fileSystem);
        var refreshOptions = new MetadataRefreshOptions(directoryService)
        {
            EnableRemoteContentProbe = true,
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllImages = false,
            EnableThumbnailImageExtraction = false,
            EnableSubtitleDownloading = false
        };

        try
        {
            await item.RefreshMetadata(refreshOptions, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] FFProbe failed for {item.Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _processingChannel.Writer.TryComplete();

        if (_workerTasks.Count > 0)
        {
            try { await Task.WhenAll(_workerTasks); } catch { }
        }
        _workerTasks.Clear();

        foreach (var kvp in _debounceTokens)
        {
            try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { }
        }
        
        _debounceTokens.Clear();
        _probeFailureTracker.Clear();
        _queuedItems.Clear();
        _disposeCts.Dispose();
    }
}