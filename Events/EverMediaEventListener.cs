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
using System.Threading.Channels; // 必须引入
using System.Threading.Tasks;
using EverMedia.Services;

namespace EverMedia.Events;

/// <summary>
/// 事件监听器：基于队列的生产者-消费者模型，防止高并发触发风控。
/// </summary>
public class EverMediaEventListener : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly EverMediaService _everMediaService;
    private readonly IFileSystem _fileSystem;

    // --- 去抖动与重试追踪 ---
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)> _probeFailureTracker = new();
    private readonly TimeSpan _shortTermRetryDelay = TimeSpan.FromSeconds(10);

    // --- [新增] 队列处理系统 ---
    private readonly Channel<BaseItem> _processingChannel;
    private readonly ConcurrentDictionary<Guid, byte> _queuedItems = new(); // 用于队列去重
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _queueProcessorTask;

    public EverMediaEventListener(
        ILogger logger,
        EverMediaService everMediaService,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _everMediaService = everMediaService;
        _fileSystem = fileSystem;

        // 创建无界通道 (存储引用占用内存很小，Unbounded 较为安全)
        _processingChannel = Channel.CreateUnbounded<BaseItem>();

        // 启动后台消费者线程
        _queueProcessorTask = Task.Run(ProcessQueueAsync);
    }

    // --- 消费者循环：核心限流逻辑 ---
    private async Task ProcessQueueAsync()
    {
        _logger.Info("[EverMedia] Queue: Background processor started.");

        while (!_disposeCts.Token.IsCancellationRequested)
        {
            try
            {
                // 1. 等待并读取队列中的下一个项目
                var item = await _processingChannel.Reader.ReadAsync(_disposeCts.Token);

                // 2. 从去重集合中移除（允许该 ID 再次入队）
                _queuedItems.TryRemove(item.Id, out _);

                // 3. 获取配置并执行限流
                var config = Plugin.Instance.Configuration;
                // 复用 TaskConfig 中的速率限制配置
                // 注意：如果不想启用限流，请在设置中将其设为 0
                int rateLimitSeconds = config?.TaskConfig?.BootstrapTaskRateLimitSeconds ?? 2;

                if (rateLimitSeconds > 0)
                {
                    // _logger.Debug($"[EverMedia] Queue: Waiting {rateLimitSeconds}s before processing {item.Name}...");
                    await Task.Delay(TimeSpan.FromSeconds(rateLimitSeconds), _disposeCts.Token);
                }

                // 4. 执行核心业务逻辑
                await ProcessItemInternalAsync(item);
            }
            catch (OperationCanceledException)
            {
                break; // 插件关闭或卸载时正常退出
            }
            catch (Exception ex)
            {
                _logger.Error($"[EverMedia] Queue: Fatal error in processor loop: {ex.Message}");
            }
        }
    }

    // --- 统一的业务处理逻辑 (融合了原 Added 和 Updated 的判断) ---
    private async Task ProcessItemInternalAsync(BaseItem item)
    {
        try
        {
            // 二次检查：处理时确保插件仍开启
            var config = Plugin.Instance.Configuration;
            if (config == null || !config.EnablePlugin) return;

            // 二次检查：项目可能在排队期间被删除
            if (item == null) return;

            _logger.Debug($"[EverMedia] Queue: Processing item '{item.Name ?? item.Path}' (ID: {item.Id})");

            string medInfoPath = _everMediaService.GetMedInfoPath(item);
            bool medInfoExists = _fileSystem.FileExists(medInfoPath);
            
            // 获取当前流信息
            var mediaStreams = item.GetMediaStreams();
            var hasVideoOrAudio = mediaStreams?.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio) == true;

            // --- 场景 1: 需要恢复 (有 MedInfo，但 Emby 没数据) ---
            if (!hasVideoOrAudio && medInfoExists)
            {
                // 检查是否是字幕引起的歧义状态 (ItemUpdated 原有逻辑)
                int savedExternalCount = _everMediaService.GetSavedExternalSubCount(item);
                int currentExternalCount = mediaStreams?.Count(s => s.Type == MediaStreamType.Subtitle && s.IsExternal) ?? 0;

                if (currentExternalCount != savedExternalCount)
                {
                    _logger.Info($"[EverMedia] Logic: Subtitle mismatch for {item.Name}. Deleting stale .medinfo and probing.");
                    try { _fileSystem.DeleteFile(medInfoPath); } catch { }
                    
                    await HandleProbeWithRetryAsync(item, config);
                }
                else
                {
                    _logger.Info($"[EverMedia] Logic: Restoring metadata for {item.Name} from local file.");
                    await _everMediaService.RestoreAsync(item);
                    // 恢复成功，清除失败计数
                    _probeFailureTracker.TryRemove(item.Id, out _);
                }
            }
            // --- 场景 2: 需要备份 (Emby 有数据，但没有 MedInfo 文件) ---
            else if (hasVideoOrAudio && !medInfoExists)
            {
                _logger.Info($"[EverMedia] Logic: Creating backup for {item.Name}.");
                await _everMediaService.BackupAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
            // --- 场景 3: 彻底丢失 (既没数据，也没本地备份) -> 触发 FFProbe ---
            else if (!hasVideoOrAudio && !medInfoExists)
            {
                 await HandleProbeWithRetryAsync(item, config);
            }
            // --- 场景 4: 健康状态 ---
            else
            {
                // HasV/A && MedInfoExists -> 正常，无需操作
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Queue: Error processing item {item.Name}: {ex.Message}");
        }
    }

    // --- 封装熔断与重试逻辑 ---
    private async Task HandleProbeWithRetryAsync(BaseItem item, EverMediaConfig config)
    {
        var now = DateTime.UtcNow;
        int maxRetries = config.FailureConfig.MaxProbeRetries;
        TimeSpan resetInterval = TimeSpan.FromMinutes(config.FailureConfig.ProbeFailureResetMinutes);
        
        (int currentCount, DateTime lastAttempt) = _probeFailureTracker.GetValueOrDefault(item.Id, (0, DateTime.MinValue));

        // 熔断重置检查
        if (currentCount >= maxRetries && (now - lastAttempt > resetInterval))
        {
            currentCount = 0;
            _logger.Info($"[EverMedia] Retry: Reset interval passed for {item.Name}. Resetting failure count.");
        }

        // 熔断检查
        if (currentCount >= maxRetries)
        {
            _logger.Debug($"[EverMedia] Retry: Item {item.Name} hit max retries ({maxRetries}). Skipping.");
            return;
        }

        // 短期冷却 (防止极速重试) - 虽然有队列限流，但保留此逻辑作为双重保险
        if (now - lastAttempt < _shortTermRetryDelay)
        {
            await Task.Delay(_shortTermRetryDelay - (now - lastAttempt));
            now = DateTime.UtcNow;
        }

        currentCount++;
        _probeFailureTracker.AddOrUpdate(item.Id, (currentCount, now), (key, old) => (currentCount, now));

        _logger.Info($"[EverMedia] Retry: Triggering FFProbe for {item.Name} (Attempt {currentCount}/{maxRetries}).");
        await TriggerFullProbeAsync(item);
    }

    // --- 辅助方法：入队 ---
    private void EnqueueItem(BaseItem item)
    {
        // 去重：如果 Item 已经在队列中，跳过
        if (_queuedItems.TryAdd(item.Id, 0))
        {
            if (!_processingChannel.Writer.TryWrite(item))
            {
                _logger.Warn($"[EverMedia] Queue: Failed to enqueue item {item.Name}.");
                _queuedItems.TryRemove(item.Id, out _);
            }
        }
    }

    // --- ItemAdded 事件处理 ---
    public void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // 快速过滤，不阻塞主线程
        if (e.Item is BaseItem item && item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
             // 仅负责入队
             EnqueueItem(item);
        }
    }

    // --- ItemUpdated 事件处理 ---
    public async void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is BaseItem item && item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            // 保持原有的 Debounce (防抖) 逻辑
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
                // 等待防抖时间
                await Task.Delay(TimeSpan.FromSeconds(1), newCts.Token);
                
                // 防抖结束，入队处理
                // 注意：这里不再直接执行逻辑，而是交给队列
                _logger.Debug($"[EverMedia] Event: ItemUpdated debounce passed for {item.Name}. Enqueuing.");
                EnqueueItem(item);
            }
            catch (OperationCanceledException)
            {
                // 被新的 Update 事件取消，正常忽略
            }
            finally
            {
                _debounceTokens.TryRemove(itemId, out _);
                newCts.Dispose();
            }
        }
    }

    // --- 触发 FFProbe ---
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

    // --- 资源销毁 ---
    public async ValueTask DisposeAsync()
    {
        // 1. 发出停止信号
        _disposeCts.Cancel();

        // 2. 标记通道写入完成
        _processingChannel.Writer.TryComplete();

        // 3. 等待消费者任务结束
        if (_queueProcessorTask != null)
        {
            try { await _queueProcessorTask; } catch { }
        }

        // 4. 清理集合
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
