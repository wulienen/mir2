# Design Document: Streaming Resource Loader

## Overview

本设计文档描述了PC游戏客户端的流式资源加载系统实现方案。该系统允许玩家在只下载少量核心资源后即可开始游戏，游戏过程中按需下载并渲染所需的图片资源。

系统采用异步下载、本地缓存、占位纹理等技术，确保游戏流畅运行的同时逐步完善本地资源。设计参考了安卓微端的成熟实现，并针对PC平台的SlimDX/DirectX渲染环境进行了适配。

## Architecture

系统由以下核心组件组成：

**客户端组件：**
- ResourceHelper: 资源下载助手，负责HTTP通信和缓存管理
- MLibrary: 库文件管理，修改以支持流式加载
- MImage: 图片资源，增加下载状态管理
- Settings: 配置管理，添加流式加载配置项

**服务端组件：**
- Resource_Server: ASP.NET Core Web API服务，提供资源下载接口
- LibraryController: API控制器，处理/api/libheader和/api/libimage请求
- LibraryParser: 库文件解析器，读取.Lib文件格式

数据流程：
1. 游戏请求渲染图片时，MLibrary检查本地文件
2. 如果库文件不存在，通过ResourceHelper下载头信息
3. 如果图片数据为空，异步下载图片数据
4. 下载期间返回占位纹理，完成后替换为实际纹理
5. 下载的数据批量写入本地缓存

服务端流程：
1. 客户端请求/api/libheader/{path}/{name}获取库文件头
2. 服务端解析.Lib文件，返回TotalLength和HeaderBytes
3. 客户端请求/api/libimage/{path}/{name}/{index}获取图片数据
4. 服务端根据索引计算偏移量，返回图片二进制数据

## Components and Interfaces

### ResourceHelper (新增)

负责与资源服务器通信，管理下载队列和本地缓存合并。

```csharp
namespace Client.MirGraphics
{
    public static class ResourceHelper
    {
        private static readonly HttpClient httpClient;
        public static bool ServerActive { get; private set; }
        public static readonly SemaphoreSlim Semaphore;
        private const int MaxConcurrentDownloads = 10;
        private static ConcurrentDictionary<string, List<PendingImageWrite>> PendingWrites;
        private static int ExceptionCount;
        private static DateTime RetryTime;
        
        static ResourceHelper()
        {
            Semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
            httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri($"http://{Settings.MicroClientIP}:{Settings.MicroClientPort}/api/");
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            PendingWrites = new ConcurrentDictionary<string, List<PendingImageWrite>>();
        }
        
        public static bool CheckServerOnline();
        public static Task<bool> GetHeaderAsync(string fileName);
        public static Task<byte[]> GetImageAsync(string fileName, int index, int length);
        public static void ProcessPendingWrites();
    }
    
    public class PendingImageWrite
    {
        public int Position { get; set; }
        public byte[] Data { get; set; }
    }
}
```

### MLibrary (修改)

修改现有MLibrary类，增加流式加载支持，包括头文件下载状态管理。

```csharp
public sealed class MLibrary
{
    // 新增字段
    private byte _headerStatus;  // 0=未加载, 1=下载中, 2=已加载
    private DateTime _nextRetryTime;
    private readonly object _loadLock = new object();
    
    // 修改Initialize方法
    public void Initialize()
    {
        if (_initialized) return;
        
        if (!File.Exists(_fileName))
        {
            if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
            {
                // 触发异步下载头文件
                InitializeFromServer();
            }
            return;
        }
        // 原有本地加载逻辑
    }
    
    private async void InitializeFromServer();
    private bool IsImageDataEmpty(int index);
}
```

### MImage (修改)

修改现有MImage类，增加下载状态管理和占位纹理支持。

