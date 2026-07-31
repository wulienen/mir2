using System.Collections.Concurrent;
using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Owns the on-disk streaming cache: one sparse container per library plus a plain-file blob cache
/// for catalog blocks, map packs and sounds. Replaces the previous single SQLite database, whose
/// global lock was queried from the render thread on every draw call.
/// </summary>
public sealed class AssetCacheStore : IDisposable
{
    /// <summary>Trim only ran when a manifest was applied, so a long session could grow past the budget.</summary>
    private const long TrimGrowthThreshold = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, LibraryCacheContainer> _containers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinnedLibraries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinnedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _evictSync = new();
    private readonly string _root;
    private readonly string _librariesRoot;
    private long _maxBytes;
    private long _pendingGrowth;
    private int _trimScheduled;
    private bool _disposed;

    public AssetCacheStore(string root, int maxMegabytes, IEnumerable<string> pinnedLibraries)
    {
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? @".\Cache\AssetsV3" : root);
        _librariesRoot = Path.Combine(_root, "libs");
        _maxBytes = Math.Max(256L, maxMegabytes) * 1024L * 1024L;
        Directory.CreateDirectory(_librariesRoot);
        Blobs = new BlobCache(_root);
        if (pinnedLibraries != null)
            foreach (string id in pinnedLibraries)
            {
                _pinnedLibraries.Add(id);
                // Containers left on disk by an earlier run are matched by path, not by id.
                if (LibraryCacheContainer.TryGetRelativePath(id, out string relative))
                    _pinnedPaths.Add(Path.Combine(_librariesRoot, relative + ".bits"));
            }
    }

    public BlobCache Blobs { get; }
    public string Root => _root;

    public LibraryCacheContainer GetContainer(V3LibraryRecord record)
    {
        if (record == null || _disposed) return null;
        if (_containers.TryGetValue(record.Id, out LibraryCacheContainer existing))
        {
            if (existing.FileLength == record.FileLength && existing.ImageCount == record.ImageCount)
            {
                existing.Touch();
                return existing;
            }
            // Published Lib changed while running: drop the stale container and reopen against the new record.
            if (_containers.TryRemove(record.Id, out LibraryCacheContainer stale)) stale.Dispose();
        }

        try
        {
            LibraryCacheContainer created = LibraryCacheContainer.Open(_librariesRoot, record);
            LibraryCacheContainer winner = _containers.GetOrAdd(record.Id, created);
            if (!ReferenceEquals(winner, created)) created.Dispose();
            winner.Touch();
            return winner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Accumulates newly stored bytes and kicks off a background trim once enough has been written. Without
    /// this the budget was only ever enforced when a manifest was applied, which for a running client meant
    /// once at startup.
    /// </summary>
    public void NoteGrowth(long bytes)
    {
        if (bytes <= 0 || _disposed) return;
        if (Interlocked.Add(ref _pendingGrowth, bytes) < TrimGrowthThreshold) return;
        if (Interlocked.Exchange(ref _trimScheduled, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try { Trim(); }
            catch (Exception ex) { CMain.SaveError($"Streaming cache trim failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _trimScheduled, 0); }
        });
    }

    public long GetLibraryBytes()
    {
        long total = 0;
        foreach (CacheEntry entry in CollectEntries()) total += entry.Bytes;
        return total;
    }

    /// <summary>
    /// Container-level eviction. Ranges inside a sparse container cannot be released individually, so the
    /// unit of eviction is a whole library. Startup libraries are never evicted and the least recently used
    /// container goes first: evicting the largest container instead threw away the map tile library that the
    /// player is standing in, which then had to be re-downloaded immediately.
    /// </summary>
    public void Trim() => TrimTo(_maxBytes);

    /// <summary>Trims against an explicit budget. Only the self-test passes anything but the configured one.</summary>
    internal void TrimTo(long maxBytes)
    {
        if (_disposed) return;
        lock (_evictSync)
        {
            Interlocked.Exchange(ref _pendingGrowth, 0);
            long blobBudget = Math.Max(64L * 1024 * 1024, maxBytes / 8);
            long blobBytes = Blobs.Trim(blobBudget, null);
            long budget = maxBytes - blobBytes;

            List<CacheEntry> entries = CollectEntries();
            long libraryBytes = 0;
            foreach (CacheEntry entry in entries) libraryBytes += entry.Bytes;
            if (libraryBytes <= budget) return;

            foreach (CacheEntry entry in entries
                         .Where(item => !item.Pinned && item.Bytes > 0)
                         .OrderBy(item => item.LastUseTicks))
            {
                if (libraryBytes <= budget) break;
                if (entry.Container != null)
                {
                    if (!_containers.TryRemove(entry.Container.Id, out LibraryCacheContainer removed)) continue;
                    removed.Delete();
                }
                else
                {
                    TryDelete(entry.BitsPath);
                    TryDelete(Path.ChangeExtension(entry.BitsPath, ".libpart"));
                }
                libraryBytes -= entry.Bytes;
            }
        }
    }

    /// <summary>
    /// Every container that counts against the budget: the ones this run has opened, plus the sidecars left
    /// on disk by earlier runs. Sidecars that cannot be parsed carry no usable data — the container would be
    /// reset on the next open — so they are deleted here rather than counted.
    /// </summary>
    private List<CacheEntry> CollectEntries()
    {
        List<CacheEntry> entries = new();
        HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryCacheContainer container in _containers.Values)
        {
            open.Add(container.BitsPath);
            entries.Add(new CacheEntry(container, container.BitsPath, container.StoredBytes,
                container.LastUseTicks, _pinnedLibraries.Contains(container.Id)));
        }

        try
        {
            foreach (string bits in Directory.EnumerateFiles(_librariesRoot, "*.bits", SearchOption.AllDirectories))
            {
                if (open.Contains(bits)) continue;
                string data = Path.ChangeExtension(bits, ".libpart");
                if (!File.Exists(data))
                {
                    TryDelete(bits);
                    continue;
                }
                if (!LibraryCacheContainer.TryReadSidecar(bits, out long stored, out long ticks))
                {
                    TryDelete(bits);
                    TryDelete(data);
                    continue;
                }
                entries.Add(new CacheEntry(null, bits, stored, ticks, _pinnedPaths.Contains(bits)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return entries;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly record struct CacheEntry(LibraryCacheContainer Container, string BitsPath, long Bytes,
        long LastUseTicks, bool Pinned);

    /// <summary>Removes container files for libraries that are no longer published.</summary>
    public void RemoveOrphans(ICollection<string> knownLibraryIds)
    {
        if (knownLibraryIds == null || knownLibraryIds.Count == 0) return;
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in knownLibraryIds)
            if (LibraryCacheContainer.TryGetRelativePath(id, out string relative))
                expected.Add(Path.Combine(_librariesRoot, relative + ".libpart"));

        try
        {
            foreach (string file in Directory.EnumerateFiles(_librariesRoot, "*.libpart", SearchOption.AllDirectories))
            {
                if (expected.Contains(file)) continue;
                try
                {
                    File.Delete(file);
                    string bits = Path.ChangeExtension(file, ".bits");
                    if (File.Exists(bits)) File.Delete(bits);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (LibraryCacheContainer container in _containers.Values) container.Dispose();
        _containers.Clear();
    }
}
