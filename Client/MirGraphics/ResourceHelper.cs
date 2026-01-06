using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;

namespace Client.MirGraphics
{
    /// <summary>
    /// 流式资源加载助手类
    /// 负责与资源服务器通信，管理下载队列和本地缓存合并
    /// </summary>
    public static class ResourceHelper
    {
        private static HttpClient httpClient;
        
        /// <summary>
        /// 资源服务器是否在线
        /// </summary>
        public static bool ServerActive { get; private set; }
        
        /// <summary>
        /// 服务器状态变化事件
        /// </summary>
        public static event Action<bool> ServerActiveChanged;
        
        /// <summary>
        /// 并发下载信号量，限制最大并发数
        /// </summary>
        public static readonly SemaphoreSlim Semaphore;
        
        /// <summary>
        /// 最大并发下载数
        /// </summary>
        private const int MaxConcurrentDownloads = 10;
        
        /// <summary>
        /// 待写入队列，按文件名分组
        /// </summary>
        private static readonly ConcurrentDictionary<string, List<PendingImageWrite>> PendingWrites;
        
        /// <summary>
        /// 网络异常计数器
        /// </summary>
        private static int ExceptionCount;
        
        /// <summary>
        /// 下次重试时间
        /// </summary>
        private static DateTime RetryTime;
        
        /// <summary>
        /// 写入操作锁
        /// </summary>
        private static readonly object WriteLock = new object();
        
        /// <summary>
        /// 是否正在处理写入
        /// </summary>
        private static bool _isProcessingWrites;
        
        /// <summary>
        /// HttpClient是否已初始化
        /// </summary>
        private static bool _httpClientInitialized;

        static ResourceHelper()
        {
            Semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
            PendingWrites = new ConcurrentDictionary<string, List<PendingImageWrite>>();
        }
        
        /// <summary>
        /// 确保HttpClient已初始化（延迟初始化，等待Settings加载完成）
        /// </summary>
        private static void EnsureHttpClientInitialized()
        {
            if (_httpClientInitialized) return;
            
            lock (WriteLock)
            {
                if (_httpClientInitialized) return;
                
                httpClient = new HttpClient();
                httpClient.BaseAddress = new Uri($"http://{Settings.MicroClientIP}:{Settings.MicroClientPort}/api/");
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                
                _httpClientInitialized = true;
            }
        }

        /// <summary>
        /// 检查资源服务器是否在线
        /// </summary>
        /// <returns>服务器是否可用</returns>
        public static bool CheckServerOnline()
        {
            EnsureHttpClientInitialized();
            
            bool wasActive = ServerActive;
            try
            {
                // 发送ping请求检查服务器状态
                using (HttpResponseMessage response = httpClient.GetAsync("ping", HttpCompletionOption.ResponseHeadersRead).Result)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        ServerActive = true;
                        ExceptionCount = 0;
                        
                        // 触发状态变化事件
                        if (!wasActive)
                            ServerActiveChanged?.Invoke(true);
                        
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略异常，返回false
            }
            
            ServerActive = false;
            
            // 触发状态变化事件
            if (wasActive)
                ServerActiveChanged?.Invoke(false);
            
            return false;
        }

        /// <summary>
        /// 检查并重试服务器连接
        /// </summary>
        public static void CheckAndRetryConnection()
        {
            if ((!ServerActive || ExceptionCount >= 5) && DateTime.Now > RetryTime)
            {
                ExceptionCount = 0;
                RetryTime = DateTime.Now.AddSeconds(15);
                CheckServerOnline();
            }
        }

