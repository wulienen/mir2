using System.Security.Cryptography;
using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Plain-file cache for the few whole-object assets: catalog index blocks, map packs and sounds.
/// Each object is one immutable content-addressed file, so a hit is a single file read with no
/// database, lock or transaction. Objects here are read once per session and then held in memory by
/// the caller, so verification happens on every read.
/// </summary>
public sealed class BlobCache
{
    private readonly object _writeSync = new();
    private readonly string _root;
    private readonly string _metadataRoot;

    public BlobCache(string root)
    {
        _root = Path.Combine(root, "blobs");
        _metadataRoot = root;
        Directory.CreateDirectory(_root);
    }

    public bool TryGet(string hash, long expectedLength, out byte[] bytes)
    {
        bytes = null;
        if (!StreamingAssetIO.IsValidSha256(hash)) return false;
        string path = GetPath(hash);

        byte[] payload;
        try
        {
            if (!File.Exists(path)) return false;
            payload = File.ReadAllBytes(path);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        if ((expectedLength >= 0 && payload.Length != expectedLength) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), hash,
                StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            return false;
        }

        Touch(path);
        bytes = payload;
        return true;
    }

    public void Put(string hash, byte[] bytes)
    {
        if (!StreamingAssetIO.IsValidSha256(hash) || bytes == null) return;
        string path = GetPath(hash);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            lock (_writeSync)
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool TryGetMetadata(string name, out byte[] bytes)
    {
        bytes = null;
        if (!IsSafeMetadataName(name)) return false;
        try
        {
            string path = Path.Combine(_metadataRoot, name);
            if (!File.Exists(path)) return false;
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void PutMetadata(string name, byte[] bytes)
    {
        if (!IsSafeMetadataName(name) || bytes == null) return;
        try
        {
            Directory.CreateDirectory(_metadataRoot);
            string path = Path.Combine(_metadataRoot, name);
            string temp = path + ".tmp";
            lock (_writeSync)
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public long GetTotalBytes()
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(_root, "*.bin", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch (IOException) { }
            }
            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Drops least-recently-used blobs until the directory fits in <paramref name="maxBytes"/>.</summary>
    public long Trim(long maxBytes, ISet<string> pinned)
    {
        if (maxBytes <= 0) return 0;
        List<(string Path, long Length, DateTime Access)> entries = new();
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*.bin", SearchOption.AllDirectories))
            {
                try
                {
                    FileInfo info = new(file);
                    if (pinned != null && pinned.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                    entries.Add((file, info.Length, info.LastAccessTimeUtc));
                    total += info.Length;
                }
                catch (IOException) { }
            }
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        if (total <= maxBytes) return total;
        entries.Sort((left, right) => left.Access.CompareTo(right.Access));
        foreach ((string path, long length, _) in entries)
        {
            if (total <= maxBytes) break;
            TryDelete(path);
            total -= length;
        }
        return total;
    }

    public void Clear()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { Directory.CreateDirectory(_root); }
        catch (IOException) { }
    }

    private string GetPath(string hash)
    {
        string normalized = hash.ToLowerInvariant();
        return Path.Combine(_root, normalized[..2], normalized + ".bin");
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsSafeMetadataName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name != "." && name != ".." && !name.Contains('/') && !name.Contains('\\');
}
