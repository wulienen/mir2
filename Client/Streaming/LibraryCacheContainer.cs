using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Per-library on-disk cache. The container is a sparse file the exact length of the published
/// <c>.Lib</c>; downloaded image records are written back at their original Lib offsets, so a read is a
/// single positional read with no lock, transaction or hashing on the render thread. A sidecar
/// <c>.bits</c> file records which images are present, how many bytes are stored, when the container was
/// last used, and pins the container to one published Lib version.
/// </summary>
public sealed class LibraryCacheContainer : IDisposable
{
    private const int BitsMagic = 0x34424C59; // YLB4
    private const int BitsHeaderSize = 72;
    private const int StoredBytesOffset = 52;
    private const int CleanFlagOffset = 60;
    private const int LastUseOffset = 64;
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>The last-use stamp only drives cache eviction, so it is persisted at most this often.</summary>
    private static readonly long PersistIntervalTicks = TimeSpan.FromMinutes(5).Ticks;

    private readonly object _writeSync = new();
    private readonly string _dataPath;
    private readonly string _bitsPath;
    private readonly byte[] _bits;
    private byte[] _unverified;

    private SafeFileHandle _data;
    private FileStream _bitsStream;
    private long _storedBytes;
    private long _lastUseTicks;
    private long _persistedUseTicks;
    private bool _disposed;

    private LibraryCacheContainer(string id, string dataPath, string bitsPath, SafeFileHandle data,
        FileStream bitsStream, byte[] bits, byte[] unverified, long storedBytes, long lastUseTicks,
        long fileLength, int imageCount)
    {
        Id = id;
        _dataPath = dataPath;
        _bitsPath = bitsPath;
        _data = data;
        _bitsStream = bitsStream;
        _bits = bits;
        _unverified = unverified;
        _storedBytes = storedBytes;
        _lastUseTicks = lastUseTicks;
        _persistedUseTicks = lastUseTicks;
        FileLength = fileLength;
        ImageCount = imageCount;
    }

    public string Id { get; }
    public long FileLength { get; }
    public int ImageCount { get; }
    public long StoredBytes => Interlocked.Read(ref _storedBytes);
    public long LastUseTicks => Interlocked.Read(ref _lastUseTicks);
    public string DataPath => _dataPath;
    public string BitsPath => _bitsPath;

    /// <summary>
    /// Marks the container as in use for eviction purposes. Called whenever a caller asks for the
    /// container, which is far more often than the stamp needs to reach the disk, so the sidecar write is
    /// rate limited to one per <see cref="PersistIntervalTicks"/>.
    /// </summary>
    public void Touch()
    {
        long now = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastUseTicks, now);
        if (now - Interlocked.Read(ref _persistedUseTicks) < PersistIntervalTicks) return;