        /// <summary>
        /// 获取库文件头信息并创建本地文件
        /// </summary>
        /// <param name="fileName">库文件路径</param>
        /// <returns>是否成功</returns>
        public static async Task<bool> GetHeaderAsync(string fileName)
        {
            var realName = fileName;
            fileName = fileName.Replace("./", "");
            fileName = fileName.Replace(".\\", "");
            fileName = fileName.Replace("\\", "/");
            
            var name = Path.GetFileName(fileName);
            var path = fileName.Replace("/" + name, "");
            name = HttpUtility.UrlPathEncode(name);
            path = HttpUtility.UrlPathEncode(path.Replace('/', '_'));
            
            var api = $"libheader/{path}/{name}";
            
            try
            {
                var result = await HttpClientGetAsync(api);
                if (result != null && result.Length > 0)
                {
                    using (result)
                    using (BinaryReader br = new BinaryReader(result))
                    {
                        // 读取总长度
                        int totalLength = br.ReadInt32();
                        // 读取头部字节长度
                        int headerLength = br.ReadInt32();
                        
                        // 验证数据有效性
                        if (totalLength <= 0)
                        {
                            LogError($"素材：{api},下载资源失败,原因：服务端返回的文件总长度无效 ({totalLength})");
                            return false;
                        }
                        
                        if (headerLength <= 0 || headerLength > result.Length - 8)
                        {
                            LogError($"素材：{api},下载资源失败,原因：服务端返回的头部长度无效 ({headerLength})");
                            return false;
                        }
                        
                        // 读取头部字节
                        byte[] headerBytes = br.ReadBytes(headerLength);
                        
                        if (headerBytes == null || headerBytes.Length == 0)
                        {
                            LogError($"素材：{api},下载资源失败,原因：服务端返回字节数异常");
                            return false;
                        }
                        
                        // 验证头部数据的基本结构（至少需要8字节：version + count）
                        if (headerBytes.Length < 8)
                        {
                            LogError($"素材：{api},下载资源失败,原因：头部数据太小 ({headerBytes.Length} bytes)");
                            return false;
                        }
                        
                        // 验证版本号（应该是2或3）
                        int version = BitConverter.ToInt32(headerBytes, 0);
                        if (version < 2 || version > 10)
                        {
                            LogError($"素材：{api},下载资源失败,原因：无效的版本号 ({version})");
                            return false;
                        }
                        
                        // 验证图片数量
                        int imageCount = BitConverter.ToInt32(headerBytes, 4);
                        if (imageCount < 0 || imageCount > 100000)
                        {
                            LogError($"素材：{api},下载资源失败,原因：无效的图片数量 ({imageCount})");
                            return false;
                        }
                        
                        // 直接使用原始文件名（相对路径）
                        // realName 已经是相对于客户端目录的路径，如 .\Data\ChrSel.Lib
                        var fullname = realName;
                        var dir = Path.GetDirectoryName(fullname);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        
                        // 使用临时文件，只在写入成功后才重命名为目标文件
                        // 这样可以避免创建损坏的文件
                        var tempFile = fullname + ".tmp";
                        
                        try
                        {
                            // 写入临时文件
                            using (FileStream stream = File.Create(tempFile))
                            {
                                stream.Write(headerBytes, 0, headerBytes.Length);
                            }
                            
                            // 删除可能存在的旧文件
                            if (File.Exists(fullname))
                            {
                                try { File.Delete(fullname); } catch { }
                            }
                            
                            // 重命名临时文件为目标文件
                            File.Move(tempFile, fullname);
                            return true;
                        }
                        catch (Exception writeEx)
                        {
                            // 写入失败，清理临时文件
                            try { File.Delete(tempFile); } catch { }
                            LogError($"素材：{api},写入文件失败,异常原因{writeEx.Message}");
                            return false;
                        }
                    }
                }
                else
                {
                    LogError($"素材：{api},下载资源失败,原因：服务端返回字节数为0");
                }
            }
            catch (Exception ex)
            {
                LogError($"素材：{api},下载资源异常,异常原因{ex.Message}");
            }
            
            return false;
        }

