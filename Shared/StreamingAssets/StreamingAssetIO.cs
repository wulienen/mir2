using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shared.StreamingAssets;

public static class StreamingAssetIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, true))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    public static byte[] Decompress(byte[] data)
    {
        using MemoryStream input = new(data);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string ComputeSha256(Stream stream)
    {
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        return hash;
    }

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    public static T ReadJson<T>(string path)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    public static bool IsValidSha256(string hash)
    {
        if (hash == null || hash.Length != 64) return false;

        foreach (char value in hash)
        {
            if (!Uri.IsHexDigit(value)) return false;
        }

        return true;
    }

    public static string GetObjectRelativePath(string hash)
    {
        if (!IsValidSha256(hash))
            throw new ArgumentException("Expected a SHA-256 hash.", nameof(hash));

        string normalized = hash.ToLowerInvariant();
        return $"{StreamingAssetConstants.ObjectsDirectory}/{normalized[..2]}/{normalized}.bin";
    }

    public static byte[] WriteLibraryIndexPage(LibraryManifestPage page)
    {
        using MemoryStream output = new();
        using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetConstants.LibraryIndexPageMagic);
            writer.Write(StreamingAssetConstants.CurrentFormatVersion);
            writer.Write(page.PageIndex);
            writer.Write(page.Start);
            writer.Write(page.Images.Count);

            foreach (LibraryImageRecord image in page.Images)
            {
                writer.Write(image.Index);
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(image.X);
                writer.Write(image.Y);
                writer.Write(image.Length);

                byte[] hash = image.Exists ? Convert.FromHexString(image.Hash) : new byte[32];
                if (hash.Length != 32)
                    throw new InvalidDataException($"Invalid image hash at index {image.Index}.");
                writer.Write(hash);
            }
        }

        return output.ToArray();
    }

    public static LibraryManifestPage ReadLibraryIndexPage(byte[] data)
    {
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input);

        if (reader.ReadInt32() != StreamingAssetConstants.LibraryIndexPageMagic)
            throw new InvalidDataException("Invalid library index page header.");
        if (reader.ReadInt32() != StreamingAssetConstants.CurrentFormatVersion)
            throw new InvalidDataException("Unsupported library index page version.");

        LibraryManifestPage page = new()
        {
            PageIndex = reader.ReadInt32(),
            Start = reader.ReadInt32()
        };

        int count = reader.ReadInt32();
        if (page.PageIndex < 0 || page.Start < 0 || count < 0 || count > StreamingAssetConstants.LibraryIndexPageSize)
            throw new InvalidDataException("Invalid library index page bounds.");

        const int recordLength = sizeof(int) + sizeof(short) * 4 + sizeof(long) + 32;
        if (input.Length - input.Position != (long)count * recordLength)
            throw new InvalidDataException("Invalid library index page length.");

        for (int i = 0; i < count; i++)
        {
            byte[] hashBytes;
            LibraryImageRecord image = new()
            {
                Index = reader.ReadInt32(),
                Width = reader.ReadInt16(),
                Height = reader.ReadInt16(),
                X = reader.ReadInt16(),
                Y = reader.ReadInt16(),
                Length = reader.ReadInt64()
            };
            hashBytes = reader.ReadBytes(32);
            if (hashBytes.Any(value => value != 0))
                image.Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            page.Images.Add(image);
        }

        return page;
    }

    public static byte[] WriteLibraryImageChunk(
        short width,
        short height,
        short x,
        short y,
        short shadowX,
        short shadowY,
        byte shadow,
        byte[] imageData,
        short maskWidth,
        short maskHeight,
        short maskX,
        short maskY,
        byte[] maskData)
    {
        using MemoryStream raw = new();
        using (BinaryWriter writer = new(raw, System.Text.Encoding.UTF8, true))
        {
            writer.Write(width);
            writer.Write(height);
            writer.Write(x);
            writer.Write(y);
            writer.Write(shadowX);
            writer.Write(shadowY);
            writer.Write(shadow);
            writer.Write(imageData.Length);
            writer.Write(imageData);

            bool hasMask = maskData != null && maskData.Length > 0;
            writer.Write(hasMask);
            if (hasMask)
            {
                writer.Write(maskWidth);
                writer.Write(maskHeight);
                writer.Write(maskX);
                writer.Write(maskY);
                writer.Write(maskData.Length);
                writer.Write(maskData);
            }
        }

        return Compress(raw.ToArray());
    }

    public static StreamingLibraryImageChunk ReadLibraryImageChunk(byte[] compressed)
    {
        byte[] raw = Decompress(compressed);
        using MemoryStream input = new(raw);
        using BinaryReader reader = new(input);

        StreamingLibraryImageChunk chunk = new()
        {
            Width = reader.ReadInt16(),
            Height = reader.ReadInt16(),
            X = reader.ReadInt16(),
            Y = reader.ReadInt16(),
            ShadowX = reader.ReadInt16(),
            ShadowY = reader.ReadInt16(),
            Shadow = reader.ReadByte()
        };

        int length = reader.ReadInt32();
        chunk.ImageData = reader.ReadBytes(length);

        chunk.HasMask = reader.ReadBoolean();
        if (chunk.HasMask)
        {
            chunk.MaskWidth = reader.ReadInt16();
            chunk.MaskHeight = reader.ReadInt16();
            chunk.MaskX = reader.ReadInt16();
            chunk.MaskY = reader.ReadInt16();
            int maskLength = reader.ReadInt32();
            chunk.MaskData = reader.ReadBytes(maskLength);
        }

        return chunk;
    }

    public static byte[] WriteMapChunk(StreamingMapChunk chunk)
    {
        using MemoryStream raw = new();
        using (BinaryWriter writer = new(raw, System.Text.Encoding.UTF8, true))
        {
            writer.Write(chunk.X);
            writer.Write(chunk.Y);
            writer.Write(chunk.Width);
            writer.Write(chunk.Height);
            writer.Write(chunk.Cells.Length);

            foreach (StreamingMapCell cell in chunk.Cells)
            {
                writer.Write(cell.BackIndex);
                writer.Write(cell.BackImage);
                writer.Write(cell.MiddleIndex);
                writer.Write(cell.MiddleImage);
                writer.Write(cell.FrontIndex);
                writer.Write(cell.FrontImage);
                writer.Write(cell.DoorIndex);
                writer.Write(cell.DoorOffset);
                writer.Write(cell.FrontAnimationFrame);
                writer.Write(cell.FrontAnimationTick);
                writer.Write(cell.MiddleAnimationFrame);
                writer.Write(cell.MiddleAnimationTick);
                writer.Write(cell.TileAnimationImage);
                writer.Write(cell.TileAnimationOffset);
                writer.Write(cell.TileAnimationFrames);
                writer.Write(cell.Light);
                writer.Write(cell.Unknown);
                writer.Write(cell.FishingCell);
            }
        }

        return Compress(raw.ToArray());
    }

    public static StreamingMapChunk ReadMapChunk(byte[] compressed)
    {
        byte[] raw = Decompress(compressed);
        using MemoryStream input = new(raw);
        using BinaryReader reader = new(input);

        StreamingMapChunk chunk = new()
        {
            X = reader.ReadInt32(),
            Y = reader.ReadInt32(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32()
        };

        int count = reader.ReadInt32();
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

        return chunk;
    }
}

public sealed class StreamingLibraryImageChunk
{
    public short Width { get; set; }
    public short Height { get; set; }
    public short X { get; set; }
    public short Y { get; set; }
    public short ShadowX { get; set; }
    public short ShadowY { get; set; }
    public byte Shadow { get; set; }
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public bool HasMask { get; set; }
    public short MaskWidth { get; set; }
    public short MaskHeight { get; set; }
    public short MaskX { get; set; }
    public short MaskY { get; set; }
    public byte[] MaskData { get; set; } = Array.Empty<byte>();
}