```csharp
public sealed class MImage
{
    // 新增字段
    private byte _downloadStatus;  // 0=未下载, 1=下载中, 2=完成
    private DateTime _nextRetryTime;
    private static Texture _placeholderTexture;
    
    public void CreateTextureFromData(byte[] data);
    
    public static Texture GetPlaceholderTexture(int width, int height)
    {
        if (_placeholderTexture == null || _placeholderTexture.Disposed)
        {
            _placeholderTexture = new Texture(DXManager.Device, 1, 1, 1, 
                Usage.None, Format.A8R8G8B8, Pool.Managed);
            // 填充透明像素
        }
        return _placeholderTexture;
    }
}
```

### Settings (修改)

添加MicroClientEnabled、MicroClientIP、MicroClientPort配置项。

```csharp
public static class Settings
{
    // 新增配置项
    public static bool MicroClientEnabled = false;
    public static string MicroClientIP = "127.0.0.1";
    public static int MicroClientPort = 8080;
    public static string MicroClientPath = "./Data/";
}
```

### Resource_Server (新增 - 服务端)

基于ASP.NET Core的资源服务器，提供HTTP API供客户端下载资源。

项目结构：
```
MicroEndServer/
├── Program.cs
├── appsettings.json
├── Controllers/
│   └── LibraryController.cs
└── Services/
    └── LibraryParser.cs
```

### LibraryController (新增 - 服务端)

API控制器，处理库文件头和图片数据请求。

```csharp
namespace MicroEndServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class LibraryController : ControllerBase
    {
        private readonly LibraryParser _parser;
        private readonly string _resourcePath;
        
        // GET /api/ping - 检查服务器可用性
        [HttpGet("ping")]
        public IActionResult Ping() => Ok("pong");
        
        // GET /api/libheader/{path}/{name} - 获取库文件头
        [HttpGet("libheader/{path}/{name}")]
        public IActionResult GetLibraryHeader(string path, string name);
        
        // GET /api/libimage/{path}/{name}/{index} - 获取图片数据
        [HttpGet("libimage/{path}/{name}/{index}")]
        public IActionResult GetLibraryImage(string path, string name, int index);
    }
}
```

### LibraryParser (新增 - 服务端)

库文件解析器，负责读取和解析.Lib文件格式。

```csharp
namespace MicroEndServer.Services
{
    public class LibraryParser
    {
        // 解析库文件头，返回TotalLength和HeaderBytes
        public LibraryHeaderResult ParseHeader(string filePath);
        
        // 获取指定索引的图片数据
        public byte[] GetImageData(string filePath, int index);
        
        // 计算图片在文件中的偏移量
        private (int offset, int length) GetImageOffset(string filePath, int index);
    }
    
    public class LibraryHeaderResult
    {
        public int TotalLength { get; set; }
        public byte[] HeaderBytes { get; set; }
    }
}
```

## Data Models

### LibraryHeader (客户端)

```csharp
public class LibraryHeader
{
    public int TotalLength { get; set; }
    public byte[] HeaderBytes { get; set; }
}
```

### DownloadState (客户端)

```csharp
public enum DownloadState : byte
{
    NotStarted = 0,
    Downloading = 1,
    Completed = 2,
    Failed = 3
}
```

### Library File Format (.Lib) - 服务端解析

库文件格式说明（用于服务端解析）：

```
文件结构:
┌─────────────────────────────────────┐
│ Version (4 bytes, int32)            │  版本号，当前为3
├─────────────────────────────────────┤
│ ImageCount (4 bytes, int32)         │  图片数量
├─────────────────────────────────────┤
│ FrameSeek (4 bytes, int32)          │  帧数据偏移量（版本>=3）
├─────────────────────────────────────┤
│ IndexList[ImageCount] (int32 each)  │  每个图片的文件偏移量
├─────────────────────────────────────┤
│ Image Data...                       │  图片数据区域
├─────────────────────────────────────┤
│ Frame Data (if version >= 3)        │  帧动画数据
└─────────────────────────────────────┘

单个图片数据结构 (17字节头部 + 压缩数据):
┌─────────────────────────────────────┐
│ Width (2 bytes, int16)              │
│ Height (2 bytes, int16)             │
│ X (2 bytes, int16)                  │
│ Y (2 bytes, int16)                  │
│ ShadowX (2 bytes, int16)            │
│ ShadowY (2 bytes, int16)            │
│ Shadow (1 byte)                     │  最高位表示是否有Mask层
│ Length (4 bytes, int32)             │  压缩数据长度
├─────────────────────────────────────┤
│ CompressedData[Length]              │  GZip压缩的ARGB像素数据
└─────────────────────────────────────┘
```

