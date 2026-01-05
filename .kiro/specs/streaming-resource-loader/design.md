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
