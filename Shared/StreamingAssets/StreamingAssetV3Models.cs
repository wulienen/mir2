namespace Shared.StreamingAssets;

public static class StreamingAssetV3Constants
{
    public const int FormatVersion = 3;
    public const int DefaultMapChunkSize = 32;
    public const int LibraryIndexMagic = 0x33494C59; // YLI3
    public const int MapIndexMagic = 0x33494D59; // YMI3
    public const string ManifestFileName = "manifest.json";
    public const string CatalogsDirectory = "catalogs";
    public const string LibrariesDirectory = "libraries";
    public const string MapsDirectory = "maps";
    public const string SoundsDirectory = "sounds";
}

public sealed class StreamingAssetV3Manifest
{
    public int FormatVersion { get; set; } = StreamingAssetV3Constants.FormatVersion;
    public string Version { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string CatalogHash { get; set; } = string.Empty;
    public long CatalogLength { get; set; }
    public List<V3LibraryRecord> Libraries { get; set; } = new();
    public List<V3MapRecord> Maps { get; set; } = new();
    public List<V3SoundRecord> Sounds { get; set; } = new();
}

public sealed class V3LibraryRecord
{
    public string Id { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public long FileLength { get; set; }
    public long CatalogOffset { get; set; }
    public int CatalogLength { get; set; }
    public string CatalogBlockHash { get; set; } = string.Empty;
}

public sealed class V3MapRecord
{
    public string Id { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int ChunkSize { get; set; } = StreamingAssetV3Constants.DefaultMapChunkSize;
    public string FileHash { get; set; } = string.Empty;
    public long FileLength { get; set; }
    public long CatalogOffset { get; set; }
    public int CatalogLength { get; set; }
    public string CatalogBlockHash { get; set; } = string.Empty;
}

public sealed class V3SoundRecord
{
    public string Id { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Extension { get; set; } = string.Empty;
}

public sealed class V3LibraryIndex
{
    public string Id { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public List<V3LibraryImageRecord> Images { get; set; } = new();
    public List<LibraryFrameRecord> Frames { get; set; } = new();
}

public sealed class V3LibraryImageRecord
{
    public int Index { get; set; }
    public long Offset { get; set; } = -1;
    public int Length { get; set; }
    public short Width { get; set; }
    public short Height { get; set; }
    public short X { get; set; }
    public short Y { get; set; }
    public short ShadowX { get; set; }
    public short ShadowY { get; set; }
    public byte Shadow { get; set; }
    public short TrueWidth { get; set; }
    public short TrueHeight { get; set; }
    public string Hash { get; set; } = string.Empty;

    public bool Exists => Offset >= 0 && Length > 0 && StreamingAssetIO.IsValidSha256(Hash);
}

public sealed class V3MapIndex
{
    public string Id { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int ChunkSize { get; set; } = StreamingAssetV3Constants.DefaultMapChunkSize;
    public List<V3MapChunkRecord> Chunks { get; set; } = new();
}

public sealed class V3MapChunkRecord
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public int UncompressedLength { get; set; }
    public string Hash { get; set; } = string.Empty;

    public string Key => $"{X}_{Y}";
}

public sealed class V3LibraryImagePayload
{
    public short Width { get; set; }
    public short Height { get; set; }
    public short X { get; set; }
    public short Y { get; set; }
    public short ShadowX { get; set; }
    public short ShadowY { get; set; }
    public byte Shadow { get; set; }
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public short MaskWidth { get; set; }
    public short MaskHeight { get; set; }
    public short MaskX { get; set; }
    public short MaskY { get; set; }
    public byte[] MaskData { get; set; } = Array.Empty<byte>();

    public bool HasMask => (Shadow & 0x80) != 0;
}