### LibraryHeaderResponse (服务端API响应)

```csharp
public class LibraryHeaderResponse
{
    public int TotalLength { get; set; }      // 文件总长度
    public string HeaderBytes { get; set; }   // Base64编码的头部字节
}
```

### ServerSettings (服务端配置)

```csharp
public class ServerSettings
{
    public int Port { get; set; } = 8080;
    public string ResourcePath { get; set; } = "./Data/";
    public bool EnableCors { get; set; } = true;
    public bool EnableLogging { get; set; } = true;
}
```

## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system-essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*

### Property 1: Retry Timing Consistency

*For any* failed download operation (header or image), the next retry time SHALL be set to exactly the specified interval (15 seconds for server connection, 1 second for individual downloads).

**Validates: Requirements 1.2, 2.3, 3.5**

### Property 2: File Position Integrity

*For any* downloaded image data, when written to the local cache file, the data SHALL be written at the exact position specified in the library header index.

**Validates: Requirements 3.3, 6.1**

### Property 3: Concurrency Limit Enforcement

*For any* sequence of download requests, the number of concurrent active downloads SHALL never exceed the configured maximum (10).

**Validates: Requirements 3.6, 5.1**

### Property 4: Texture State Consistency

*For any* image request, the returned texture SHALL be a placeholder while download status is "Downloading", and SHALL be the actual texture when download status is "Completed".

**Validates: Requirements 4.1, 4.3**

### Property 5: Disabled Mode Behavior

*For any* library access when MicroClientEnabled is false, the system SHALL not make any network requests and SHALL behave identically to the original implementation.

**Validates: Requirements 7.4, 9.2**

### Property 6: Header Download Triggers File Creation

*For any* library file that does not exist locally, accessing it SHALL trigger a header download, and upon successful download, a local file with the correct pre-allocated size SHALL be created.

**Validates: Requirements 2.1, 2.2**

### Property 7: Empty Data Triggers Download

*For any* image whose local data consists entirely of zeros, requesting that image for rendering SHALL initiate an asynchronous download.

**Validates: Requirements 3.1**

### Property 8: Exception Counter Threshold

*For any* sequence of network exceptions, when the exception count reaches 5, the server SHALL be marked as unavailable (ServerActive = false).

**Validates: Requirements 8.2, 8.3**

### Property 9: Settings Persistence Round-Trip

*For any* valid settings configuration, saving and then loading the settings SHALL produce an equivalent configuration.

**Validates: Requirements 7.5**

### Property 10: Local Resource Optimization

*For any* library file that exists locally with valid (non-zero) data, accessing images SHALL not trigger any network requests.

**Validates: Requirements 9.1**

### Property 11: Placeholder Dimension Matching

*For any* image with known dimensions from the header, the placeholder texture returned during download SHALL have the same width and height.

**Validates: Requirements 4.4**

### Property 12: Server Header Response Integrity

*For any* valid library file, the server's libheader API response SHALL contain the correct TotalLength matching the actual file size, and HeaderBytes that when decoded and written to a new file, produces a valid library file header.

**Validates: Requirements 10.1, 10.5**

### Property 13: Server Image Data Correctness

*For any* valid image index in a library file, the server's libimage API response SHALL return the exact binary data that exists at the calculated offset in the original file.

**Validates: Requirements 10.2, 10.6, 12.2, 12.3**

### Property 14: Server Error Response Consistency

*For any* request for a non-existent file, the server SHALL return HTTP 404, and for any internal error, the server SHALL return HTTP 500.

**Validates: Requirements 10.7, 10.8**

### Property 15: Server Ping Availability

*For any* ping request to an active server, the server SHALL respond with HTTP 200 and "pong" body.

