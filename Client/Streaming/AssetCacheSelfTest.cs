using System.Security.Cryptography;
using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Offline check of the on-disk streaming cache: sparse library containers, the present bitmap's survival
/// across a restart, and the blob cache's content verification. Run with <c>--asset-cache-self-test</c>.
/// </summary>
public static class AssetCacheSelfTest
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "YangfeiCrystal-AssetCache-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A cached record is now trusted structurally rather than by a per-image hash, so the payload
            // has to be a real Lib image record that matches the catalog metadata.
            V3LibraryImageRecord image = new()
            {
                Index = 3,
                Offset = 512,
                Length = 17 + 64,
                Width = 4,
                Height = 4,
                X = 1,
                Y = 2,
                ShadowX = 3,
                ShadowY = 4,
                Shadow = 1
            };
            byte[] payload = BuildImageRecord(image, 64);
            if (!StreamingAssetV3IO.IsLibraryImageRecordValid(image, payload))
                throw new InvalidDataException("A valid Lib image record was rejected.");
            byte[] tampered = (byte[])payload.Clone();
            tampered[0] ^= 0xFF;
            if (StreamingAssetV3IO.IsLibraryImageRecordValid(image, tampered))
                throw new InvalidDataException("A corrupt Lib image record was accepted.");

            V3LibraryRecord library = new()
            {
                Id = "map/wemademir2/tiles",
                ImageCount = 16,
                FileLength = 4096,
                FileHash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()
            };
            string blobHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                LibraryCacheContainer container = store.GetContainer(library)
                    ?? throw new InvalidDataException("Could not open a library container.");
                if (container.IsPresent(image.Index))
                    throw new InvalidDataException("A fresh container reported a present image.");
                if (container.TryRead(image, out _))
                    throw new InvalidDataException("A fresh container returned image bytes.");

                container.Write(image, payload);
                if (!container.IsPresent(image.Index))
                    throw new InvalidDataException("The present bit was not set after a write.");
                if (!container.TryRead(image, out byte[] read) || !read.AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The container did not return the bytes that were written.");
                if (container.StoredBytes != payload.Length)
                    throw new InvalidDataException("The container reported the wrong stored size.");

                store.Blobs.Put(blobHash, payload);
                if (!store.Blobs.TryGet(blobHash, payload.Length, out byte[] blob) ||
                    !blob.AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The blob cache did not round-trip.");
                if (store.Blobs.TryGet(blobHash, payload.Length + 1, out _))
                    throw new InvalidDataException("The blob cache accepted a wrong length.");
            }

            // Reopen: the bitmap must survive a clean shutdown, and a changed published Lib must reset it.
            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                LibraryCacheContainer container = store.GetContainer(library)
                    ?? throw new InvalidDataException("Could not reopen the library container.");
                if (!container.IsPresent(image.Index) || !container.TryRead(image, out byte[] read) ||
                    !read.AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The container did not survive a clean shutdown.");

                V3LibraryRecord republished = new()
                {
                    Id = library.Id,
                    ImageCount = library.ImageCount,
                    FileLength = library.FileLength * 2,
                    FileHash = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6 })).ToLowerInvariant()
                };
                LibraryCacheContainer reset = store.GetContainer(republished)
                    ?? throw new InvalidDataException("Could not open the republished library container.");
                if (reset.IsPresent(image.Index))
                    throw new InvalidDataException("A republished library kept its stale present bits.");
            }

            RunEvictionChecks(image, payload);
            RunWorkingSetChecks();
            RunUncleanShutdownChecks(image, payload);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Covers the client half of the first-run working set: a published pack is unpacked straight into a
    /// container, a malformed record is refused without taking the rest of the library down, and a second
    /// pass writes nothing because the present bits already cover it.
    /// </summary>
    private static void RunWorkingSetChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "YangfeiCrystal-AssetWorkSet-" + Guid.NewGuid().ToString("N"));
        V3LibraryRecord library = new()
        {
            Id = "chrsel",
            ImageCount = 8,
            FileLength = 8192,
            FileHash = Convert.ToHexString(SHA256.HashData(new byte[] { 7, 7, 7 })).ToLowerInvariant()
        };
        V3LibraryImageRecord good = new()
        {
            Index = 2, Offset = 1024, Length = 17 + 32,
            Width = 4, Height = 2, X = 5, Y = 6, ShadowX = 7, ShadowY = 8, Shadow = 2
        };
        byte[] goodBytes = BuildImageRecord(good, 32);
        // A record whose declared payload length does not match its bytes must be rejected structurally.
        byte[] badBytes = BuildImageRecord(new V3LibraryImageRecord { Width = 2, Height = 2 }, 16);
        BitConverter.TryWriteBytes(badBytes.AsSpan(13), 4096);

        byte[] pack = StreamingWorkingSetPack.Write(new[]
        {
            new WorkingSetLibrary
            {
                Id = library.Id,
                FileHash = library.FileHash,
                Entries =
                {
                    new WorkingSetEntry(good.Index, good.Offset, goodBytes.Length, goodBytes),
                    new WorkingSetEntry(5, 2048, badBytes.Length, badBytes)
                }
            }
        });

        try
        {
            StreamingWorkingSet parsed = StreamingWorkingSetPack.Parse(pack);
            WorkingSetLibraryView view = parsed.Libraries.Single();
            if (parsed.ImageCount != 2 || !string.Equals(view.FileHash, library.FileHash, StringComparison.Ordinal))
                throw new InvalidDataException("The working set pack did not round-trip.");

            using AssetCacheStore store = new(root, 256, new[] { "chrsel" });
            LibraryCacheContainer container = store.GetContainer(library)
                ?? throw new InvalidDataException("Could not open the working set container.");

            if (AssetManager.ApplyWorkingSetLibrary(view, pack, container, out long written) != 1 ||
                written != goodBytes.Length)
                throw new InvalidDataException("The working set was not applied as expected.");
            if (!container.IsPresent(good.Index) || container.IsPresent(5))
                throw new InvalidDataException("The working set set the wrong present bits.");
            if (!container.TryRead(good, out byte[] read) || !read.AsSpan().SequenceEqual(goodBytes))
                throw new InvalidDataException("The working set did not store the published bytes.");

            if (AssetManager.ApplyWorkingSetLibrary(view, pack, container, out long again) != 0 || again != 0)
                throw new InvalidDataException("The working set rewrote records that were already present.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Eviction used to see only the containers the running client had opened and threw away the largest one
    /// first, which meant the configured budget was ignored on a fresh start and the map tile library in use
    /// was the first casualty. This covers both: containers left on disk count against the budget, and the
    /// least recently used unpinned container is the one that goes.
    /// </summary>
    private static void RunEvictionChecks(V3LibraryImageRecord image, byte[] payload)
    {
        string root = Path.Combine(Path.GetTempPath(), "YangfeiCrystal-AssetEvict-" + Guid.NewGuid().ToString("N"));
        string[] ids = { "chrsel", "map/old", "map/new" };
        try
        {
            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                foreach (string id in ids)
                {
                    V3LibraryRecord record = new()
                    {
                        Id = id,
                        ImageCount = 16,
                        FileLength = 4096,
                        FileHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))
                            .ToLowerInvariant()
                    };
                    LibraryCacheContainer container = store.GetContainer(record)
                        ?? throw new InvalidDataException($"Could not open container '{id}'.");
                    container.Write(image, payload);
                }
            }

            // Stamp deterministic last-use values; UtcNow has no usable resolution for three calls in a row.
            for (int i = 0; i < ids.Length; i++)
            {
                string bits = BitsPath(root, ids[i]);
                using FileStream stream = new(bits, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Position = 64;
                stream.Write(BitConverter.GetBytes(1000L + i));
            }
            if (!LibraryCacheContainer.TryReadSidecar(BitsPath(root, "map/old"), out long stored, out long ticks) ||
                stored != payload.Length || ticks != 1001L)
                throw new InvalidDataException("A closed container's sidecar did not read back.");

            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                if (store.GetLibraryBytes() != payload.Length * 3L)
                    throw new InvalidDataException("Containers left on disk were not counted against the budget.");

                // Budget for two containers: only the oldest unpinned one may go.
                store.TrimTo(payload.Length * 2L);
                if (!File.Exists(BitsPath(root, "chrsel")) || !File.Exists(BitsPath(root, "map/new")))
                    throw new InvalidDataException("Eviction removed a container that was still within budget.");
                if (File.Exists(BitsPath(root, "map/old")) ||
                    File.Exists(Path.ChangeExtension(BitsPath(root, "map/old"), ".libpart")))
                    throw new InvalidDataException("Eviction did not remove the least recently used container.");
                if (store.GetLibraryBytes() != payload.Length * 2L)
                    throw new InvalidDataException("Eviction did not update the accounted cache size.");

                // Nothing fits any more, but a startup library is never evicted.
                store.TrimTo(0);
                if (File.Exists(BitsPath(root, "map/new")))
                    throw new InvalidDataException("Eviction spared an unpinned container with no budget left.");
                if (!File.Exists(BitsPath(root, "chrsel")))
                    throw new InvalidDataException("Eviction removed a pinned startup library.");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The per-image device flush is gone from <see cref="LibraryCacheContainer.Write"/> because it cost
    /// 28 ms per call on a spinning disk; the sidecar's clean flag plus verification on first read is what
    /// replaces it, so that path is the only thing standing between a hard kill and a corrupt cache. This
    /// covers it: a container whose clean flag is 0 must reject a present record whose payload did not
    /// survive, clear its present bit, and let the rest of the library carry on.
    /// </summary>
    private static void RunUncleanShutdownChecks(V3LibraryImageRecord image, byte[] payload)
    {
        const int cleanFlagOffset = 60;
        string root = Path.Combine(Path.GetTempPath(), "YangfeiCrystal-AssetTorn-" + Guid.NewGuid().ToString("N"));
        V3LibraryRecord library = new()
        {
            Id = "map/torn/tiles",
            ImageCount = 16,
            FileLength = 4096,
            FileHash = Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 })).ToLowerInvariant()
        };
        // A second record far enough away that tearing the first one cannot touch it.
        V3LibraryImageRecord intact = new()
        {
            Index = 9, Offset = 2048, Length = 17 + 48,
            Width = 8, Height = 6, X = 1, Y = 1, ShadowX = 2, ShadowY = 2, Shadow = 1
        };
        byte[] intactBytes = BuildImageRecord(intact, 48);

        try
        {
            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                LibraryCacheContainer container = store.GetContainer(library)
                    ?? throw new InvalidDataException("Could not open the torn-record container.");
                container.Write(image, payload);
                container.Write(intact, intactBytes);
            }

            string bits = BitsPath(root, library.Id);
            // Simulate a hard kill: clear the clean flag the orderly shutdown just wrote, and tear one
            // payload the way a lost write would. Both present bits stay set.
            using (FileStream stream = new(bits, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = cleanFlagOffset;
                stream.WriteByte(0);
            }
            using (FileStream stream = new(Path.ChangeExtension(bits, ".libpart"), FileMode.Open,
                       FileAccess.Write, FileShare.None))
            {
                stream.Position = image.Offset;
                stream.Write(new byte[image.Length]);
            }

            using (AssetCacheStore store = new(root, 256, new[] { "chrsel" }))
            {
                LibraryCacheContainer container = store.GetContainer(library)
                    ?? throw new InvalidDataException("Could not reopen the torn-record container.");
                if (!container.IsPresent(image.Index) || !container.IsPresent(intact.Index))
                    throw new InvalidDataException("An unclean shutdown threw away the whole present bitmap.");

                if (container.TryRead(image, out _))
                    throw new InvalidDataException("A torn record survived an unclean shutdown.");
                if (container.IsPresent(image.Index))
                    throw new InvalidDataException("A rejected record kept its present bit.");

                if (!container.TryRead(intact, out byte[] read) || !read.AsSpan().SequenceEqual(intactBytes))
                    throw new InvalidDataException("Recovery rejected a record that was intact.");
                // The second read no longer needs checking, and must not re-check.
                if (!container.TryRead(intact, out _))
                    throw new InvalidDataException("A verified record was rejected on its second read.");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static string BitsPath(string root, string id)
    {
        if (!LibraryCacheContainer.TryGetRelativePath(id, out string relative))
            throw new InvalidDataException($"Library id '{id}' is not a safe cache path.");
        return Path.Combine(root, "libs", relative + ".bits");
    }

    /// <summary>Builds one well-formed Lib image record (17-byte header plus payload) for the metadata given.</summary>
    private static byte[] BuildImageRecord(V3LibraryImageRecord image, int payloadLength)
    {
        byte[] pixels = new byte[payloadLength];
        Random.Shared.NextBytes(pixels);
        byte[] record = new byte[17 + payloadLength];
        BitConverter.TryWriteBytes(record.AsSpan(0), image.Width);
        BitConverter.TryWriteBytes(record.AsSpan(2), image.Height);
        BitConverter.TryWriteBytes(record.AsSpan(4), image.X);
        BitConverter.TryWriteBytes(record.AsSpan(6), image.Y);
        BitConverter.TryWriteBytes(record.AsSpan(8), image.ShadowX);
        BitConverter.TryWriteBytes(record.AsSpan(10), image.ShadowY);
        record[12] = image.Shadow;
        BitConverter.TryWriteBytes(record.AsSpan(13), payloadLength);
        pixels.CopyTo(record.AsSpan(17));
        return record;
    }
}
