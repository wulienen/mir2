using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Client.MirNetwork;

namespace Client
{
    public class ResourceManager
    {
        private static ResourceManager _instance;
        public static ResourceManager Instance => _instance ??= new ResourceManager();

        private readonly ConcurrentDictionary<string, byte[]> _resourceCache;
        private readonly string _resourcePath;
        private TcpClient _resourceClient;
        private NetworkStream _resourceStream;
        private bool _isConnected;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, Task<byte[]>> _loadingTasks = new();

        public event EventHandler<string> OnResourceLoaded;
        public event EventHandler<string> OnResourceLoadFailed;

        private ResourceManager()
        {
            _resourceCache = new ConcurrentDictionary<string, byte[]>();
            _resourcePath = Application.StartupPath;
        }

        public async Task<bool> ConnectToResourceServer(string host, int port)
        {
            try
            {
                await _connectionSemaphore.WaitAsync();
                if (_isConnected) return true;

                _resourceClient = new TcpClient();
                await _resourceClient.ConnectAsync(host, port);
                _resourceStream = _resourceClient.GetStream();
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Resource server connection failed: {ex}");
                return false;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task<byte[]> LoadResource(string resourceName, bool forceReload = false)
        {
            try
            {
                // 检查缓存
                if (!forceReload && _resourceCache.TryGetValue(resourceName, out var cachedData))
                    return cachedData;

                // 检查是否正在加载
                if (_loadingTasks.TryGetValue(resourceName, out var loadingTask))
                    return await loadingTask;

                // 创建新的加载任务
                var task = Task.Run(async () =>
                {
                    try
                    {
                        // 检查本地文件
                        string localPath = Path.Combine(_resourcePath, resourceName);
                        if (File.Exists(localPath))
                        {
                            var data = await File.ReadAllBytesAsync(localPath);
                            _resourceCache[resourceName] = data;
                            OnResourceLoaded?.Invoke(this, resourceName);
                            return data;
                        }

                        // 从服务器加载
                        if (!_isConnected)
                        {
                            CMain.SaveError($"[ResourceManager] 未连接资源服务器，无法加载: {resourceName}");
                            OnResourceLoadFailed?.Invoke(this, resourceName);
                            return null;
                        }

                        // 发送资源请求
                        var request = new ResourceRequest
                        {
                            ResourceName = resourceName,
                            RequestType = ResourceRequestType.Load
                        };

                        await SendRequest(request);

                        // 接收资源数据
                        var response = await ReceiveResponse();
                        if (response.Success)
                        {
                            // 保存到本地
                            var directory = Path.GetDirectoryName(localPath);
                            if (!Directory.Exists(directory))
                                Directory.CreateDirectory(directory);
                            try
                            {
                                await File.WriteAllBytesAsync(localPath, response.Data);
                            }
                            catch (Exception ex)
                            {
                                CMain.SaveError($"[ResourceManager] 写入本地缓存失败: {localPath}, 错误: {ex}");
                            }
                            _resourceCache[resourceName] = response.Data;
                            OnResourceLoaded?.Invoke(this, resourceName);
                            return response.Data;
                        }
                        else
                        {
                            CMain.SaveError($"[ResourceManager] 资源服务器返回失败: {resourceName}, 错误: {response.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        CMain.SaveError($"[ResourceManager] 加载资源异常: {resourceName}, 错误: {ex}");
                    }

                    OnResourceLoadFailed?.Invoke(this, resourceName);
                    return null;
                });

                _loadingTasks[resourceName] = task;
                var result = await task;
                _loadingTasks.TryRemove(resourceName, out _);
                return result;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"[ResourceManager] LoadResource方法异常: {resourceName}, 错误: {ex}");
                OnResourceLoadFailed?.Invoke(this, resourceName);
                return null;
            }
        }

        private async Task SendRequest(ResourceRequest request)
        {
            if (!_isConnected) return;

            var data = request.Serialize();
            await _resourceStream.WriteAsync(data, 0, data.Length);
        }

        private async Task<ResourceResponse> ReceiveResponse()
        {
            if (!_isConnected) return null;

            var buffer = new byte[4096];
            var bytesRead = await _resourceStream.ReadAsync(buffer, 0, buffer.Length);
            return ResourceResponse.Deserialize(buffer, 0, bytesRead);
        }

        public void Dispose()
        {
            _resourceStream?.Dispose();
            _resourceClient?.Dispose();
            _connectionSemaphore?.Dispose();
            _isConnected = false;
        }
 
    }

    public enum ResourceRequestType
    {
        Load,
        CheckUpdate
    }

    public class ResourceRequest
    {
        public string ResourceName { get; set; }
        public ResourceRequestType RequestType { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            writer.Write(ResourceName);
            writer.Write((int)RequestType);
            
            return ms.ToArray();
        }
    }

    public class ResourceResponse
    {
        public bool Success { get; set; }
        public byte[] Data { get; set; }
        public string ErrorMessage { get; set; }

        public static ResourceResponse Deserialize(byte[] data, int offset, int length)
        {
            using var ms = new MemoryStream(data, offset, length);
            using var reader = new BinaryReader(ms);

            var response = new ResourceResponse
            {
                Success = reader.ReadBoolean()
            };

            if (response.Success)
            {
                var dataLength = reader.ReadInt32();
                response.Data = reader.ReadBytes(dataLength);
            }
            else
            {
                response.ErrorMessage = reader.ReadString();
            }

            return response;
        }
    }
}