**Validates: Requirements 10.3**

## Error Handling

1. **网络错误**: 记录日志（包含文件名和索引），增加异常计数，达到5次后标记服务器不可用
2. **文件I/O错误**: 记录日志，使用文件锁防止并发写入冲突
3. **数据校验错误**: 验证下载数据长度与预期是否匹配，不匹配时记录错误并重试
4. **服务器不可用**: 显示警告消息，每15秒重试连接

## 新增设计：非阻塞场景切换

### 问题分析

当前StartGame流程会等待Libraries.Loaded变为true才进入GameScene，这在流式加载模式下会导致长时间等待。

### 解决方案

1. **移除阻塞检查**: 删除SelectScene.StartGame中的Libraries.Loaded检查
2. **异步初始化**: GameScene构造函数中的库初始化改为非阻塞
3. **渐进式渲染**: 所有UI组件和地图渲染都支持资源未加载时的优雅降级

```csharp
// SelectScene.cs - 修改后的StartGame方法
public void StartGame()
{
    // 不再等待Libraries.Loaded
    // 直接发送StartGame请求，让服务器响应后切换场景
    StartGameButton.Enabled = false;
    Network.Enqueue(new C.StartGame
    {
        CharacterIndex = Characters[_selected].Index
    });
}

// 在收到服务器StartGame响应后
public void StartGame(S.StartGame p)
{
    if (p.Result == 4)
    {
        // 异步初始化游戏库（不阻塞）
        Libraries.InitializeForGame();
        
        // 立即切换到GameScene
        ActiveScene = new GameScene();
        Dispose();
    }
}
```

## 新增设计：系统性UI组件定位修复

### 问题分析

当前问题：
1. MirImageControl.Size属性依赖Library.GetTrueSize(Index)
2. 当资源未加载时，GetTrueSize返回Size.Empty (0,0)
3. 对话框在构造函数中计算居中位置时，Size为(0,0)导致位置计算错误
4. 逐个修复对话框不可行，需要系统性解决方案

### 解决方案

在MirImageControl基类中添加DefaultSize属性，当实际Size为(0,0)时使用DefaultSize进行定位计算。

```csharp
// MirImageControl.cs - 新增属性
public class MirImageControl : MirControl
{
    // 新增：默认大小，用于资源未加载时的定位计算
    private Size _defaultSize = Size.Empty;
    public Size DefaultSize
    {
        get { return _defaultSize; }
        set { _defaultSize = value; }
    }
    
    // 修改Size属性
    public override Size Size
    {
        set { base.Size = value; }
        get
        {
            if (AutoSize && Library != null && Index >= 0)
            {
                Size actualSize = Library.GetTrueSize(Index);
                // 如果实际大小为空且有默认大小，使用默认大小
                if (actualSize.IsEmpty && !_defaultSize.IsEmpty)
                    return _defaultSize;
                return actualSize;
            }
            // 如果base.Size为空且有默认大小，使用默认大小
            if (base.Size.IsEmpty && !_defaultSize.IsEmpty)
                return _defaultSize;
            return base.Size;
        }
    }
}
```

### 使用方式

对话框只需设置DefaultSize即可自动处理定位：

```csharp
// 示例：LoginDialog
public LoginDialog()
{
    Index = 1084;
    Library = Libraries.Prguse;
    DefaultSize = new Size(328, 220);  // 设置默认大小
    Location = new Point((Settings.ScreenWidth - Size.Width) / 2,
                         (Settings.ScreenHeight - Size.Height) / 2);
    // ...
}
```

### 需要设置DefaultSize的对话框列表

| 对话框 | DefaultSize |
|--------|-------------|
| LoginDialog | (328, 220) |
| InputKeyDialog | (204, 268) |
| NewAccountDialog | (568, 467) |
| ChangePasswordDialog | (370, 280) |
| NewCharacterDialog | (568, 467) |
| NoticeDialog | (320, 470) |
| RankingDialog | (288, 324) |
| OptionDialog | (352, 412) |
| ChatOptionDialog | (270, 180) |
| ChatNoticeDialog | (260, 100) |
| MirInputBox | (360, 110) |
| MirMessageBox | (360, 150) |
| MirAmountBox | (238, 175) |

