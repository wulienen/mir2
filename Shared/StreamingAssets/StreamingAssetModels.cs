using System.Text.Json.Serialization;

namespace Shared.StreamingAssets;

public static class StreamingAssetConstants
{
    public const string ManifestFileName = "manifest.json";
    public const string LibrariesDirectory = "libraries";
    public const string LibraryImagesDirectory = "images";
    public const string MapsDirectory = "maps";
    public const string MapChunksDirectory = "chunks";
    public const string SoundsDirectory = "sounds";
    public const int CurrentFormatVersion = 1;
    public const int DefaultMapChunkSize = 32;

    /// <summary>图片数量超过此阈值的图库将被拆分为分页清单，避免客户端全量下载。</summary>
    public const int LibraryManifestPageThreshold = 5000;
    /// <summary>每页包含的图片记录数。</summary>
    public const int LibraryManifestDefaultPageSize = 4000;
    /// <summary>页面文件名模板，{0} 为页码（从 0 开始）。</summary>
    public const string LibraryManifestPagePattern = "manifest_{0}.json";
}

public sealed class AssetManifest
{
    public int FormatVersion { get; set; } = StreamingAssetConstants.CurrentFormatVersion;
    public string Version { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int MapChunkSize { get; set; } = StreamingAssetConstants.DefaultMapChunkSize;
    public List<AssetLibraryRecord> Libraries { get; set; } = new();
    public List<AssetMapRecord> Maps { get; set; } = new();
    public List<AssetSoundRecord> Sounds { get; set; } = new();
}

public sealed class AssetLibraryRecord
{
    public string Id { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

public sealed class AssetMapRecord
{
    public string Id { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

public sealed class AssetSoundRecord
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

public sealed class LibraryManifest
{
    public int FormatVersion { get; set; } = StreamingAssetConstants.CurrentFormatVersion;
    public string Id { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public List<LibraryImageRecord> Images { get; set; } = new();
    public List<LibraryFrameRecord> Frames { get; set; } = new();

    /// <summary>每页记录数。非空时表示本清单为分页模式，Images 列表为空。</summary>
    public int? PageSize { get; set; }
    /// <summary>分页索引。非空时每条记录描述一个页面文件。</summary>
    public List<LibraryManifestPageRecord>? Pages { get; set; }

    [JsonIgnore]
    public bool IsPaged => PageSize.HasValue && Pages != null;
}

/// <summary>根清单中对单个页面文件的轻量描述。</summary>
public sealed class LibraryManifestPageRecord
{
    public int PageIndex { get; set; }
    /// <summary>本页第一条记录对应的图片 index。</summary>
    public int Start { get; set; }
    /// <summary>本页实际包含的记录数。</summary>
    public int Count { get; set; }
    /// <summary>相对于图库目录的文件路径，例如 "manifest_0.json"。</summary>
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

/// <summary>单个页面文件的完整内容。</summary>
public sealed class LibraryManifestPage
{
    public int FormatVersion { get; set; } = StreamingAssetConstants.CurrentFormatVersion;
    public int PageIndex { get; set; }
    /// <summary>本页第一条记录对应的图片 index，等于 PageIndex * PageSize。</summary>
    public int Start { get; set; }
    public List<LibraryImageRecord> Images { get; set; } = new();
}

public sealed class LibraryImageRecord
{
    public int Index { get; set; }
    public string Path { get; set; } = string.Empty;
    public short Width { get; set; }
    public short Height { get; set; }
    public short X { get; set; }
    public short Y { get; set; }
    public short ShadowX { get; set; }
    public short ShadowY { get; set; }
    public byte Shadow { get; set; }
    public int Length { get; set; }
    public bool HasMask { get; set; }
    public short MaskWidth { get; set; }
    public short MaskHeight { get; set; }
    public short MaskX { get; set; }
    public short MaskY { get; set; }
    public int MaskLength { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long FileLength { get; set; }
}

public sealed class LibraryFrameRecord
{
    public byte Action { get; set; }
    public int Start { get; set; }
    public int Count { get; set; }
    public int Skip { get; set; }
    public int Interval { get; set; }
    public int EffectStart { get; set; }
    public int EffectCount { get; set; }
    public int EffectSkip { get; set; }
    public int EffectInterval { get; set; }
    public bool Reverse { get; set; }
    public bool Blend { get; set; }
}

public sealed class MapManifest
{
    public int FormatVersion { get; set; } = StreamingAssetConstants.CurrentFormatVersion;
    public string Id { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int ChunkSize { get; set; } = StreamingAssetConstants.DefaultMapChunkSize;
    public List<MapChunkRecord> Chunks { get; set; } = new();
}

public sealed class MapChunkRecord
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }

    [JsonIgnore]
    public string Key => $"{X}_{Y}";
}

public sealed class StreamingMapChunk
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public StreamingMapCell[] Cells { get; set; } = Array.Empty<StreamingMapCell>();
}

public struct StreamingMapCell
{
    public short BackIndex;
    public int BackImage;
    public short MiddleIndex;
    public int MiddleImage;
    public short FrontIndex;
    public int FrontImage;
    public byte DoorIndex;
    public byte DoorOffset;
    public byte FrontAnimationFrame;
    public byte FrontAnimationTick;
    public byte MiddleAnimationFrame;
    public byte MiddleAnimationTick;
    public short TileAnimationImage;
    public short TileAnimationOffset;
    public byte TileAnimationFrames;
    public byte Light;
    public byte Unknown;
    public bool FishingCell;
}
