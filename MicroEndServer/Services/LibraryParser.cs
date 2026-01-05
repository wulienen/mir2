namespace MicroEndServer.Services;

/// <summary>
/// Debug information for path resolution
/// </summary>
public class PathDebugInfo
{
    public string InputPath { get; set; } = string.Empty;
    public string InputName { get; set; } = string.Empty;
    public string DecodedPath { get; set; } = string.Empty;
    public string DecodedName { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ResourcePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool FileExists { get; set; }
}

/// <summary>
/// Library file header response containing total length and header bytes
/// </summary>
public class LibraryHeaderResult
{
    /// <summary>
    /// Total file length in bytes
    /// </summary>
    public long TotalLength { get; set; }
    
    /// <summary>
    /// Header bytes containing version, image count, and index array
    /// </summary>
    public byte[] HeaderBytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Library file parser service for reading .Lib files
/// Parses the standard MLibrary format (version 3)
/// 
/// File structure:
/// - Version (4 bytes, int32) - Version number, current is 3
/// - ImageCount (4 bytes, int32) - Number of images
/// - FrameSeek (4 bytes, int32) - Frame data offset (version >= 3)
/// - IndexList[ImageCount] (int32 each) - File offset for each image
/// - Image Data... - Image data area
/// - Frame Data (if version >= 3) - Frame animation data
/// 
/// Single image data structure (17 byte header + compressed data):
/// - Width (2 bytes, int16)
/// - Height (2 bytes, int16)
/// - X (2 bytes, int16)
/// - Y (2 bytes, int16)
/// - ShadowX (2 bytes, int16)
/// - ShadowY (2 bytes, int16)
/// - Shadow (1 byte) - High bit indicates if there's a Mask layer
/// - Length (4 bytes, int32) - Compressed data length
/// - CompressedData[Length] - GZip compressed ARGB pixel data
/// </summary>
public class LibraryParser
{
    private readonly ILogger<LibraryParser> _logger;
    private readonly string _resourcePath;

    public LibraryParser(IConfiguration configuration, ILogger<LibraryParser> logger)
    {
        _logger = logger;
        _resourcePath = configuration.GetSection("ServerSettings").GetValue<string>("ResourcePath") ?? "./Data/";
    }

