using System.IO.Compression;

namespace Shared.StreamingAssets;

public static class StreamingAssetV3IO
{
    private const int MaximumAssetCount = 10_000_000;
    private const int MaximumRecordLength = 256 * 1024 * 1024;
    private const int MaximumIdLength = 2048;

    public static byte[] WriteLibraryIndex(V3LibraryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Images.Count != index.ImageCount)
            throw new InvalidDataException("Library image count does not match its index.");

        using MemoryStream output = new();
        using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetV3Constants.LibraryIndexMagic);
            writer.Write(StreamingAssetV3Constants.FormatVersion);
            writer.Write(index.Id ?? string.Empty);
            writer.Write(index.ImageCount);
            writer.Write(index.Frames.Count);

            for (int i = 0; i < index.Images.Count; i++)
            {
                V3LibraryImageRecord image = index.Images[i];
                if (image.Index != i)
                    throw new InvalidDataException($"Library image index is not dense at {i}.");

                writer.Write(image.Index);
                writer.Write(image.Exists);
                writer.Write(image.Offset);
                writer.Write(image.Length);
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(image.X);
                writer.Write(image.Y);
                writer.Write(image.ShadowX);
                writer.Write(image.ShadowY);
                writer.Write(image.Shadow);
                writer.Write(image.TrueWidth);
                writer.Write(image.TrueHeight);
                writer.Write(image.Exists ? Convert.FromHexString(image.Hash) : new byte[32]);
            }

            foreach (LibraryFrameRecord frame in index.Frames)
            {
                writer.Write(frame.Action);
                writer.Write(frame.Start);
                writer.Write(frame.Count);
                writer.Write(frame.Skip);
                writer.Write(frame.Interval);
                writer.Write(frame.EffectStart);
                writer.Write(frame.EffectCount);
                writer.Write(frame.EffectSkip);
                writer.Write(frame.EffectInterval);
                writer.Write(frame.Reverse);
                writer.Write(frame.Blend);
            }
        }

        return output.ToArray();
    }

    public static V3LibraryIndex ReadLibraryIndex(byte[] data, string expectedId = null,
        int expectedImageCount = -1, long libraryLength = -1)
    {
        ArgumentNullException.ThrowIfNull(data);
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input);

        Require(reader.ReadInt32() == StreamingAssetV3Constants.LibraryIndexMagic, "Invalid V3 library index magic.");
        Require(reader.ReadInt32() == StreamingAssetV3Constants.FormatVersion, "Unsupported V3 library index version.");
        string id = reader.ReadString();
        Require(id.Length <= MaximumIdLength, "Library id is too long.");
        Require(expectedId == null || string.Equals(NormalizeId(id), NormalizeId(expectedId), StringComparison.Ordinal),
            "Library id does not match the root manifest.");

        int imageCount = reader.ReadInt32();
        int frameCount = reader.ReadInt32();
        Require(imageCount >= 0 && imageCount <= MaximumAssetCount, "Invalid library image count.");
        Require(frameCount >= 0 && frameCount <= MaximumAssetCount, "Invalid library frame count.");
        Require(expectedImageCount < 0 || imageCount == expectedImageCount,
            "Library image count does not match the root manifest.");

        V3LibraryIndex index = new() { Id = NormalizeId(id), ImageCount = imageCount };
        for (int i = 0; i < imageCount; i++)
        {
            V3LibraryImageRecord image = new()
            {
                Index = reader.ReadInt32()
            };
            bool exists = reader.ReadBoolean();
            image.Offset = reader.ReadInt64();
            image.Length = reader.ReadInt32();
            image.Width = reader.ReadInt16();
            image.Height = reader.ReadInt16();
            image.X = reader.ReadInt16();
            image.Y = reader.ReadInt16();
            image.ShadowX = reader.ReadInt16();
            image.ShadowY = reader.ReadInt16();
            image.Shadow = reader.ReadByte();
            image.TrueWidth = reader.ReadInt16();
            image.TrueHeight = reader.ReadInt16();
            byte[] hash = ReadExact(reader, 32);
            image.Hash = exists ? Convert.ToHexString(hash).ToLowerInvariant() : string.Empty;

            Require(image.Index == i, "Library image indexes are not dense.");
            if (exists)
            {
                Require(image.Offset >= 0 && image.Length >= 17 && image.Length <= MaximumRecordLength,
                    "Invalid library image range.");
                Require(image.Width >= 0 && image.Height >= 0 && image.TrueWidth >= 0 && image.TrueHeight >= 0,
                    "Invalid library image dimensions.");
                Require(StreamingAssetIO.IsValidSha256(image.Hash), "Invalid library image hash.");
                if (libraryLength >= 0)
                    Require(image.Offset <= libraryLength && image.Length <= libraryLength - image.Offset,
                        "Library image range exceeds the Lib file.");
            }
            else
            {
                Require(image.Offset == -1 && image.Length == 0 && hash.All(value => value == 0),
                    "Invalid empty library image record.");
            }
            index.Images.Add(image);
        }

        for (int i = 0; i < frameCount; i++)
        {
            index.Frames.Add(new LibraryFrameRecord
            {
                Action = reader.ReadByte(),
                Start = reader.ReadInt32(),
                Count = reader.ReadInt32(),
                Skip = reader.ReadInt32(),
                Interval = reader.ReadInt32(),
                EffectStart = reader.ReadInt32(),
                EffectCount = reader.ReadInt32(),
                EffectSkip = reader.ReadInt32(),
                EffectInterval = reader.ReadInt32(),
                Reverse = reader.ReadBoolean(),
                Blend = reader.ReadBoolean()
            });
        }

        Require(input.Position == input.Length, "Trailing data in V3 library index.");
        return index;
    }

    public static byte[] WriteMapIndex(V3MapIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        using MemoryStream output = new();
        using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetV3Constants.MapIndexMagic);
            writer.Write(StreamingAssetV3Constants.FormatVersion);
            writer.Write(index.Id ?? string.Empty);
            writer.Write(index.Width);
            writer.Write(index.Height);
            writer.Write(index.ChunkSize);
            writer.Write(index.Chunks.Count);
            foreach (V3MapChunkRecord chunk in index.Chunks)
            {
                writer.Write(chunk.X);
                writer.Write(chunk.Y);
                writer.Write(chunk.Width);
                writer.Write(chunk.Height);
                writer.Write(chunk.Offset);
                writer.Write(chunk.Length);
                writer.Write(chunk.UncompressedLength);
                writer.Write(Convert.FromHexString(chunk.Hash));
            }
        }
        return output.ToArray();
    }

    public static V3MapIndex ReadMapIndex(byte[] data, string expectedId = null, long packLength = -1)
    {
        ArgumentNullException.ThrowIfNull(data);
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input);

        Require(reader.ReadInt32() == StreamingAssetV3Constants.MapIndexMagic, "Invalid V3 map index magic.");
        Require(reader.ReadInt32() == StreamingAssetV3Constants.FormatVersion, "Unsupported V3 map index version.");
        string id = reader.ReadString();
        Require(id.Length <= MaximumIdLength, "Map id is too long.");
        Require(expectedId == null || string.Equals(NormalizeId(id), NormalizeId(expectedId), StringComparison.Ordinal),
            "Map id does not match the root manifest.");

        V3MapIndex index = new()
        {
            Id = NormalizeId(id),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
            ChunkSize = reader.ReadInt32()
        };
        int count = reader.ReadInt32();
        Require(index.Width > 0 && index.Height > 0 && index.Width <= 100_000 && index.Height <= 100_000,
            "Invalid map dimensions.");
        Require(index.ChunkSize > 0 && index.ChunkSize <= 1024, "Invalid map chunk size.");
        Require(count >= 0 && count <= MaximumAssetCount, "Invalid map chunk count.");

        int columns = (index.Width + index.ChunkSize - 1) / index.ChunkSize;
        int rows = (index.Height + index.ChunkSize - 1) / index.ChunkSize;
        Require((long)columns * rows == count, "Map chunk count does not cover the complete map.");

        HashSet<string> keys = new(StringComparer.Ordinal);
        long expectedOffset = 0;
        for (int i = 0; i < count; i++)
        {
            V3MapChunkRecord chunk = new()
            {
                X = reader.ReadInt32(),
                Y = reader.ReadInt32(),
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Offset = reader.ReadInt64(),
                Length = reader.ReadInt32(),
                UncompressedLength = reader.ReadInt32(),
                Hash = Convert.ToHexString(ReadExact(reader, 32)).ToLowerInvariant()
            };
            Require(chunk.X >= 0 && chunk.Y >= 0 && chunk.Width > 0 && chunk.Height > 0 &&
                    chunk.X + chunk.Width <= index.Width && chunk.Y + chunk.Height <= index.Height,
                "Invalid map chunk bounds.");
            int expectedX = i / rows * index.ChunkSize;
            int expectedY = i % rows * index.ChunkSize;
            Require(chunk.X == expectedX && chunk.Y == expectedY &&
                    chunk.Width == Math.Min(index.ChunkSize, index.Width - expectedX) &&
                    chunk.Height == Math.Min(index.ChunkSize, index.Height - expectedY),
                "Map chunks are missing, misordered or not aligned to the chunk grid.");
            Require(chunk.Offset >= 0 && chunk.Length > 0 && chunk.Length <= MaximumRecordLength &&
                    chunk.UncompressedLength > 0 && chunk.UncompressedLength <= MaximumRecordLength,
                "Invalid map chunk range.");
            Require(chunk.Offset == expectedOffset, "Map chunk ranges are not contiguous.");
            Require(chunk.UncompressedLength == checked(20 + chunk.Width * chunk.Height * 32),
                "Invalid map chunk uncompressed length.");
            Require(StreamingAssetIO.IsValidSha256(chunk.Hash), "Invalid map chunk hash.");
            Require(keys.Add(chunk.Key), "Duplicate map chunk coordinates.");
            if (packLength >= 0)
                Require(chunk.Offset <= packLength && chunk.Length <= packLength - chunk.Offset,
                    "Map chunk range exceeds its pack.");
            index.Chunks.Add(chunk);
            expectedOffset += chunk.Length;
        }

        if (packLength >= 0) Require(expectedOffset == packLength, "Map pack has trailing or missing data.");
        Require(input.Position == input.Length, "Trailing data in V3 map index.");
        return index;
    }

    public static StreamingMapChunk ReadMapChunk(byte[] data, V3MapChunkRecord expected)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(expected);
        byte[] raw = DecompressExact(data, expected.UncompressedLength);
        using MemoryStream input = new(raw, false);
        using BinaryReader reader = new(input);

        StreamingMapChunk chunk = new()
        {
            X = reader.ReadInt32(),
            Y = reader.ReadInt32(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32()
        };
        int count = reader.ReadInt32();
        Require(chunk.X == expected.X && chunk.Y == expected.Y && chunk.Width == expected.Width &&
                chunk.Height == expected.Height && count == checked(expected.Width * expected.Height),
            "Map chunk header does not match its index.");

        chunk.Cells = new StreamingMapCell[count];
        for (int i = 0; i < count; i++)
        {
            chunk.Cells[i] = new StreamingMapCell
            {
                BackIndex = reader.ReadInt16(),
                BackImage = reader.ReadInt32(),
                MiddleIndex = reader.ReadInt16(),
                MiddleImage = reader.ReadInt32(),
                FrontIndex = reader.ReadInt16(),
                FrontImage = reader.ReadInt32(),
                DoorIndex = reader.ReadByte(),
                DoorOffset = reader.ReadByte(),
                FrontAnimationFrame = reader.ReadByte(),
                FrontAnimationTick = reader.ReadByte(),
                MiddleAnimationFrame = reader.ReadByte(),
                MiddleAnimationTick = reader.ReadByte(),
                TileAnimationImage = reader.ReadInt16(),
                TileAnimationOffset = reader.ReadInt16(),
                TileAnimationFrames = reader.ReadByte(),
                Light = reader.ReadByte(),
                Unknown = reader.ReadByte(),
                FishingCell = reader.ReadBoolean()
            };
        }

        Require(input.Position == input.Length, "Trailing data in V3 map chunk.");
        return chunk;
    }

    public static V3LibraryImagePayload ReadLibraryImageRecord(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Require(data.Length >= 17 && data.Length <= MaximumRecordLength, "Invalid Lib image record length.");
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input);

        V3LibraryImagePayload image = new()
        {
            Width = reader.ReadInt16(),
            Height = reader.ReadInt16(),
            X = reader.ReadInt16(),
            Y = reader.ReadInt16(),
            ShadowX = reader.ReadInt16(),
            ShadowY = reader.ReadInt16(),
            Shadow = reader.ReadByte()
        };
        int imageLength = reader.ReadInt32();
        Require(imageLength >= 0 && imageLength <= input.Length - input.Position, "Invalid Lib image payload length.");
        image.ImageData = ReadExact(reader, imageLength);

        if (image.HasMask)
        {
            Require(input.Length - input.Position >= 12, "Truncated Lib mask header.");
            image.MaskWidth = reader.ReadInt16();
            image.MaskHeight = reader.ReadInt16();
            image.MaskX = reader.ReadInt16();
            image.MaskY = reader.ReadInt16();
            int maskLength = reader.ReadInt32();
            Require(maskLength >= 0 && maskLength <= input.Length - input.Position, "Invalid Lib mask payload length.");
            image.MaskData = ReadExact(reader, maskLength);
        }

        Require(input.Position == input.Length, "Trailing data in Lib image record.");
        return image;
    }

    public static (short Width, short Height) ComputeTrueSize(short width, short height, byte[] compressedImage)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        int expectedLength = checked(width * height * 4);
        int rowStride = checked(width * 4);
        byte[] pixels;
        try
        {
            pixels = DecompressExact(compressedImage, expectedLength);
        }
        catch (InvalidDataException) when ((width & 3) != 0 || (height & 3) != 0)
        {
            int storageWidth = (width + 3) & ~3;
            int storageHeight = (height + 3) & ~3;
            pixels = DecompressExact(compressedImage, checked(storageWidth * storageHeight * 4));
            rowStride = checked(storageWidth * 4);
        }

        int left = 0;
        int top = 0;
        int right = width;
        int bottom = height;
        bool visible = false;

        for (int x = 0; x < right && !visible; x++)
        {
            for (int y = 0; y < bottom; y++)
            {
                if (pixels[y * rowStride + x * 4 + 3] == 0) continue;
                left = x;
                visible = true;
                break;
            }
        }

        visible = false;
        for (int y = 0; y < bottom && !visible; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (pixels[y * rowStride + x * 4 + 3] == 0) continue;
                top = y;
                visible = true;
                break;
            }
        }

        visible = false;
        for (int x = right - 1; x >= left && !visible; x--)
        {
            for (int y = 0; y < bottom; y++)
            {
                if (pixels[y * rowStride + x * 4 + 3] == 0) continue;
                right = x + 1;
                visible = true;
                break;
            }
        }

        visible = false;
        for (int y = bottom - 1; y >= top && !visible; y--)
        {
            for (int x = left; x < right; x++)
            {
                if (pixels[y * rowStride + x * 4 + 3] == 0) continue;
                bottom = y + 1;
                visible = true;
                break;
            }
        }

        return (checked((short)(right - left)), checked((short)(bottom - top)));
    }

    public static byte[] DecompressExact(byte[] compressed, int expectedLength)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (expectedLength < 0 || expectedLength > MaximumRecordLength)
            throw new InvalidDataException("Invalid decompressed image length.");

        byte[] output = new byte[expectedLength];
        using MemoryStream input = new(compressed, false);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        int total = 0;
        while (total < output.Length)
        {
            int read = gzip.Read(output, total, output.Length - total);
            if (read == 0) break;
            total += read;
        }
        Require(total == output.Length && gzip.ReadByte() == -1, "Unexpected decompressed image size.");
        return output;
    }

    public static string GetCatalogPath(string hash) => GetHashedPath(StreamingAssetV3Constants.CatalogsDirectory, hash, ".bin");
    public static string GetLibraryPath(string hash) => GetHashedPath(StreamingAssetV3Constants.LibrariesDirectory, hash, ".lib");
    public static string GetMapPath(string hash) => GetHashedPath(StreamingAssetV3Constants.MapsDirectory, hash, ".mappack");

    public static string GetSoundPath(string hash, string extension)
    {
        string normalized = (extension ?? string.Empty).ToLowerInvariant();
        if (normalized is not ".wav" and not ".mp3" and not ".lst")
            throw new ArgumentException("Unsupported sound extension.", nameof(extension));
        return GetHashedPath(StreamingAssetV3Constants.SoundsDirectory, hash, normalized);
    }

    private static string GetHashedPath(string directory, string hash, string extension)
    {
        if (!StreamingAssetIO.IsValidSha256(hash))
            throw new ArgumentException("Expected a SHA-256 hash.", nameof(hash));
        return $"{directory}/{hash.ToLowerInvariant()}{extension}";
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Unexpected end of binary asset metadata.");
        return bytes;
    }

    private static string NormalizeId(string id) => (id ?? string.Empty).Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
