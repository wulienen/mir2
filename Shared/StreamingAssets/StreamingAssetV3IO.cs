using System.Buffers.Binary;
using System.IO.Compression;

namespace Shared.StreamingAssets;

public static class StreamingAssetV3IO
{
    private const int MaximumAssetCount = 10_000_000;
    private const int MaximumRecordLength = 256 * 1024 * 1024;
    private const int MaximumIdLength = 2048;
    private const int RecordSize = StreamingAssetV3Constants.LibraryImageRecordSize;

    /// <summary>
    /// Serialises one library index as an uncompressed header (segment directory and frames) followed by
    /// Brotli-compressed segments of fixed-size image records. Clients fetch the header once and then only
    /// the segments they draw from, so the published catalog no longer has to be read whole.
    /// Layout (little-endian):
    /// magic, formatVersion, id, imageCount, segmentSize, segmentCount, frameCount,
    /// segmentCount * (compressedLength, uncompressedLength, 32 byte hash), frames, then the payloads.
    /// </summary>
    public static V3LibraryIndexBlock WriteLibraryIndexBlock(V3LibraryIndex index,
        int segmentSize = StreamingAssetV3Constants.LibraryIndexSegmentSize)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Images.Count != index.ImageCount)
            throw new InvalidDataException("Library image count does not match its index.");
        if (segmentSize <= 0 || segmentSize > MaximumAssetCount)
            throw new InvalidDataException("Invalid library index segment size.");

        int segmentCount = (index.ImageCount + segmentSize - 1) / segmentSize;
        if (segmentCount > StreamingAssetV3Constants.MaxLibraryIndexSegments)
            throw new InvalidDataException("Library index needs too many segments.");

        byte[][] payloads = new byte[segmentCount][];
        int[] rawLengths = new int[segmentCount];
        string[] hashes = new string[segmentCount];
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int first = segment * segmentSize;
            int count = Math.Min(segmentSize, index.ImageCount - first);
            byte[] raw = new byte[count * RecordSize];
            for (int i = 0; i < count; i++)
            {
                V3LibraryImageRecord image = index.Images[first + i];
                if (image.Index != first + i)
                    throw new InvalidDataException($"Library image index is not dense at {first + i}.");
                EncodeLibraryImageRecord(image, raw.AsSpan(i * RecordSize, RecordSize));
            }
            payloads[segment] = Compress(raw);
            rawLengths[segment] = raw.Length;
            hashes[segment] = StreamingAssetIO.ComputeSha256(payloads[segment]);
        }

        using MemoryStream output = new();
        int headerLength;
        using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetV3Constants.LibraryIndexMagic);
            writer.Write(StreamingAssetV3Constants.FormatVersion);
            writer.Write(index.Id ?? string.Empty);
            writer.Write(index.ImageCount);
            writer.Write(segmentSize);
            writer.Write(segmentCount);
            writer.Write(index.Frames.Count);

            for (int segment = 0; segment < segmentCount; segment++)
            {
                writer.Write(payloads[segment].Length);
                writer.Write(rawLengths[segment]);
                writer.Write(Convert.FromHexString(hashes[segment]));
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

            writer.Flush();
            headerLength = checked((int)output.Length);
            foreach (byte[] payload in payloads) writer.Write(payload);
        }

        byte[] bytes = output.ToArray();
        return new V3LibraryIndexBlock
        {
            Bytes = bytes,
            HeaderLength = headerLength,
            HeaderHash = StreamingAssetIO.ComputeSha256(bytes.AsSpan(0, headerLength).ToArray())
        };
    }

    /// <summary>Parses a block header. <paramref name="length"/> must be the published header length.</summary>
    public static V3LibraryIndexHeader ReadLibraryIndexHeader(byte[] data, int offset, int length,
        string expectedId = null, int expectedImageCount = -1)
    {
        ArgumentNullException.ThrowIfNull(data);
        Require(offset >= 0 && length > 0 && length <= data.Length - offset, "Library index header range is invalid.");
        using MemoryStream input = new(data, offset, length, false);
        using BinaryReader reader = new(input);

        Require(reader.ReadInt32() == StreamingAssetV3Constants.LibraryIndexMagic, "Invalid V3 library index magic.");
        Require(reader.ReadInt32() == StreamingAssetV3Constants.FormatVersion, "Unsupported V3 library index version.");
        string id = reader.ReadString();
        Require(id.Length <= MaximumIdLength, "Library id is too long.");
        Require(expectedId == null || string.Equals(NormalizeId(id), NormalizeId(expectedId), StringComparison.Ordinal),
            "Library id does not match the root manifest.");

        int imageCount = reader.ReadInt32();
        int segmentSize = reader.ReadInt32();
        int segmentCount = reader.ReadInt32();
        int frameCount = reader.ReadInt32();
        Require(imageCount >= 0 && imageCount <= MaximumAssetCount, "Invalid library image count.");
        Require(segmentSize > 0 && segmentSize <= MaximumAssetCount, "Invalid library index segment size.");
        Require(segmentCount >= 0 && segmentCount <= StreamingAssetV3Constants.MaxLibraryIndexSegments,
            "Invalid library index segment count.");
        Require(segmentCount == (imageCount + segmentSize - 1) / segmentSize,
            "Library index segment count does not match its image count.");
        Require(frameCount >= 0 && frameCount <= MaximumAssetCount, "Invalid library frame count.");
        Require(expectedImageCount < 0 || imageCount == expectedImageCount,
            "Library image count does not match the root manifest.");

        V3LibraryIndexHeader header = new()
        {
            Id = NormalizeId(id), ImageCount = imageCount, SegmentSize = segmentSize
        };

        long payloadOffset = 0;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int compressedLength = reader.ReadInt32();
            int uncompressedLength = reader.ReadInt32();
            string hash = Convert.ToHexString(ReadExact(reader, 32)).ToLowerInvariant();
            Require(compressedLength > 0 && compressedLength <= MaximumRecordLength,
                "Invalid library index segment length.");
            Require(uncompressedLength == header.GetSegmentImageCount(segment) * RecordSize,
                "Library index segment declares an unexpected record count.");
            Require(StreamingAssetIO.IsValidSha256(hash), "Invalid library index segment hash.");
            header.Segments.Add(new V3LibraryIndexSegment
            {
                Index = segment, Offset = payloadOffset, CompressedLength = compressedLength,
                UncompressedLength = uncompressedLength, Hash = hash
            });
            payloadOffset = checked(payloadOffset + compressedLength);
        }

        for (int i = 0; i < frameCount; i++)
        {
            header.Frames.Add(new LibraryFrameRecord
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

        Require(input.Position == input.Length, "Trailing data in V3 library index header.");
        header.HeaderLength = length;
        // Payload offsets are relative to the block, so shift them past the header.
        foreach (V3LibraryIndexSegment segment in header.Segments) segment.Offset += length;
        return header;
    }

    /// <summary>Inflates one segment and validates every record it contains against the Lib file length.</summary>
    public static byte[] ReadLibraryIndexSegment(V3LibraryIndexHeader header, int segmentIndex,
        byte[] compressed, long libraryLength = -1)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(compressed);
        Require(segmentIndex >= 0 && segmentIndex < header.SegmentCount, "Library index segment is out of range.");
        V3LibraryIndexSegment segment = header.Segments[segmentIndex];
        Require(compressed.Length == segment.CompressedLength, "Library index segment length does not match its header.");

        byte[] raw = Decompress(compressed, segment.UncompressedLength);
        int first = header.GetSegmentFirstImage(segmentIndex);
        int count = header.GetSegmentImageCount(segmentIndex);
        for (int i = 0; i < count; i++)
            ValidateLibraryImageRecord(DecodeLibraryImageRecord(raw, i, first + i), libraryLength);
        return raw;
    }

    public static V3LibraryImageRecord DecodeLibraryImageRecord(byte[] segment, int indexInSegment, int imageIndex)
    {
        ArgumentNullException.ThrowIfNull(segment);
        int start = indexInSegment * RecordSize;
        if (indexInSegment < 0 || start > segment.Length - RecordSize)
            throw new InvalidDataException("Library image record is out of range.");
        ReadOnlySpan<byte> span = segment.AsSpan(start, RecordSize);

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        return new V3LibraryImageRecord
        {
            Index = imageIndex,
            Offset = length == 0 ? -1 : offset,
            Length = length == 0 ? 0 : checked((int)length),
            Width = BinaryPrimitives.ReadInt16LittleEndian(span[8..]),
            Height = BinaryPrimitives.ReadInt16LittleEndian(span[10..]),
            X = BinaryPrimitives.ReadInt16LittleEndian(span[12..]),
            Y = BinaryPrimitives.ReadInt16LittleEndian(span[14..]),
            ShadowX = BinaryPrimitives.ReadInt16LittleEndian(span[16..]),
            ShadowY = BinaryPrimitives.ReadInt16LittleEndian(span[18..]),
            Shadow = span[20],
            TrueWidth = BinaryPrimitives.ReadInt16LittleEndian(span[21..]),
            TrueHeight = BinaryPrimitives.ReadInt16LittleEndian(span[23..])
        };
    }

    private static void EncodeLibraryImageRecord(V3LibraryImageRecord image, Span<byte> span)
    {
        if (image.Exists)
        {
            if (image.Offset < 0 || image.Offset > uint.MaxValue)
                throw new InvalidDataException($"Library image {image.Index} offset does not fit in 32 bits.");
            if (image.Length <= 0 || image.Length > MaximumRecordLength)
                throw new InvalidDataException($"Library image {image.Index} has an invalid record length.");
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)image.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)image.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0);
        }

        BinaryPrimitives.WriteInt16LittleEndian(span[8..], image.Width);
        BinaryPrimitives.WriteInt16LittleEndian(span[10..], image.Height);
        BinaryPrimitives.WriteInt16LittleEndian(span[12..], image.X);
        BinaryPrimitives.WriteInt16LittleEndian(span[14..], image.Y);
        BinaryPrimitives.WriteInt16LittleEndian(span[16..], image.ShadowX);
        BinaryPrimitives.WriteInt16LittleEndian(span[18..], image.ShadowY);
        span[20] = image.Shadow;
        BinaryPrimitives.WriteInt16LittleEndian(span[21..], image.TrueWidth);
        BinaryPrimitives.WriteInt16LittleEndian(span[23..], image.TrueHeight);
    }

    private static void ValidateLibraryImageRecord(V3LibraryImageRecord image, long libraryLength)
    {
        if (!image.Exists)
        {
            Require(image.Offset == -1 && image.Length == 0, "Invalid empty library image record.");
            return;
        }
        Require(image.Length >= 17 && image.Length <= MaximumRecordLength, "Invalid library image range.");
        Require(image.Width >= 0 && image.Height >= 0 && image.TrueWidth >= 0 && image.TrueHeight >= 0,
            "Invalid library image dimensions.");
        if (libraryLength >= 0)
            Require(image.Offset <= libraryLength && image.Length <= libraryLength - image.Offset,
                "Library image range exceeds the Lib file.");
    }

    /// <summary>
    /// Checks downloaded Lib bytes against their catalog metadata. This replaces the per-image SHA-256 the
    /// catalog used to carry: a record only passes if it parses exactly and its header matches the index.
    /// </summary>
    public static bool IsLibraryImageRecordValid(V3LibraryImageRecord image, byte[] bytes)
    {
        if (image == null || bytes == null || bytes.Length != image.Length) return false;
        try
        {
            V3LibraryImagePayload payload = ReadLibraryImageRecord(bytes);
            return payload.Width == image.Width && payload.Height == image.Height && payload.X == image.X &&
                   payload.Y == image.Y && payload.ShadowX == image.ShadowX && payload.ShadowY == image.ShadowY &&
                   payload.Shadow == image.Shadow;
        }
        catch (InvalidDataException) { return false; }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>Materialises a whole block. Used by the builder; clients only ever read single segments.</summary>
    public static V3LibraryIndex ReadLibraryIndex(byte[] data, string expectedId = null,
        int expectedImageCount = -1, long libraryLength = -1)
    {
        ArgumentNullException.ThrowIfNull(data);
        V3LibraryIndexHeader header = ReadLibraryIndexHeader(data, 0, PeekHeaderLength(data),
            expectedId, expectedImageCount);
        V3LibraryIndex index = new() { Id = header.Id, ImageCount = header.ImageCount };
        long expectedEnd = header.HeaderLength;

        for (int segmentIndex = 0; segmentIndex < header.SegmentCount; segmentIndex++)
        {
            V3LibraryIndexSegment segment = header.Segments[segmentIndex];
            Require(segment.Offset == expectedEnd, "Library index segments are not contiguous.");
            Require(segment.Offset + segment.CompressedLength <= data.Length,
                "Library index segment exceeds its block.");
            byte[] compressed = data.AsSpan((int)segment.Offset, segment.CompressedLength).ToArray();
            byte[] raw = ReadLibraryIndexSegment(header, segmentIndex, compressed, libraryLength);
            int first = header.GetSegmentFirstImage(segmentIndex);
            int count = header.GetSegmentImageCount(segmentIndex);
            for (int i = 0; i < count; i++) index.Images.Add(DecodeLibraryImageRecord(raw, i, first + i));
            expectedEnd += segment.CompressedLength;
        }

        Require(expectedEnd == data.Length, "Trailing data in V3 library index block.");
        index.Frames.AddRange(header.Frames);
        return index;
    }

    /// <summary>
    /// Reads the header length of a block that is already in memory, so a whole published block can be
    /// parsed without consulting the manifest.
    /// </summary>
    public static int PeekHeaderLength(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input);
        Require(reader.ReadInt32() == StreamingAssetV3Constants.LibraryIndexMagic, "Invalid V3 library index magic.");
        Require(reader.ReadInt32() == StreamingAssetV3Constants.FormatVersion, "Unsupported V3 library index version.");
        string id = reader.ReadString();
        Require(id.Length <= MaximumIdLength, "Library id is too long.");
        _ = reader.ReadInt32();
        int segmentSize = reader.ReadInt32();
        int segmentCount = reader.ReadInt32();
        int frameCount = reader.ReadInt32();
        Require(segmentSize > 0 && segmentCount >= 0 &&
                segmentCount <= StreamingAssetV3Constants.MaxLibraryIndexSegments &&
                frameCount >= 0 && frameCount <= MaximumAssetCount, "Invalid V3 library index header.");
        long length = input.Position + (long)segmentCount * 40 + (long)frameCount * 35;
        Require(length > 0 && length <= data.Length, "V3 library index header exceeds its block.");
        return (int)length;
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.Optimal, true)) brotli.Write(raw);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed, int expectedLength)
    {
        if (expectedLength < 0 || expectedLength > MaximumRecordLength)
            throw new InvalidDataException("Invalid library index segment length.");
        byte[] output = new byte[expectedLength];
        using MemoryStream input = new(compressed, false);
        using BrotliStream brotli = new(input, CompressionMode.Decompress);
        int total = 0;
        while (total < output.Length)
        {
            int read = brotli.Read(output, total, output.Length - total);
            if (read == 0) break;
            total += read;
        }
        Require(total == output.Length && brotli.ReadByte() == -1, "Unexpected library index segment size.");
        return output;
    }

    /// <summary>
    /// Builds a self-describing map pack. Layout (little-endian):
    /// magic, formatVersion, width, height, chunkSize, chunkCount,
    /// chunkCount * (compressedLength, uncompressedLength), then the payloads back to back.
    /// Chunks are stored column-major: chunk i covers X = i / rows * chunkSize, Y = i % rows * chunkSize.
    /// </summary>
    public static byte[] WriteMapPack(int width, int height, int chunkSize,
        IReadOnlyList<byte[]> compressedChunks, IReadOnlyList<int> uncompressedLengths)
    {
        ArgumentNullException.ThrowIfNull(compressedChunks);
        ArgumentNullException.ThrowIfNull(uncompressedLengths);
        if (width <= 0 || height <= 0 || width > 100_000 || height > 100_000)
            throw new InvalidDataException("Invalid map dimensions.");
        if (chunkSize <= 0 || chunkSize > 1024) throw new InvalidDataException("Invalid map chunk size.");
        if (compressedChunks.Count != uncompressedLengths.Count)
            throw new InvalidDataException("Map chunk payload and length counts differ.");

        int columns = (width + chunkSize - 1) / chunkSize;
        int rows = (height + chunkSize - 1) / chunkSize;
        if ((long)columns * rows != compressedChunks.Count)
            throw new InvalidDataException("Map chunks do not cover the complete map.");

        using MemoryStream output = new();
        using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetV3Constants.MapPackMagic);
            writer.Write(StreamingAssetV3Constants.FormatVersion);
            writer.Write(width);
            writer.Write(height);
            writer.Write(chunkSize);
            writer.Write(compressedChunks.Count);

            for (int i = 0; i < compressedChunks.Count; i++)
            {
                byte[] payload = compressedChunks[i] ?? throw new InvalidDataException($"Missing map chunk payload at {i}.");
                int chunkX = i / rows * chunkSize;
                int chunkY = i % rows * chunkSize;
                int expected = checked(20 + Math.Min(chunkSize, width - chunkX) * Math.Min(chunkSize, height - chunkY) * 32);
                if (uncompressedLengths[i] != expected)
                    throw new InvalidDataException($"Map chunk {i} declares an unexpected uncompressed length.");
                if (payload.Length <= 0 || payload.Length > MaximumRecordLength)
                    throw new InvalidDataException($"Map chunk {i} has an invalid payload length.");
                writer.Write(payload.Length);
                writer.Write(uncompressedLengths[i]);
            }

            foreach (byte[] payload in compressedChunks) writer.Write(payload);
        }

        return output.ToArray();
    }

    public static StreamingMapChunk ReadMapChunk(byte[] data, int offset, int length,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight, int uncompressedLength)
    {
        ArgumentNullException.ThrowIfNull(data);
        Require(offset >= 0 && length > 0 && length <= data.Length - offset, "Map chunk range exceeds its pack.");
        byte[] raw = DecompressExact(data, offset, length, uncompressedLength);
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
        Require(chunk.X == expectedX && chunk.Y == expectedY && chunk.Width == expectedWidth &&
                chunk.Height == expectedHeight && count == checked(expectedWidth * expectedHeight),
            "Map chunk header does not match its pack directory.");

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
        return DecompressExact(compressed, 0, compressed.Length, expectedLength);
    }

    public static byte[] DecompressExact(byte[] compressed, int offset, int count, int expectedLength)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (offset < 0 || count < 0 || count > compressed.Length - offset)
            throw new InvalidDataException("Invalid compressed payload range.");
        if (expectedLength < 0 || expectedLength > MaximumRecordLength)
            throw new InvalidDataException("Invalid decompressed image length.");

        byte[] output = new byte[expectedLength];
        using MemoryStream input = new(compressed, offset, count, false);
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

    public static string GetWorkingSetPath(string hash) =>
        GetHashedPath(StreamingAssetV3Constants.WorkingSetsDirectory, hash, ".wsp");

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