        lock (_writeSync)
        {
            if (_disposed || now - _persistedUseTicks < PersistIntervalTicks) return;
            _persistedUseTicks = now;
            WriteLastUse(now);
        }
    }

    public static LibraryCacheContainer Open(string root, V3LibraryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryGetRelativePath(record.Id, out string relative))
            throw new InvalidDataException($"Library id '{record.Id}' is not a safe cache path.");
        if (record.ImageCount < 0 || record.FileLength <= 0 || !StreamingAssetIO.IsValidSha256(record.FileHash))
            throw new InvalidDataException($"Library '{record.Id}' has an invalid manifest record.");

        string dataPath = Path.Combine(root, relative + ".libpart");
        string bitsPath = Path.Combine(root, relative + ".bits");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath));

        int bitmapBytes = (record.ImageCount + 7) / 8;
        byte[] hash = Convert.FromHexString(record.FileHash);
        byte[] bits = new byte[bitmapBytes];
        byte[] unverified = null;
        long storedBytes = 0;
        long lastUseTicks = DateTime.UtcNow.Ticks;
        bool reset = true;

        if (File.Exists(bitsPath) && File.Exists(dataPath))
        {
            try
            {
                byte[] existing = File.ReadAllBytes(bitsPath);
                if (existing.Length == BitsHeaderSize + bitmapBytes &&
                    ReadInt32(existing, 0) == BitsMagic &&
                    ReadInt32(existing, 4) == StreamingAssetV3Constants.FormatVersion &&
                    ReadInt32(existing, 8) == record.ImageCount &&
                    ReadInt64(existing, 12) == record.FileLength &&
                    existing.AsSpan(20, 32).SequenceEqual(hash) &&
                    new FileInfo(dataPath).Length == record.FileLength)
                {
                    Buffer.BlockCopy(existing, BitsHeaderSize, bits, 0, bitmapBytes);
                    storedBytes = ReadInt64(existing, StoredBytesOffset);
                    if (existing[CleanFlagOffset] == 0)
                    {
                        // Previous run did not shut down cleanly: every present record is verified once on
                        // first read before it is trusted.
                        unverified = (byte[])bits.Clone();
                    }
                    reset = false;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (reset)
        {
            Array.Clear(bits);
            storedBytes = 0;
            unverified = null;
            TryDelete(dataPath);
            TryDelete(bitsPath);
        }

        SafeFileHandle data = File.OpenHandle(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite, FileOptions.None);
        try
        {
            if (reset)
            {
                TrySetSparse(data);
                RandomAccess.SetLength(data, record.FileLength);
            }
        }
        catch
        {
            data.Dispose();
            throw;
        }

        FileStream bitsStream = new(bitsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        LibraryCacheContainer container = new(record.Id, dataPath, bitsPath, data, bitsStream, bits, unverified,
            storedBytes, lastUseTicks, record.FileLength, record.ImageCount);
        container.WriteBitsHeader(hash, clean: false, bitmapBytes);
        return container;
    }

    /// <summary>
    /// Reads the eviction-relevant fields of a sidecar without opening the container. Cache trimming has to
    /// account for containers left on disk by earlier runs, which are the bulk of the cache right after a
    /// start; ignoring them made the configured budget meaningless.
    /// </summary>
    public static bool TryReadSidecar(string bitsPath, out long storedBytes, out long lastUseTicks)
    {
        storedBytes = 0;
        lastUseTicks = 0;
        try
        {
            using FileStream stream = new(bitsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < BitsHeaderSize) return false;
            byte[] header = new byte[BitsHeaderSize];
            int total = 0;
            while (total < header.Length)
            {
                int read = stream.Read(header, total, header.Length - total);
                if (read <= 0) return false;
                total += read;
            }
            if (ReadInt32(header, 0) != BitsMagic ||
                ReadInt32(header, 4) != StreamingAssetV3Constants.FormatVersion) return false;

            storedBytes = Math.Max(0, ReadInt64(header, StoredBytesOffset));
            lastUseTicks = ReadInt64(header, LastUseOffset);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool IsPresent(int index)
    {
        if (index < 0 || index >= ImageCount) return false;
        return (Volatile.Read(ref _bits[index >> 3]) & (1 << (index & 7))) != 0;
    }

    public bool TryRead(V3LibraryImageRecord record, out byte[] bytes)
    {
        bytes = null;
        if (record == null || _disposed || !IsPresent(record.Index)) return false;
        if (record.Offset < 0 || record.Length <= 0 || record.Offset + record.Length > FileLength) return false;

        byte[] buffer = new byte[record.Length];
        try
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(_data, buffer.AsSpan(total), record.Offset + total);
                if (read <= 0) return false;
                total += read;
            }
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }

        byte[] unverified = _unverified;
        if (unverified != null)
        {
            bool needsCheck;
            lock (_writeSync)
            {
                int mask = 1 << (record.Index & 7);
                needsCheck = (unverified[record.Index >> 3] & mask) != 0;
                if (needsCheck) unverified[record.Index >> 3] &= (byte)~mask;
            }
            // No per-image hash exists in the catalog any more, so recovery re-checks the record
            // structurally: it must parse as exactly one Lib image record matching the catalog metadata.
            if (needsCheck && !StreamingAssetV3IO.IsLibraryImageRecordValid(record, buffer))
            {
                ClearBit(record.Index, record.Length);
                return false;
            }
        }

        bytes = buffer;
        return true;
    }

    /// <summary>
    /// Writes one image's bytes at its offset in the published Lib, then sets the present bit.
    ///
    /// There is deliberately no per-record device flush here. <c>FlushFileBuffers</c> measured 28 ms per
    /// call on a 7200 rpm disk, which turned entering a map into half a minute of pure fsync and starved
    /// the render thread's own reads. Durability comes from the sidecar's clean flag instead: it is 0 for
    /// the whole run, so any hard kill makes the next start treat every present record as unverified and
    /// structurally validate it on first read. A torn or missing payload is rejected and re-downloaded,
    /// which is exactly what the flush was protecting against. <see cref="Dispose"/> flushes once.
    /// </summary>
    public void Write(V3LibraryImageRecord record, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(bytes);
        if (_disposed || record.Index < 0 || record.Index >= ImageCount) return;
        if (record.Offset < 0 || bytes.Length != record.Length || record.Offset + bytes.Length > FileLength) return;

        try
        {
            RandomAccess.Write(_data, bytes, record.Offset);
        }
        catch (IOException) { return; }
        catch (ObjectDisposedException) { return; }

        SetBit(record.Index, bytes.Length);
    }

    public void Flush()
    {
        lock (_writeSync)
        {
            if (_disposed) return;
            try { _bitsStream.Flush(true); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        lock (_writeSync)
        {
            if (_disposed) return;
            _disposed = true;
            // The one device flush of this container's payloads, ordered before the clean flag: the flag
            // promises the next start that every present record is durable, so it must not be set first.
            try { RandomAccess.FlushToDisk(_data); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            try
            {
                _bitsStream.Position = CleanFlagOffset;
                _bitsStream.WriteByte(1);
                _bitsStream.Position = StoredBytesOffset;
                _bitsStream.Write(BitConverter.GetBytes(Interlocked.Read(ref _storedBytes)));
                _bitsStream.Position = LastUseOffset;
                _bitsStream.Write(BitConverter.GetBytes(Interlocked.Read(ref _lastUseTicks)));
                _bitsStream.Flush(true);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            _bitsStream.Dispose();
            _data.Dispose();
        }
    }

    /// <summary>Deletes both container files. Used by cache eviction; the container must be disposed first.</summary>
    public void Delete()
    {
        Dispose();
        TryDelete(_dataPath);
        TryDelete(_bitsPath);
    }

    public static bool TryGetRelativePath(string id, out string relative)
    {
        relative = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512) return false;

        string normalized = id.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return false;

        string[] parts = normalized.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..") return false;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)) return false;
        }

        relative = string.Join(Path.DirectorySeparatorChar, parts);
        return true;
    }

    private void SetBit(int index, int length)
    {
        lock (_writeSync)
        {
            if (_disposed) return;
            int mask = 1 << (index & 7);
            if ((_bits[index >> 3] & mask) != 0) return;
            _bits[index >> 3] |= (byte)mask;
            Interlocked.Add(ref _storedBytes, length);
            WriteBitThrough(index);
        }
    }

    private void ClearBit(int index, int length)
    {
        lock (_writeSync)
        {
            if (_disposed) return;
            int mask = 1 << (index & 7);
            if ((_bits[index >> 3] & mask) == 0) return;
            _bits[index >> 3] &= (byte)~mask;
            Interlocked.Add(ref _storedBytes, -length);
            WriteBitThrough(index);
        }
    }

    private void WriteBitThrough(int index)
    {
        try
        {
            _bitsStream.Position = BitsHeaderSize + (index >> 3);
            _bitsStream.WriteByte(_bits[index >> 3]);
            _bitsStream.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WriteBitsHeader(byte[] hash, bool clean, int bitmapBytes)
    {
        lock (_writeSync)
        {
            byte[] header = new byte[BitsHeaderSize];
            BitConverter.TryWriteBytes(header.AsSpan(0), BitsMagic);
            BitConverter.TryWriteBytes(header.AsSpan(4), StreamingAssetV3Constants.FormatVersion);
            BitConverter.TryWriteBytes(header.AsSpan(8), ImageCount);
            BitConverter.TryWriteBytes(header.AsSpan(12), FileLength);
            hash.CopyTo(header, 20);
            BitConverter.TryWriteBytes(header.AsSpan(StoredBytesOffset), Interlocked.Read(ref _storedBytes));
            header[CleanFlagOffset] = clean ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(header.AsSpan(LastUseOffset), Interlocked.Read(ref _lastUseTicks));

            _bitsStream.SetLength(BitsHeaderSize + bitmapBytes);
            _bitsStream.Position = 0;
            _bitsStream.Write(header);
            _bitsStream.Write(_bits, 0, bitmapBytes);
            _bitsStream.Flush(true);
        }
    }

    /// <summary>Caller holds <see cref="_writeSync"/> and has checked <see cref="_disposed"/>.</summary>
    private void WriteLastUse(long ticks)
    {
        try
        {
            _bitsStream.Position = LastUseOffset;
            _bitsStream.Write(BitConverter.GetBytes(ticks));
            _bitsStream.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void TrySetSparse(SafeFileHandle handle)
    {
        try
        {
            DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int ReadInt32(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
    private static long ReadInt64(byte[] data, int offset) => BitConverter.ToInt64(data, offset);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode,
        IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
