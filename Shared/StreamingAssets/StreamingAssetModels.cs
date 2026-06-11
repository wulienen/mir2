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
