namespace Shared.StreamingAssets;

public static class StreamingAssetV3Constants
{
    public const int FormatVersion = 3;
    public const int DefaultMapChunkSize = 32;
    public const int LibraryIndexMagic = 0x34494C59; // YLI4
    public const int MapPackMagic = 0x33504D59; // YMP3
    public const int WorkingSetMagic = 0x33535759; // YWS3
    public const int MaxMapPackChunks = 1 << 20;

    /// <summary>Fixed size of one packed image record inside a library index segment.</summary>
    public const int LibraryImageRecordSize = 25;

    /// <summary>Images per index segment. A client only fetches the segments it actually draws from.</summary>
    public const int LibraryIndexSegmentSize = 4096;

    public const int MaxLibraryIndexSegments = 1 << 16;
    public const string ManifestFileName = "manifest.json";
    public const string CatalogsDirectory = "catalogs";
    public const string LibrariesDirectory = "libraries";
    public const string MapsDirectory = "maps";
    public const string SoundsDirectory = "sounds";
    public const string WorkingSetsDirectory = "worksets";
}

public sealed class StreamingAssetV3Manifest
{
    public int FormatVersion { get; set; } = StreamingAssetV3Constants.FormatVersion;
    public string Version { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string CatalogHash { get; set; } = string.Empty;
    public long CatalogLength { get; set; }

    /// <summary>
    /// Optional first-run working set: the image records a cold client touches between the login screen and
    /// standing in the first map, published as one file so they arrive in a single request instead of
    /// hundreds of ranged ones. Empty when no usage recording has been published.
    /// </summary>
    public string WorkingSetHash { get; set; } = string.Empty;

    public long WorkingSetLength { get; set; }
    public int WorkingSetImageCount { get; set; }
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

    /// <summary>
    /// Length of the block's uncompressed header (segment directory plus frames). The client fetches
    /// exactly these bytes first; every segment payload is then located and verified from the header.
    /// </summary>
    public int CatalogHeaderLength { get; set; }

    public string CatalogHeaderHash { get; set; } = string.Empty;
}

public sealed class V3MapRecord
{
    public string Id { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int ChunkSize { get; set; } = StreamingAssetV3Constants.DefaultMapChunkSize;
    public string FileHash { get; set; } = string.Empty;
    public long FileLength { get; set; }
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

/// <summary>
/// Uncompressed head of one library's catalog block: the segment directory and the frame table. It is
/// small enough (a few hundred bytes for most libraries) to be fetched in one range request.
/// </summary>
public sealed class V3LibraryIndexHeader
{
    public string Id { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public int SegmentSize { get; set; } = StreamingAssetV3Constants.LibraryIndexSegmentSize;
    public int HeaderLength { get; set; }
    public List<V3LibraryIndexSegment> Segments { get; set; } = new();
    public List<LibraryFrameRecord> Frames { get; set; } = new();

    public int SegmentCount => Segments.Count;

    public int GetSegmentIndex(int imageIndex) => imageIndex / SegmentSize;

    public int GetSegmentFirstImage(int segment) => segment * SegmentSize;

    public int GetSegmentImageCount(int segment) =>
        Math.Min(SegmentSize, ImageCount - segment * SegmentSize);
}

/// <summary>One Brotli-compressed run of packed image records, addressed relative to the block start.</summary>
public sealed class V3LibraryIndexSegment
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int CompressedLength { get; set; }
    public int UncompressedLength { get; set; }
    public string Hash { get; set; } = string.Empty;
}

/// <summary>Result of serialising a library index: the bytes to publish plus what the manifest needs.</summary>
public sealed class V3LibraryIndexBlock
{
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public int HeaderLength { get; set; }
    public string HeaderHash { get; set; } = string.Empty;
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

    /// <summary>
    /// A published record is either absent or a complete Lib image record. Downloaded bytes are checked
    /// structurally against this metadata instead of against a per-image hash, which keeps the catalog
    /// small enough to segment and stream.
    /// </summary>
    public bool Exists => Offset >= 0 && Length >= 17;
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
