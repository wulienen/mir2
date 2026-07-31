namespace Shared.StreamingAssets;

/// <summary>
/// Reader for a whole <c>.mappack</c> file. The compressed bytes stay resident (a pack is well under
/// a megabyte) and individual 32x32 chunks are inflated on demand, so a map costs one HTTP request
/// and one hash verification instead of hundreds of ranged requests.
/// </summary>
public sealed class StreamingMapPack
{
    private const int HeaderSize = 24;

    private readonly byte[] _data;
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly int[] _uncompressedLengths;

    private StreamingMapPack(byte[] data, int width, int height, int chunkSize,
        int[] offsets, int[] lengths, int[] uncompressedLengths)
    {
        _data = data;
        _offsets = offsets;
        _lengths = lengths;
        _uncompressedLengths = uncompressedLengths;
        Width = width;
        Height = height;
        ChunkSize = chunkSize;
        Columns = (width + chunkSize - 1) / chunkSize;
        Rows = (height + chunkSize - 1) / chunkSize;
    }

    public int Width { get; }
    public int Height { get; }
    public int ChunkSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int ChunkCount => _lengths.Length;

    public static StreamingMapPack Parse(byte[] data, int expectedWidth = -1, int expectedHeight = -1,
        int expectedChunkSize = -1)
    {
        ArgumentNullException.ThrowIfNull(data);
        Require(data.Length >= HeaderSize, "Truncated map pack header.");

        int magic = ReadInt32(data, 0);
        int formatVersion = ReadInt32(data, 4);
        int width = ReadInt32(data, 8);
        int height = ReadInt32(data, 12);
        int chunkSize = ReadInt32(data, 16);
        int chunkCount = ReadInt32(data, 20);

        Require(magic == StreamingAssetV3Constants.MapPackMagic, "Invalid map pack magic.");
        Require(formatVersion == StreamingAssetV3Constants.FormatVersion, "Unsupported map pack version.");
        Require(width > 0 && height > 0 && width <= 100_000 && height <= 100_000, "Invalid map pack dimensions.");
        Require(chunkSize > 0 && chunkSize <= 1024, "Invalid map pack chunk size.");
        Require(chunkCount > 0 && chunkCount <= StreamingAssetV3Constants.MaxMapPackChunks, "Invalid map pack chunk count.");
        Require(expectedWidth < 0 || width == expectedWidth, "Map pack width does not match the root manifest.");
        Require(expectedHeight < 0 || height == expectedHeight, "Map pack height does not match the root manifest.");
        Require(expectedChunkSize < 0 || chunkSize == expectedChunkSize,
            "Map pack chunk size does not match the root manifest.");

        int columns = (width + chunkSize - 1) / chunkSize;
        int rows = (height + chunkSize - 1) / chunkSize;
        Require((long)columns * rows == chunkCount, "Map pack chunks do not cover the complete map.");

        long directoryLength = (long)chunkCount * 8;
        Require(data.Length >= HeaderSize + directoryLength, "Truncated map pack directory.");

        int[] offsets = new int[chunkCount];
        int[] lengths = new int[chunkCount];
        int[] uncompressedLengths = new int[chunkCount];
        long payloadStart = HeaderSize + directoryLength;
        long cursor = payloadStart;

        for (int i = 0; i < chunkCount; i++)
        {
            int position = HeaderSize + i * 8;
            int length = ReadInt32(data, position);
            int uncompressedLength = ReadInt32(data, position + 4);
            int chunkX = i / rows * chunkSize;
            int chunkY = i % rows * chunkSize;
            int expected = 20 + Math.Min(chunkSize, width - chunkX) * Math.Min(chunkSize, height - chunkY) * 32;

            Require(length > 0 && length <= data.Length - cursor, "Map pack chunk payload exceeds the file.");
            Require(uncompressedLength == expected, "Map pack chunk declares an unexpected uncompressed length.");

            offsets[i] = (int)cursor;
            lengths[i] = length;
            uncompressedLengths[i] = uncompressedLength;
            cursor += length;
        }

        Require(cursor == data.Length, "Map pack has trailing or missing data.");
        return new StreamingMapPack(data, width, height, chunkSize, offsets, lengths, uncompressedLengths);
    }

    /// <summary>Chunk order is column-major, matching the builder.</summary>
    public int GetChunkIndex(int column, int row)
    {
        if (column < 0 || column >= Columns || row < 0 || row >= Rows) return -1;
        return column * Rows + row;
    }

    public int GetChunkIndexForCell(int x, int y) => GetChunkIndex(x / ChunkSize, y / ChunkSize);

    public StreamingMapChunk ReadChunk(int index)
    {
        if (index < 0 || index >= _lengths.Length) throw new ArgumentOutOfRangeException(nameof(index));
        int chunkX = index / Rows * ChunkSize;
        int chunkY = index % Rows * ChunkSize;
        return StreamingAssetV3IO.ReadMapChunk(_data, _offsets[index], _lengths[index], chunkX, chunkY,
            Math.Min(ChunkSize, Width - chunkX), Math.Min(ChunkSize, Height - chunkY), _uncompressedLengths[index]);
    }

    private static int ReadInt32(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
