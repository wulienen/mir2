using Shared.StreamingAssets;

namespace AssetServer;

/// <summary>
/// Keeps the root manifest and its ETag in memory. The client polls the manifest on every start and after
/// every update check, and it is the only mutable file the server publishes; re-reading and re-hashing
/// almost a megabyte of JSON per request was pure waste. The cached copy is refreshed when the file's
/// write time or length changes, which is two cheap metadata calls per request.
/// </summary>
internal static class ManifestCache
{
    private static readonly object Sync = new();
    private static byte[] _bytes;
    private static string _etag;
    private static DateTime _writeUtc;
    private static long _length = -1;

    public static bool TryGet(string path, out byte[] bytes, out string etag)
    {
        bytes = null;
        etag = null;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        lock (Sync)
        {
            if (_bytes == null || info.LastWriteTimeUtc != _writeUtc || info.Length != _length)
            {
                byte[] read;
                try { read = File.ReadAllBytes(path); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }

                _bytes = read;
                _etag = "\"" + StreamingAssetIO.ComputeSha256(read) + "\"";
                _writeUtc = info.LastWriteTimeUtc;
                _length = info.Length;
            }

            bytes = _bytes;
            etag = _etag;
            return true;
        }
    }
}