        /// <summary>
        /// 获取图片数据（包含17字节头信息和压缩数据）
        /// </summary>
        /// <param name="fileName">库文件路径</param>
        /// <param name="index">图片索引</param>
        /// <param name="position">图片在文件中的位置（索引位置，不是数据位置）</param>
        /// <param name="compressedLength">压缩数据长度（不含17字节头）</param>
        /// <returns>ImageDownloadResult 包含图片头信息和压缩数据</returns>
        public static async Task<ImageDownloadResult> GetImageAsync(string fileName, int index, int position, int compressedLength)
        {
            var realName = fileName;
            fileName = fileName.Replace("./", "");
            fileName = fileName.Replace(".\\", "");
            fileName = fileName.Replace("\\", "/");
            
            var name = Path.GetFileName(fileName);
            var path = fileName.Replace("/" + name, "");
            name = HttpUtility.UrlPathEncode(name);
            path = HttpUtility.UrlPathEncode(path.Replace('/', '_'));
            
            var api = $"libimage/{path}/{name}/{index}";
            
            try
            {
                // 使用信号量控制并发
                await Semaphore.WaitAsync();
                
                try
                {
                    var result = await HttpClientGetAsync(api);
                    if (result != null && result.Length > 0)
                    {
                        using (result)
                        using (BinaryReader br = new BinaryReader(result))
                        {
                            // 读取位置（服务端返回的占位符，不使用）
                            int imagePosition = br.ReadInt32();
                            // 读取数据长度（服务端返回的完整图片数据长度：17字节头 + 压缩数据）
                            int dataLength = br.ReadInt32();
                            // 读取完整图片数据（17字节头 + 压缩数据）
                            byte[] fullImageData = br.ReadBytes(dataLength);
                            
                            if (fullImageData == null || fullImageData.Length < 17)
                            {
                                LogError($"素材：{api},下载资源失败,原因：服务端返回数据长度异常 {fullImageData?.Length ?? 0}");
                                return null;
                            }
                            
                            // 解析17字节头信息
                            short width = BitConverter.ToInt16(fullImageData, 0);
                            short height = BitConverter.ToInt16(fullImageData, 2);
                            short x = BitConverter.ToInt16(fullImageData, 4);
                            short y = BitConverter.ToInt16(fullImageData, 6);
                            short shadowX = BitConverter.ToInt16(fullImageData, 8);
                            short shadowY = BitConverter.ToInt16(fullImageData, 10);
                            byte shadow = fullImageData[12];
                            int length = BitConverter.ToInt32(fullImageData, 13);
                            
                            // 将完整数据添加到待写入队列（写入到索引位置，包含17字节头）
                            var pendingWrite = new PendingImageWrite
                            {
                                Position = position,  // 索引位置（图片头开始位置）
                                Data = fullImageData  // 完整数据（17字节头 + 压缩数据）
                            };
                            
                            if (!PendingWrites.TryGetValue(realName, out var list))
                            {
                                list = new List<PendingImageWrite> { pendingWrite };
                                PendingWrites[realName] = list;
                            }
                            else
                            {
                                lock (list)
                                {
                                    list.Add(pendingWrite);
                                }
                            }
                            
                            // 返回包含头信息和压缩数据的结果
                            if (fullImageData.Length > 17)
                            {
                                byte[] compressedData = new byte[fullImageData.Length - 17];
                                Array.Copy(fullImageData, 17, compressedData, 0, compressedData.Length);
                                
                                return new ImageDownloadResult
                                {
                                    Width = width,
                                    Height = height,
                                    X = x,
                                    Y = y,
                                    ShadowX = shadowX,
                                    ShadowY = shadowY,
                                    Shadow = shadow,
                                    Length = length,
                                    CompressedData = compressedData
                                };
                            }
                            
                            return null;
                        }
                    }
                    else
                    {
                        LogError($"素材：{api},下载资源失败,原因：服务端返回字节数为0");
                    }
                }
                finally
                {
                    Semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                LogError($"素材：{api},下载资源异常,异常原因{ex.Message}");
                ExceptionCount++;
                
                // 当异常计数达到5次，标记服务器不可用
                if (ExceptionCount >= 5)
                {
                    ServerActive = false;
                }
            }
            
            return null;
        }

        /// <summary>
        /// 处理待写入队列，批量写入数据到本地文件
        /// </summary>
        public static void ProcessPendingWrites()
        {
            if (_isProcessingWrites)
                return;
            
            lock (WriteLock)
            {
                if (_isProcessingWrites)
                    return;
                
                _isProcessingWrites = true;
                
                try
                {
                    foreach (var kvp in PendingWrites)
                    {
                        var fileName = kvp.Key;
                        var writes = kvp.Value;
                        
                        if (writes == null || writes.Count == 0)
                            continue;
                        
                        // fileName 已经是相对路径，如 .\Data\ChrSel.Lib
                        var fullPath = fileName;
                        if (!File.Exists(fullPath))
                            continue;
                        
                        List<PendingImageWrite> toProcess;
                        lock (writes)
                        {
                            if (writes.Count == 0)
                                continue;
                            
                            toProcess = new List<PendingImageWrite>(writes);
                            writes.Clear();
                        }
                        
                        try
                        {
                            // 使用 FileShare.ReadWrite 允许 MLibrary 同时读取文件
                            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                            {
                                foreach (var write in toProcess)
                                {
                                    // 计算写入结束位置
                                    long endPosition = write.Position + write.Data.Length;
                                    
                                    // 如果写入位置超出当前文件大小，扩展文件
                                    if (endPosition > stream.Length)
                                    {
                                        stream.SetLength(endPosition);
                                    }
                                    
                                    stream.Seek(write.Position, SeekOrigin.Begin);
                                    stream.Write(write.Data, 0, write.Data.Length);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError($"写入文件失败：{fullPath}, 异常：{ex.Message}");
                        }
                    }
                }
                finally
                {
                    _isProcessingWrites = false;
                }
            }
        }

        /// <summary>
        /// HTTP GET请求
        /// </summary>
        /// <param name="url">请求URL</param>
        /// <returns>响应数据流</returns>
        private static async Task<MemoryStream> HttpClientGetAsync(string url)
        {
            EnsureHttpClientInitialized();
            
            try
            {
                using (HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        LogError($"素材：{url},下载资源异常,异常原因{response.StatusCode}");
                        return null;
                    }
                    
                    using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        long totalBytes = response.Content.Headers.ContentLength ?? -1;
                        MemoryStream ms = new MemoryStream();
                        await contentStream.CopyToAsync(ms);
                        ms.Seek(0, SeekOrigin.Begin);
                        
                        if (totalBytes < 0 || ms.Length == totalBytes)
                            return ms;
                        else
                        {
                            LogError($"素材：{url},下载资源失败,原因：服务端返回字节数异常");
                            ms.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"素材：{url},下载资源异常,异常原因{ex.Message}");
                ExceptionCount++;
            }
            
            return null;
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        /// <param name="message">错误消息</param>
        private static void LogError(string message)
        {
            try
            {
                CMain.SaveError(message);
            }
            catch
            {
                // 忽略日志错误
            }
        }
    }

    /// <summary>
    /// 待写入的图片数据
    /// </summary>
    public class PendingImageWrite
    {
        /// <summary>
        /// 在文件中的写入位置
        /// </summary>
        public int Position { get; set; }
        
        /// <summary>
        /// 图片数据
        /// </summary>
        public byte[] Data { get; set; }
    }

    /// <summary>
    /// 图片下载结果，包含头信息和压缩数据
    /// </summary>
    public class ImageDownloadResult
    {
        /// <summary>
        /// 图片宽度
        /// </summary>
        public short Width { get; set; }
        
        /// <summary>
        /// 图片高度
        /// </summary>
        public short Height { get; set; }
        
        /// <summary>
        /// X偏移量
        /// </summary>
        public short X { get; set; }
        
        /// <summary>
        /// Y偏移量
        /// </summary>
        public short Y { get; set; }
        
        /// <summary>
        /// 阴影X偏移量
        /// </summary>
        public short ShadowX { get; set; }
        
        /// <summary>
        /// 阴影Y偏移量
        /// </summary>
        public short ShadowY { get; set; }
        
        /// <summary>
        /// 阴影标志（高位表示是否有Mask层）
        /// </summary>
        public byte Shadow { get; set; }
        
        /// <summary>
        /// 压缩数据长度
        /// </summary>
        public int Length { get; set; }
        
        /// <summary>
        /// 压缩数据
        /// </summary>
        public byte[] CompressedData { get; set; }
    }
}
