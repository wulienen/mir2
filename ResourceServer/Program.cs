using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ResourceServer
{
    class Program
    {
        private static TcpListener _listener;
        private static string _resourcePath;
        private static bool _isRunning;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Resource Server Starting...");
            
            _resourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
            if (!Directory.Exists(_resourcePath))
                Directory.CreateDirectory(_resourcePath);

            _listener = new TcpListener(IPAddress.Any, 8000);
            _listener.Start();
            _isRunning = true;

            Console.WriteLine($"Resource Server Started on port 8000");
            Console.WriteLine($"Resource Path: {_resourcePath}");

            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting client: {ex}");
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new BinaryReader(stream);
                using var writer = new BinaryWriter(stream);

                while (client.Connected)
                {
                    // 读取请求
                    var resourceName = reader.ReadString();
                    var requestType = (ResourceRequestType)reader.ReadInt32();

                    Console.WriteLine($"Received request for resource: {resourceName}");

                    // 处理请求
                    var response = new ResourceResponse();
                    var resourcePath = Path.Combine(_resourcePath, resourceName);

                    if (File.Exists(resourcePath))
                    {
                        response.Success = true;
                        response.Data = await File.ReadAllBytesAsync(resourcePath);
                    }
                    else
                    {
                        response.Success = false;
                        response.ErrorMessage = $"Resource not found: {resourceName}";
                    }

                    // 发送响应
                    writer.Write(response.Success);
                    if (response.Success)
                    {
                        writer.Write(response.Data.Length);
                        writer.Write(response.Data);
                    }
                    else
                    {
                        writer.Write(response.ErrorMessage);
                    }
                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex}");
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    public enum ResourceRequestType
    {
        Load,
        CheckUpdate
    }

    public class ResourceResponse
    {
        public bool Success { get; set; }
        public byte[] Data { get; set; }
        public string ErrorMessage { get; set; }
    }
}