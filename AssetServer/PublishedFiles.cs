using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace AssetServer;

/// <summary>
/// Handle pool for published, content-addressed files. A file name contains the SHA-256 of its contents,
/// so a published file never changes and an open handle stays valid for as long as the file exists. The
/// first entry into the game is a burst of small ranged reads over a handful of large Lib files, and
/// opening and closing the file per request dominated that path. Handles are opened with
/// <see cref="FileShare.Delete"/> so a republish or <c>--prune</c> can still remove the file underneath.
/// </summary>
internal static class PublishedFiles
{
    private const int MaxHandles = 256;

    private static readonly ConcurrentDictionary<string, PublishedFile> Files = new(StringComparer.OrdinalIgnoreCase);

    public static PublishedFile TryGet(string path)
    {
        if (Files.TryGetValue(path, out PublishedFile existing))
        {
            existing.Touch();
            return existing;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        PublishedFile created = new(path, handle, info.Length);
        PublishedFile winner = Files.GetOrAdd(path, created);
        if (!ReferenceEquals(winner, created)) created.Dispose();
        if (Files.Count > MaxHandles) EvictOldest();
        winner.Touch();
        return winner;
    }

    /// <summary>Drops the least recently used handles. Immutable files make this free of correctness risk.</summary>
    private static void EvictOldest()
    {
        foreach (PublishedFile file in Files.Values
                     .OrderBy(item => item.LastUseTicks)
                     .Take(Math.Max(1, Files.Count - MaxHandles)))
        {
            if (Files.TryRemove(file.Path, out PublishedFile removed)) removed.Dispose();
        }
    }
}

internal sealed class PublishedFile : IDisposable
{
    private long _lastUseTicks;

    public PublishedFile(string path, SafeFileHandle handle, long length)
    {
        Path = path;
        Handle = handle;
        Length = length;
        _lastUseTicks = DateTime.UtcNow.Ticks;
    }

    public string Path { get; }
    public SafeFileHandle Handle { get; }
    public long Length { get; }
    public long LastUseTicks => Interlocked.Read(ref _lastUseTicks);

    public void Touch() => Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks);

    public void Dispose() => Handle.Dispose();
}
