 **中文说明** | [English](README_EN.md)

# EverMedia

> **Emby Plugin**

### **持久化 .strm 文件的音视频与字幕信息, 支持自动修复。告别重复扫描，提升加载速度。**

![EverMediaLogo](https://raw.githubusercontent.com/Swiftfrog/swiftfrog.github.io/master/EverMediaLogo.png)

---

## 功能

| 功 能 特 性 | 说 明 |
|------|------|
|  **自动备份** | 当 `.strm` 文件或原盘媒体文件被扫描或更新时，自动将音视频流、字幕、时长、分辨率等信息保存到 `-mediainfo.json` 文件中。 |
|  **自动恢复** | 自动从 `-mediainfo.json` 恢复，无需手动刷新。 |
|  **自动更新字幕** | **添加 / 删除字幕后，自动更新media info。无需手动。** |
|  **原盘媒体信息提取** | 支持 `.iso`、`.img`、`.m2ts`、`.ts`、`.vob` 等原盘格式的媒体信息提取。 |
|  **自定义存储** | 可将所有 `-mediainfo.json` 文件统一存放在指定目录。 |
|  **自定义设置** | 可独立启用/禁用/参数设置等，灵活控制资源。 |
|  **多线程设置** | 配置线程数量和执行 FFProbe 间隔，避免高并发导致风控。 |
|  **增量扫描** | 计划任务只扫描“上次运行后修改过”的 `.strm` 文件，效率高。 |

> **适用场景**：  
> - 使用 `.strm` 播放网络视频（如 网盘、Plex 链接）
> - 媒体库中包含大量外链文件
> - 想要稳定、快速加载，不依赖 Emby 每次重扫
> - 网络源不稳定，频繁触发探测导致封控

---

## 安装说明

### 1. 下载插件
从 Releases 页面下载最新版：
```
EverMedia.dll
```

### 2. 安装到 Emby
```
EverMedia.dll 复制到 Emby Server下的plugins
```
> **提示**：插件安装后默认**不自动运行**，需手动启用（见下文配置）。

---

## 配置说明

安装后，进入：  
**仪表盘 → 插件 → EverMedia**

### 🔧 基础设置

| 选项 | 说明 |
|------|------|
| **启用插件** (`EnablePlugin`) | ✅ 开启后，监听 `.strm` 文件和原盘媒体文件，自动更新 `-mediainfo.json`（推荐开启） |
| **启用计划任务** (`EnableBootstrapTask`) | ✅ 开启后，计划任务方可使用。执行扫库任务特别耗费资源，避免误操作，所以添加此开关。 |
| **备份模式** (`BackupMode`) | - `SideBySide`： `-mediainfo.json` 与媒体文件同目录（默认）<br>- `Centralized`： `-mediainfo.json` 统一存入指定目录。 |
| **存储路径** (`CentralizedRootPath`) | 如果选择集中存储，需要指定的文件夹路径。`SideBySide` 无需设置。 |
| **媒体信息JSON根目录** (`MediaInfoJsonRootFolder`) | 集中存储媒体信息JSON文件的根目录。留空则使用SideBySide模式。 |
| **启用原盘媒体信息提取** (`EnableDiscMediaInfoExtract`) | 启用对原盘媒体文件的信息提取功能。 |
| **上次任务运行时间UTC** (`LastBootstrapTaskRun`) |任务运行后，自动记录时间。非必要不操作。<br>如果想扫描某个时间段后添加的媒体项目或者重新扫描，可以根据时间自行设定。|
| **FFProbe 最大重试次数** (`MaxProbeRetries`) | 防止.strm失效后，emby不停访问导致死循环。 |
| **FFProbe 失败重置时间 (分钟)** (`ProbeFailureResetMinutes`) | 刷新失败后，在一定时间内，不再重复访问，直接跳过。这个时间过后，如果任务运行，则继续访问。|
| **全局并发线程数** (`MaxConcurrency`) | 同时刷新媒体信息的线程数量，例如： 2就是同时刷新2个项目。 |
| **全局访问间隔（秒）** (`BootstrapTaskRateLimitSeconds`)| 任务刷新的间隔，例如：2就是2个项目之间最少要间隔2秒。0就是没有限制。请根据自己的情况定义，默认偏保守。 |

> **建议配置**：  
> - 启用插件
> - 启用计划任务
> - 备份模式：`SideBySide`
> - FFProbe 最大重试次数：`3`
> - FFProbe 失败重置时间 (分钟)： `30`
> - 全局并发线程数：`2`
> - 全局访问间隔（秒）： `2`
> - **首次安装建议手动执行一次计划任务**

---

## 文件结构示例

### 默认模式（SideBySide）
```
/Media/Movies/
├── MyMovie.strm
├── MyMovie-mediainfo.json     ← 自动生成，存储元数据
└── MyMovie.srt         ← 字幕文件（自动识别）
```

### 中心化模式（Centralized）
```
/Media/Movies/
└── MyMovie.strm

/.evermedia/
└── Movies/
    └── MyMovie-mediainfo.json     ← 统一存放，便于备份和迁移
```

> `-mediainfo.json` 是**纯文本 JSON 文件**，可手动查看或备份，但**不要手动修改**。

---

## 使用场景示例

| 场景 | EverMedia 行为 |
|------|----------------|
| **添加一个新的 `.strm` 文件** | 自动探测 → 生成 `-mediainfo.json` → 下次播放秒开 |
| **添加一个原盘媒体文件** | 自动探测 → 生成 `-mediainfo.json` → 下次播放秒开 |
| **为 `.strm` 添加中文字幕** | 检测到“仅字幕变化” → 删除旧 `-mediainfo.json` → 重新探测 → 保存含新字幕的新文件 |
| **Emby 数据库崩溃** | 重启后，计划任务自动扫描所有 `.strm` 和原盘文件 → 从 `-mediainfo.json` 恢复所有元数据 |
| **网络源断开导致探测失败** | `-mediainfo.json` 保留历史信息，播放仍可正常显示标题、时长、分辨率 |
| **手动删除 `-mediainfo.json`** | 下次播放时，自动重新探测并重建 |

---

## ❓ 常见问题

### Q1：`-mediainfo.json` 文件能删除吗？
> 可以删除。插件会在下次播放或扫描时自动重建。但**不建议手动修改**内容。

### Q2：插件会影响性能吗？
> **极低开销**。  
> - 事件监听：仅在 `.strm` 文件或原盘媒体文件变化时触发；  
> - 计划任务：默认每天执行一次，或手动触发；  
> - FFProbe：仅在首次或字幕变更时执行，之后直接读 `-mediainfo.json`。

### Q3：支持 Jellyfin 吗？
> 目前仅支持 **Emby 4.9.1.x**（基于 .NET 8）。Jellyfin 还没开发计划，暂不支持。

### Q4：为什么播放时还是慢？
> 请检查：
> - `-mediainfo.json` 文件是否存在？
> - `.strm` 文件或原盘媒体文件路径是否正确？
> - 是否启用了“启用插件”和“启用计划任务”？
> - 网络源是否可访问？

### Q5：支持哪些原盘媒体格式？
> 支持 `.iso`、`.img`、`.m2ts`、`.ts`、`.vob` 等原盘格式。

### Q6：如何手动触发扫描？
> 进入 **仪表盘 → 计划任务 → EverMedia 引导任务 → 点击“运行”**。

---

## 📜 开发者说明

- **项目地址**：https://github.com/wabisabi926/EverMedia  
- **贡献**：欢迎提交 Issue / Pull Request  
- **许可证**：[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) 任何基于本项目代码的分发（包括商业用途）**必须以相同许可证开源全部源代码**。
- **依赖**：Emby Server 4.9.1.80，.NET 8

---

## 💬 支持与反馈

如有问题、建议或想支持本项目：

- 在 GitHub 提交 [Issue](https://github.com/wabisabi926/EverMedia/issues)  
- 给项目点个 ⭐️，让更多人受益！

> EverMedia —— 让你的媒体库，更智能、更稳定、更持久。