## 新增设计：地图资源流式加载

### 问题分析

地图显示黑块的原因：
1. MapLib资源未加载时，Draw方法可能绘制空白或黑色
2. 地图瓦片的BackImage、MiddleImage、FrontImage都依赖MapLib资源
3. 当资源下载中时，没有正确跳过绘制

### 解决方案

1. **修改MLibrary.Draw方法**: 当资源未加载时返回false，不进行绘制
2. **修改DrawFloor方法**: 检查Draw返回值，未绘制时跳过该瓦片
3. **添加FloorValid失效机制**: 当资源下载完成时，设置FloorValid=false触发重绘

```csharp
// MLibrary.cs - 修改Draw方法
public bool Draw(int index, int x, int y)
{
    if (x >= Settings.ScreenWidth || y >= Settings.ScreenHeight)
        return false;

    if (!CheckImage(index))
        return false;  // 资源未加载，返回false

    MImage mi = _images[index];
    if (mi == null || !mi.TextureValid)
        return false;  // 纹理无效，返回false

    if (x + mi.Width < 0 || y + mi.Height < 0)
        return false;

    DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), 
                   new Vector3((float)x, (float)y, 0.0F), Color.White);
    return true;  // 绘制成功
}

// GameScene.MapControl - 修改DrawFloor
private void DrawFloor()
{
    // ... 现有代码 ...
    
    // Back
    if (cell.BackImage != 0 && cell.BackIndex != -1)
    {
        int index = (cell.BackImage & 0x1FFFFFFF) - 1;
        var lib = Libraries.GetMapLib(cell.BackIndex);
        if (lib != null)
        {
            // 如果绘制失败（资源未加载），不影响其他瓦片
            lib.Draw(index, drawX, drawY);
        }
    }
    // ... 其他层同理 ...
}
```

### 资源下载完成后的重绘机制

```csharp
// MLibrary.cs - 在图片下载完成后
private async void DownloadImageAsync(int index, MImage mi)
{
    // ... 下载逻辑 ...
    
    if (downloadSuccess)
    {
        mi.CreateTextureFromData(data);
        mi.DownloadStatus = 2;
        
        // 如果是地图库，通知MapControl重绘
        if (IsMapLibrary)
        {
            MapControl.InvalidateFloor();
        }
    }
}

// GameScene.MapControl - 添加失效方法
public static void InvalidateFloor()
{
    FloorValid = false;
}
```

## Correctness Properties (新增)

### Property 16: Non-Blocking Scene Transition

*For any* StartGame request in streaming mode, the scene transition to GameScene SHALL occur immediately without waiting for resource downloads.

**Validates: Requirements 17.1, 17.4**

### Property 17: DefaultSize Fallback

*For any* MirImageControl with DefaultSize set, when the actual image Size is (0,0), the Size property SHALL return DefaultSize.

**Validates: Requirements 18.1, 18.2, 18.4**

### Property 18: Map Tile Graceful Degradation

*For any* map tile whose resource is not yet loaded, the DrawFloor method SHALL skip drawing that tile without causing visual artifacts (black blocks).

**Validates: Requirements 19.1, 19.4**

## Testing Strategy

### 单元测试

- 验证Settings配置的读写
- 验证ResourceHelper的URL构建
- 验证MImage的占位纹理创建
- 验证下载状态转换逻辑

### 属性测试

使用NUnit + FsCheck进行属性测试，每个属性测试运行至少100次迭代。

测试框架配置：
```csharp
[Property(MaxTest = 100)]
public Property RetryTimingConsistency() { ... }
```

每个属性测试必须标注对应的设计属性编号：
```csharp
// Feature: streaming-resource-loader, Property 1: Retry Timing Consistency
// Validates: Requirements 1.2, 2.3, 3.5
```

### 集成测试

- 模拟资源服务器，测试完整下载流程
- 测试并发下载限制
- 测试断网重连场景