    /// <summary>
    /// Get the full file path for a library file
    /// Supports multiple path formats:
    /// - Data/xxx.Lib -> {ResourcePath}/xxx.Lib
    /// - Data_Monster/xxx.Lib -> {ResourcePath}/Monster/xxx.Lib
    /// - Data_Map_WemadeMir2/xxx.Lib -> {ResourcePath}/Map/WemadeMir2/xxx.Lib
    /// </summary>
    public string GetFilePath(string path, string name)
    {
        // Decode URL-encoded path and name
        var decodedPath = Uri.UnescapeDataString(path);
        var decodedName = Uri.UnescapeDataString(name);
        
        _logger.LogDebug("GetFilePath input: path={Path}, name={Name}", path, name);
        _logger.LogDebug("GetFilePath decoded: path={DecodedPath}, name={DecodedName}", decodedPath, decodedName);
        
        // Replace _ with / to restore original path structure
        // Client encodes / as _ for URL safety
        var normalizedPath = decodedPath.Replace('_', '/');
        
        _logger.LogDebug("GetFilePath normalized: {NormalizedPath}", normalizedPath);
        
        // 客户端路径格式: .\Data\xxx.Lib 或 .\Data\Monster\xxx.Lib 或 .\Data\Map\WemadeMir2\xxx.Lib
        // 编码后: path=Data 或 path=Data_Monster 或 path=Data_Map_WemadeMir2, name=xxx.Lib
        // ResourcePath 已经指向 Data 目录，所以需要去掉 path 中的 "Data" 前缀
        
        string relativePath;
        
        // 移除开头的 "Data/" 或 "Data"
        if (normalizedPath.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedPath.Substring(5); // 移除 "Data/"
        }
        else if (normalizedPath.Equals("Data", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = ""; // 文件直接在 Data 目录下
        }
        else
        {
            // 如果路径不以 Data 开头，直接使用原路径
            relativePath = normalizedPath;
        }
        
        string fullPath;
        if (string.IsNullOrEmpty(relativePath))
        {
            fullPath = Path.Combine(_resourcePath, decodedName);
        }
        else
        {
            fullPath = Path.Combine(_resourcePath, relativePath, decodedName);
        }
        
        // Normalize path separators for the current OS
        fullPath = Path.GetFullPath(fullPath);
        
        _logger.LogDebug("GetFilePath result: {FullPath}, exists={Exists}", fullPath, File.Exists(fullPath));
        
        return fullPath;
    }
    
    /// <summary>
    /// Get debug information about path resolution
    /// </summary>
    public PathDebugInfo GetPathDebugInfo(string path, string name)
    {
        var decodedPath = Uri.UnescapeDataString(path);
        var decodedName = Uri.UnescapeDataString(name);
        var normalizedPath = decodedPath.Replace('_', '/');
        
        string relativePath;
        if (normalizedPath.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedPath.Substring(5);
        }
        else if (normalizedPath.Equals("Data", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = "";
        }
        else
        {
            relativePath = normalizedPath;
        }
        
        string fullPath;
        if (string.IsNullOrEmpty(relativePath))
        {
            fullPath = Path.Combine(_resourcePath, decodedName);
        }
        else
        {
            fullPath = Path.Combine(_resourcePath, relativePath, decodedName);
        }
        
        fullPath = Path.GetFullPath(fullPath);
        
        return new PathDebugInfo
        {
            InputPath = path,
            InputName = name,
            DecodedPath = decodedPath,
            DecodedName = decodedName,
            NormalizedPath = normalizedPath,
            RelativePath = relativePath,
            ResourcePath = _resourcePath,
            FullPath = fullPath,
            FileExists = File.Exists(fullPath)
        };
    }

    /// <summary>
    /// Parse library file header to extract image count and index information
    /// </summary>
    /// <param name="filePath">Full path to the library file</param>
    /// <returns>LibraryHeaderResult containing TotalLength and HeaderBytes, or null if parsing fails</returns>
    public LibraryHeaderResult? ParseHeader(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Library file not found: {FilePath}", filePath);
            return null;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Read version
            int version = reader.ReadInt32();
            if (version < 2)
            {
                _logger.LogWarning("Unsupported library version: {Version} in {FilePath}", version, filePath);
                return null;
            }

            // Read image count
            int imageCount = reader.ReadInt32();

            // Read frame seek position (version >= 3)
            int frameSeek = 0;
            if (version >= 3)
            {
                frameSeek = reader.ReadInt32();
            }

            // Calculate header length:
            // - 4 bytes version
            // - 4 bytes image count
            // - 4 bytes frame seek (if version >= 3)
            // - 4 * imageCount bytes for index array
            int headerLength = 4 + 4 + (version >= 3 ? 4 : 0) + (4 * imageCount);

            // Read the complete header
            fs.Seek(0, SeekOrigin.Begin);
            byte[] headerBytes = reader.ReadBytes(headerLength);

            return new LibraryHeaderResult
            {
                TotalLength = fs.Length,
                HeaderBytes = headerBytes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing library header: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Get image data for a specific index from the library file
    /// </summary>
    /// <param name="filePath">Full path to the library file</param>
    /// <param name="index">Image index</param>
    /// <returns>Image binary data (17 byte header + compressed data), or null if not found</returns>
    public byte[]? GetImageData(string filePath, int index)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Library file not found: {FilePath}", filePath);
            return null;
        }

        try
        {
            var offsetInfo = GetImageOffset(filePath, index);
            if (offsetInfo == null)
            {
                return null;
            }

            var (offset, length) = offsetInfo.Value;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            // Seek to image position
            fs.Seek(offset, SeekOrigin.Begin);
            
            // Read image data (header + compressed data)
            byte[] imageData = new byte[length];
            int bytesRead = fs.Read(imageData, 0, length);
            
            if (bytesRead != length)
            {
                _logger.LogWarning("Could not read complete image data for index {Index} in {FilePath}", index, filePath);
                return null;
            }

            return imageData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading image data for index {Index} in {FilePath}", index, filePath);
            return null;
        }
    }

    /// <summary>
    /// Calculate the byte offset and length for an image in the library file
    /// </summary>
    /// <param name="filePath">Full path to the library file</param>
    /// <param name="index">Image index</param>
    /// <returns>Tuple of (offset, length) or null if invalid</returns>
    private (int offset, int length)? GetImageOffset(string filePath, int index)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Read version
            int version = reader.ReadInt32();
            if (version < 2)
            {
                return null;
            }

            // Read image count
            int imageCount = reader.ReadInt32();
            
            if (index < 0 || index >= imageCount)
            {
                _logger.LogWarning("Image index {Index} out of range (0-{MaxIndex}) in {FilePath}", index, imageCount - 1, filePath);
                return null;
            }

            // Skip frame seek (version >= 3)
            if (version >= 3)
            {
                reader.ReadInt32();
            }

            // Read index array to get the offset for the requested image
            int[] indexList = new int[imageCount];
            for (int i = 0; i < imageCount; i++)
            {
                indexList[i] = reader.ReadInt32();
            }

            int imageOffset = indexList[index];
            if (imageOffset == 0)
            {
                // Empty image slot
                return null;
            }

            // Seek to image position and read header to get length
            fs.Seek(imageOffset, SeekOrigin.Begin);
            
            // Read image header (17 bytes)
            // Width (2) + Height (2) + X (2) + Y (2) + ShadowX (2) + ShadowY (2) + Shadow (1) + Length (4) = 17 bytes
            reader.ReadInt16(); // Width
            reader.ReadInt16(); // Height
            reader.ReadInt16(); // X
            reader.ReadInt16(); // Y
            reader.ReadInt16(); // ShadowX
            reader.ReadInt16(); // ShadowY
            byte shadow = reader.ReadByte(); // Shadow (high bit = has mask)
            int compressedLength = reader.ReadInt32(); // Compressed data length

            // Check if there's a mask layer (high bit of shadow)
            bool hasMask = (shadow >> 7) == 1;
            
            // Calculate total length: 17 byte header + compressed data
            int totalLength = 17 + compressedLength;
            
            // If has mask, we need to include mask data as well
            if (hasMask)
            {
                // Skip compressed data to read mask header
                fs.Seek(imageOffset + 17 + compressedLength, SeekOrigin.Begin);
                
                // Mask header: Width (2) + Height (2) + X (2) + Y (2) + Length (4) = 12 bytes
                reader.ReadInt16(); // MaskWidth
                reader.ReadInt16(); // MaskHeight
                reader.ReadInt16(); // MaskX
                reader.ReadInt16(); // MaskY
                int maskLength = reader.ReadInt32(); // Mask compressed data length
                
                totalLength += 12 + maskLength;
            }

            return (imageOffset, totalLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating image offset for index {Index} in {FilePath}", index, filePath);
            return null;
        }
    }
}
