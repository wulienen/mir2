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

    public static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string ComputeSha256(Stream stream)
    {
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (stream.CanSeek) stream.Position = originalPosition;
        return hash;
    }

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    public static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);

    public static bool IsValidSha256(string hash)
    {
        if (hash == null || hash.Length != 64) return false;
        foreach (char value in hash)
            if (!Uri.IsHexDigit(value)) return false;
        return true;
    }

    public static byte[] WriteMapChunk(StreamingMapChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Width <= 0 || chunk.Height <= 0 ||
            chunk.Cells?.Length != checked(chunk.Width * chunk.Height))
            throw new InvalidDataException("Invalid map chunk dimensions.");

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

        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, true))
            gzip.Write(raw.GetBuffer(), 0, checked((int)raw.Length));
        return output.ToArray();
    }
}
