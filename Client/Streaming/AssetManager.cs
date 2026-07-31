using Shared.StreamingAssets;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Client.Streaming
{
    public static class AssetManager
    {
        private const int CoalesceGapBytes = 16 * 1024;
        private const int MaxSpanBytes = 4 * 1024 * 1024;
        private const int MaxBatchSegments = 64;
        private const int MaxBatchBytes = 4 * 1024 * 1024;
        private const int MaxQueueBatch = 256;
        private const int RetryEpochMilliseconds = 2000;

        /// <summary>
        /// Shortest gap between two floor render-target rebuilds triggered by newly arrived pixels. Low
        /// enough that a streaming map still fills in smoothly, high enough that a burst of arrivals cannot
        /// cost one full-screen floor pass per frame.
        /// </summary>
        private const int FloorRebuildIntervalMs = 200;

        /// <summary>Records which working set has already been unpacked into the containers.</summary>
        private const string WorkingSetMarkerName = "workset.applied";

        private static readonly string[] StartupLibraryIds =
        {
            "chrsel", "prguse", "prguse2", "prguse3", "xiayiui", "ui_32bit", "title"
        };

        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });

        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Loads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> BackgroundLoads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, RetryState> RetryStates = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, StreamingLibraryIndex> LibraryIndexes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, StreamingMapPack> MapPacks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, LibraryFetchQueue> LibraryQueues = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object ManifestLock = new();

        private static SemaphoreSlim DownloadSemaphore = new(16);
        private static AssetCacheStore _store;
        private static StreamingAssetV3Manifest _manifest;
        private static Dictionary<string, V3LibraryRecord> _libraries;
        private static Dictionary<string, V3MapRecord> _maps;
        private static Dictionary<string, V3SoundRecord> _sounds;
        private static Task _manifestTask;
        private static DateTime _nextManifestAttemptUtc = DateTime.MinValue;
        private static int _notificationPending;
        private static bool _floorRebuildPending;
        private static long _nextFloorRebuildTime;
        public static event Action AssetsUpdated;
        public static bool Enabled => Settings.StreamingEnabled && !string.IsNullOrWhiteSpace(Settings.AssetBaseUrl);

        /// <summary>
        /// True once every piece of metadata the startup UI lays out from is resolvable. Set by
        /// <see cref="StartupAssetBootstrapper"/>, which owns the single bounded wait on the main thread.
        /// </summary>
        public static bool StartupMetadataReady { get; internal set; }

        /// <summary>
        /// Coarse clock (about two seconds per step) that lets the render thread retry assets it gave up
        /// on without performing any per-frame I/O.
        /// </summary>
        public static int RetryEpoch => (int)(Environment.TickCount64 / RetryEpochMilliseconds);

        public static void Initialize()
        {
            if (!Enabled) return;
            Client.Timeout = TimeSpan.FromSeconds(Math.Max(5, Settings.AssetRequestTimeoutSeconds));
            DownloadSemaphore = new SemaphoreSlim(Math.Max(1, Settings.AssetDownloadConcurrency));
            _store = new AssetCacheStore(Settings.AssetCachePath, Settings.AssetCacheMaxMB, StartupLibraryIds);
            WorkingSetRecorder.Initialize();
            StartupAssetBootstrapper.Begin();
        }

        /// <summary>Marks every container as cleanly closed so the next run can trust it without re-hashing.</summary>
        public static void Shutdown()
        {
            WorkingSetRecorder.Save();
            AssetCacheStore store = Interlocked.Exchange(ref _store, null);
            store?.Dispose();
        }

        /// <summary>
        /// The startup libraries that actually have to be streamed. Anything already satisfied by a valid
        /// local Lib is skipped, so a full client with <c>PreferLocalAssets</c> never waits on the network.
        /// </summary>
        public static List<string> GetRequiredStartupLibraryIds()
        {
            List<string> result = new();
            foreach (string id in StartupLibraryIds)
            {
                if (Settings.PreferLocalAssets &&
                    HasUsableLocalLibrary(Path.Combine(Settings.DataPath, id + ".Lib"))) continue;
                if (!TryGetLoadedLibraryRecord(id, out _)) continue;
                result.Add(id);
            }
            return result;
        }

        /// <summary>Awaits the root manifest without blocking the caller's thread.</summary>
        public static async Task<StreamingAssetV3Manifest> GetManifestAsync()
        {
            if (!Enabled || _store == null) return null;
            await EnsureManifestAsync().ConfigureAwait(false);
            return _manifest;
        }

        /// <summary>
        /// Non-blocking manifest access. The manifest is loaded by an explicit startup phase and refreshed
        /// in the background; callers that arrive before it is ready simply report "not available yet"
        /// instead of stalling the thread on an HTTP request.
        /// </summary>
        public static StreamingAssetV3Manifest Manifest
        {
            get
            {
                if (!Enabled || _store == null) return null;
                StreamingAssetV3Manifest manifest = _manifest;
                if (manifest == null) RequestManifest();
                return manifest;
            }
        }

        public static void RequestManifest()
        {
            if (!Enabled || _manifest != null) return;
            QueueBackground("manifest-v3", async () =>
            {
                await EnsureManifestAsync().ConfigureAwait(false);
                if (_manifest != null) PostAssetsUpdated();
            });
        }

        public static bool TryGetLibraryRecord(string id, out V3LibraryRecord record)
        {
            record = null;
            if (Manifest == null) return false;
            EnsureIndexes();
            return _libraries.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetMapRecord(string id, out V3MapRecord record)
        {
            record = null;
            if (Manifest == null) return false;
            EnsureIndexes();
            return _maps.TryGetValue(NormalizeId(id), out record);
        }

        /// <summary>Manifest lookup that never blocks; used by the async download paths.</summary>
        public static bool TryGetLoadedLibraryRecord(string id, out V3LibraryRecord record)
        {
            record = null;
            if (_manifest == null) return false;
            EnsureIndexes();
            return _libraries.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetLoadedMapRecord(string id, out V3MapRecord record)
        {
            record = null;
            if (_manifest == null)
            {
                RequestManifest();
                return false;
            }
            EnsureIndexes();
            return _maps.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetSoundRecord(string id, out V3SoundRecord record)
        {
            record = null;
            if (Manifest == null) return false;
            EnsureIndexes();
            return _sounds.TryGetValue(NormalizeId(id), out record);
        }
        public static int GetStreamingLibraryCount(string localDirectory, string suffix)
        {
            if (Manifest == null) return 0;
            EnsureIndexes();
            string dataRoot = Path.GetFullPath(Settings.DataPath);
            string directory = Path.GetFullPath(localDirectory);
            string prefix = directory.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
                ? NormalizeId(Path.GetRelativePath(dataRoot, directory))
                : NormalizeId(new DirectoryInfo(directory).Name);
            prefix = prefix.TrimEnd('/');
            string normalizedSuffix = (suffix ?? string.Empty).ToLowerInvariant();
            int maximum = -1;
            foreach (string id in _libraries.Keys)
            {
                if (!id.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                string name = id[(prefix.Length + 1)..];
                if (name.Contains('/')) continue;
                if (!string.IsNullOrEmpty(normalizedSuffix))
                {
                    if (!name.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    name = name[..^normalizedSuffix.Length];
                }
                if (int.TryParse(name, out int value) && value > maximum) maximum = value;
            }
            return maximum + 1;
        }

        /// <summary>
        /// Loads a library's catalog header - the segment directory and frame table. Packed image records
        /// live in separately compressed segments that are fetched on demand, so this is one small range
        /// request even for a library with a hundred thousand images.
        /// </summary>
        public static async Task<StreamingLibraryIndex> GetLibraryIndexAsync(string libraryId,
            bool loadAllSegments = false)
        {
            string id = NormalizeId(libraryId);
            if (!LibraryIndexes.TryGetValue(id, out StreamingLibraryIndex index))
            {
                await EnsureManifestAsync().ConfigureAwait(false);
                StreamingAssetV3Manifest manifest = _manifest;
                if (manifest == null || !TryGetLoadedLibraryRecord(id, out V3LibraryRecord record)) return null;
                byte[] bytes = await GetRangeAsync(record.CatalogHeaderHash, record.CatalogHeaderLength,
                    StreamingAssetV3IO.GetCatalogPath(manifest.CatalogHash), manifest.CatalogLength,
                    record.CatalogOffset).ConfigureAwait(false);
                if (bytes == null) return null;
                try
                {
                    V3LibraryIndexHeader header = StreamingAssetV3IO.ReadLibraryIndexHeader(bytes, 0,
                        record.CatalogHeaderLength, id, record.ImageCount);
                    if (record.CatalogHeaderLength + header.Segments.Sum(item => (long)item.CompressedLength) !=
                        record.CatalogLength)
                        throw new InvalidDataException("Segment directory does not cover the catalog block.");
                    index = LibraryIndexes.GetOrAdd(id, new StreamingLibraryIndex(record, header));
                }
                catch (Exception ex)
                {
                    CMain.SaveError($"Invalid V3 library index '{id}': {ex.Message}");
                    return null;
                }
            }

            if (!loadAllSegments) return index;
            for (int segment = 0; segment < index.SegmentCount; segment++)
                if (!await LoadLibrarySegmentAsync(index, segment).ConfigureAwait(false)) return null;
            return index;
        }

        /// <summary>
        /// Fetches and decodes one segment of packed image records. Each segment carries its own SHA-256 in
        /// the header, so it is verified and blob-cached exactly like any other content-addressed piece.
        /// </summary>
        public static async Task<bool> LoadLibrarySegmentAsync(StreamingLibraryIndex index, int segment)
        {
            if (index == null || segment < 0 || segment >= index.SegmentCount) return false;
            if (index.IsSegmentLoaded(segment)) return true;
            StreamingAssetV3Manifest manifest = _manifest;
            if (manifest == null) return false;
            V3LibraryIndexSegment entry = index.Header.Segments[segment];
            byte[] compressed = await GetRangeAsync(entry.Hash, entry.CompressedLength,
                StreamingAssetV3IO.GetCatalogPath(manifest.CatalogHash), manifest.CatalogLength,
                index.Library.CatalogOffset + entry.Offset).ConfigureAwait(false);
            if (compressed == null) return false;
            try
            {
                byte[] raw = StreamingAssetV3IO.ReadLibraryIndexSegment(index.Header, segment, compressed,
                    index.Library.FileLength);
                int count = index.Header.GetSegmentImageCount(segment);
                int first = index.Header.GetSegmentFirstImage(segment);
                V3LibraryImageRecord[] records = new V3LibraryImageRecord[count];
                for (int i = 0; i < count; i++)
                    records[i] = StreamingAssetV3IO.DecodeLibraryImageRecord(raw, i, first + i);
                index.SetSegment(segment, records);
                return true;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid V3 library index segment '{index.Id}' #{segment}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Bounded wait used only by the startup UI libraries, whose one-shot layout needs real sizes on
        /// the first frame. The bootstrapper has normally already resolved these, so this returns from the
        /// in-memory cache; if it has not, the wait still cannot outlive <paramref name="timeout"/>.
        /// </summary>
        public static StreamingLibraryIndex WaitForLibraryIndex(string libraryId, TimeSpan timeout)
        {
            if (TryGetCachedLibraryIndex(libraryId, out StreamingLibraryIndex cached) &&
                cached.AreAllSegmentsLoaded()) return cached;
            Task<StreamingLibraryIndex> task = GetLibraryIndexAsync(libraryId, true);
            try { return task.Wait(timeout) ? task.Result : null; }
            catch (AggregateException) { return null; }
        }

        public static bool TryGetCachedLibraryIndex(string libraryId, out StreamingLibraryIndex index) =>
            LibraryIndexes.TryGetValue(NormalizeId(libraryId), out index);

        public static void QueueLibraryIndex(string libraryId)
        {
            string id = NormalizeId(libraryId);
            QueueBackground("library-index:" + id, async () =>
            {
                if (await GetLibraryIndexAsync(id).ConfigureAwait(false) != null) PostAssetsUpdated();
            });
        }

        /// <summary>
        /// Requests the index segment covering <paramref name="imageIndex"/>. Called from the render thread,
        /// which is why the index itself rate limits the request to one attempt per retry epoch.
        /// </summary>
        public static void QueueLibrarySegment(StreamingLibraryIndex index, int imageIndex)
        {
            if (index == null) return;
            int segment = index.GetSegmentIndex(imageIndex);
            if (segment < 0 || segment >= index.SegmentCount || index.IsSegmentLoaded(segment)) return;
            if (!index.TryBeginSegmentRequest(segment, RetryEpoch)) return;
            QueueBackground($"library-segment:{index.Id}:{segment}", async () =>
            {
                if (await LoadLibrarySegmentAsync(index, segment).ConfigureAwait(false)) PostAssetsUpdated();
            });
        }
        /// <summary>
        /// Returns the sparse container backing a library. Callers hold on to it, so the draw path can ask
        /// <see cref="LibraryCacheContainer.IsPresent"/> without any file system access.
        /// </summary>
        public static LibraryCacheContainer GetLibraryContainer(V3LibraryRecord library) =>
            _store?.GetContainer(library);

        public static void QueueLibraryImage(V3LibraryRecord library, V3LibraryImageRecord image)
        {
            if (_store == null || library == null || image?.Exists != true) return;
            LibraryQueues.GetOrAdd(library.Id, _ => new LibraryFetchQueue()).Enqueue(library, image);
        }

        public static bool TryGetCachedMapPack(string mapId, out StreamingMapPack pack) =>
            MapPacks.TryGetValue(NormalizeId(mapId), out pack);

        public static void QueueMapPack(string mapId)
        {
            string id = NormalizeId(mapId);
            if (MapPacks.ContainsKey(id)) return;
            QueueBackground("mappack:" + id, async () =>
            {
                if (await GetMapPackAsync(id).ConfigureAwait(false) != null) PostAssetsUpdated();
            });
        }

        /// <summary>
        /// Downloads a whole map pack in one request. Packs stay well under a megabyte, so this replaces
        /// the hundreds of per-chunk ranged requests the previous design issued on entering a map.
        /// </summary>
        public static async Task<StreamingMapPack> GetMapPackAsync(string mapId)
        {
            string id = NormalizeId(mapId);
            if (MapPacks.TryGetValue(id, out StreamingMapPack cached)) return cached;
            if (!TryGetMapRecord(id, out V3MapRecord record)) return null;
            byte[] bytes = await GetFullFileAsync(record.FileHash, record.FileLength,
                StreamingAssetV3IO.GetMapPath(record.FileHash)).ConfigureAwait(false);
            if (bytes == null) return null;
            try
            {
                StreamingMapPack pack = StreamingMapPack.Parse(bytes, record.Width, record.Height, record.ChunkSize);
                MapPacks[id] = pack;
                return pack;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid V3 map pack '{id}': {ex.Message}");
                return null;
            }
        }
        public static bool TryGetCachedSoundBytes(string soundId, out byte[] bytes, out string extension)
        {
            bytes = null;
            extension = null;
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record) || _store == null ||
                !_store.Blobs.TryGet(record.Hash, record.Length, out bytes)) return false;
            extension = record.Extension;
            return true;
        }

        public static bool TryGetCachedSoundBytes(string soundName, IEnumerable<string> extensions,
            out byte[] bytes, out string extension)
        {
            foreach (string id in GetSoundIdCandidates(soundName, extensions))
                if (TryGetCachedSoundBytes(id, out bytes, out extension)) return true;
            bytes = null;
            extension = null;
            return false;
        }

        /// <summary>
        /// Cache-only sound lookup with a background fetch on a miss. Sounds are never downloaded on the
        /// calling thread; a miss is silence for now and a notification once the bytes arrive.
        /// </summary>
        public static bool GetSoundBytes(string soundId, out byte[] bytes, out string extension)
        {
            if (TryGetCachedSoundBytes(soundId, out bytes, out extension)) return true;
            bytes = null;
            extension = null;
            QueueSound(soundId);
            return false;
        }

        /// <summary>
        /// Unpacks the published first-run working set into the library containers. One whole-file GET
        /// replaces the hundreds of ranged requests a cold client used to issue between the login screen and
        /// the first map. The pack is deliberately not blob-cached: it is read once, and a marker records
        /// which pack has already been applied so a warm client never downloads it again.
        /// </summary>
        internal static async Task<int> ApplyWorkingSetAsync()
        {
            AssetCacheStore store = _store;
            StreamingAssetV3Manifest manifest = _manifest;
            if (store == null || manifest == null) return 0;
            if (!StreamingAssetIO.IsValidSha256(manifest.WorkingSetHash) || manifest.WorkingSetLength <= 0 ||
                manifest.WorkingSetLength > int.MaxValue || manifest.WorkingSetImageCount <= 0) return 0;
            if (store.Blobs.TryGetMetadata(WorkingSetMarkerName, out byte[] marker) &&
                string.Equals(Encoding.UTF8.GetString(marker).Trim(), manifest.WorkingSetHash,
                    StringComparison.OrdinalIgnoreCase)) return 0;

            byte[] pack = await DownloadWorkingSetAsync(manifest.WorkingSetHash,
                (int)manifest.WorkingSetLength).ConfigureAwait(false);
            if (pack == null) return 0;

            StreamingWorkingSet set;
            try { set = StreamingWorkingSetPack.Parse(pack); }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid streaming working set: {ex.Message}");
                return 0;
            }

            int applied = 0;
            foreach (WorkingSetLibraryView view in set.Libraries)
            {
                if (!TryGetLoadedLibraryRecord(view.Id, out V3LibraryRecord record)) continue;
                // A library republished since the recording keeps its id but not its bytes, so its entries
                // would land at the wrong offsets. Skipping them costs a few ranged requests, nothing else.
                if (!string.Equals(record.FileHash, view.FileHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (Settings.PreferLocalAssets &&
                    HasUsableLocalLibrary(Path.Combine(Settings.DataPath, view.Id + ".Lib"))) continue;

                LibraryCacheContainer container = store.GetContainer(record);
                if (container == null) continue;

                applied += ApplyWorkingSetLibrary(view, pack, container, out long written);
                store.NoteGrowth(written);
            }

            // The marker is written even when nothing was applied: the pack has been seen, and re-downloading
            // it on every start would be worse than the few ranged requests a partial application costs.
            store.Blobs.PutMetadata(WorkingSetMarkerName, Encoding.UTF8.GetBytes(manifest.WorkingSetHash));
            if (applied > 0) PostAssetsUpdated();
            return applied;
        }

        /// <summary>
        /// Writes one pack library's records into its container and reports how many landed. Separated from
        /// the download so <see cref="AssetCacheSelfTest"/> can exercise the unpacking without a server.
        /// </summary>
        internal static int ApplyWorkingSetLibrary(WorkingSetLibraryView view, byte[] pack,
            LibraryCacheContainer container, out long written)
        {
            written = 0;
            int applied = 0;
            foreach (WorkingSetPayload entry in view.Entries)
            {
                if (container.IsPresent(entry.Index)) continue;
                if (entry.PayloadOffset < 0 || entry.Length <= 0 ||
                    entry.Length > pack.Length - entry.PayloadOffset) continue;

                byte[] bytes = new byte[entry.Length];
                Buffer.BlockCopy(pack, entry.PayloadOffset, bytes, 0, entry.Length);
                V3LibraryImageRecord image = TryDescribeRecord(entry, bytes);
                if (image == null)
                {
                    CMain.SaveError($"Working set entry {entry.Index} of '{view.Id}' is malformed.");
                    continue;
                }

                container.Write(image, bytes);
                if (!container.IsPresent(entry.Index)) continue;
                written += entry.Length;
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// Turns one pack entry into the record the container writes. The pack's authenticity comes from the
        /// manifest's SHA-256 over the whole file, so per-record verification is structural: the bytes must
        /// parse as exactly one Lib image record, and its header is what the record then reports.
        /// </summary>
        private static V3LibraryImageRecord TryDescribeRecord(WorkingSetPayload entry, byte[] bytes)
        {
            try
            {
                V3LibraryImagePayload payload = StreamingAssetV3IO.ReadLibraryImageRecord(bytes);
                return new V3LibraryImageRecord
                {
                    Index = entry.Index,
                    Offset = entry.Offset,
                    Length = entry.Length,
                    Width = payload.Width,
                    Height = payload.Height,
                    X = payload.X,
                    Y = payload.Y,
                    ShadowX = payload.ShadowX,
                    ShadowY = payload.ShadowY,
                    Shadow = payload.Shadow
                };
            }
            catch (InvalidDataException) { return null; }
            catch (EndOfStreamException) { return null; }
        }

        private static async Task<byte[]> DownloadWorkingSetAsync(string hash, int length)
        {
            string relativePath = StreamingAssetV3IO.GetWorkingSetPath(hash);
            if (!CanAttempt(hash)) return null;
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(relativePath)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length != length ||
                    !string.Equals(StreamingAssetIO.ComputeSha256(bytes), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 or length validation failed for {hash}.");
                RetryStates.TryRemove(hash, out _);
                return bytes;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming working set download failed '{relativePath}': {ex.Message}");
                RecordFailure(hash);
                return null;
            }
            finally { DownloadSemaphore.Release(); }
        }

        /// <summary>Downloads one sound into the blob cache. Used by the startup phase for the sound list.</summary>
        public static async Task<bool> PrefetchSoundAsync(string soundId)
        {
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record)) return false;
            return await GetFullFileAsync(record.Hash, record.Length,
                StreamingAssetV3IO.GetSoundPath(record.Hash, record.Extension)).ConfigureAwait(false) != null;
        }

        public static void QueueSound(string soundId)
        {
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record)) return;
            QueueBackground("sound:" + record.Hash, async () =>
            {
                byte[] bytes = await GetFullFileAsync(record.Hash, record.Length,
                    StreamingAssetV3IO.GetSoundPath(record.Hash, record.Extension)).ConfigureAwait(false);
                if (bytes != null) PostAssetsUpdated();
            });
        }

        public static void QueueSound(string soundName, IEnumerable<string> extensions)
        {
            foreach (string id in GetSoundIdCandidates(soundName, extensions))
            {
                if (!TryGetSoundRecord(id, out _)) continue;
                QueueSound(id);
                return;
            }
        }
        public static string ToLibraryId(string localFileName)
        {
            string root = Path.GetFullPath(Settings.DataPath);
            string path = Path.GetFullPath(localFileName);
            string id = path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, Path.ChangeExtension(path, null))
                : Path.GetFileNameWithoutExtension(path);
            return NormalizeId(id);
        }

        public static string ToMapId(string localFileName) =>
            NormalizeId(Path.GetFileNameWithoutExtension(localFileName));

        public static string NormalizeId(string id) =>
            (id ?? string.Empty).Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

        public static bool HasUsableLocalLibrary(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new(stream);
                if (stream.Length < 8) return false;
                int version = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (version < 2 || version > 3 || count < 0 || count > 10_000_000) return false;
                long tableStart = version >= 3 ? 12 : 8;
                long tableLength = (long)count * 4;
                if (tableStart > stream.Length || tableLength > stream.Length - tableStart) return false;
                if (version >= 3)
                {
                    int frameSeek = reader.ReadInt32();
                    if (frameSeek != 0 && (frameSeek < tableStart + tableLength || frameSeek > stream.Length - 4))
                        return false;
                }
                for (int i = 0; i < count; i++)
                {
                    int offset = reader.ReadInt32();
                    if (offset > 0 && (offset < tableStart + tableLength || offset > stream.Length - 17)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Applies the coalesced "new bytes arrived" notification. Rebuilding the floor render target is a
        /// full pass over every visible cell, and while a map streams in something lands on almost every
        /// frame, so invalidating on each notification cost one extra floor rebuild per frame for the whole
        /// load. The invalidation is rate limited instead; tiles that just arrived can wait a few frames,
        /// and the pending flag survives until the rebuild actually happens.
        /// </summary>
        public static void ProcessNotifications()
        {
            if (Interlocked.Exchange(ref _notificationPending, 0) != 0)
            {
                try
                {
                    AssetsUpdated?.Invoke();
                    MirControls.MirScene.ActiveScene?.Redraw();
                    _floorRebuildPending = true;
                }
                catch (Exception ex) { CMain.SaveError($"Asset notification failed: {ex.Message}"); }
            }

            if (!_floorRebuildPending || CMain.Time < _nextFloorRebuildTime) return;
            try
            {
                MirScenes.GameScene scene = MirScenes.GameScene.Scene;
                if (scene?.MapControl == null || scene.MapControl.IsDisposed) return;

                _floorRebuildPending = false;
                _nextFloorRebuildTime = CMain.Time + FloorRebuildIntervalMs;
                scene.MapControl.FloorValid = false;
                scene.MapControl.LightsValid = false;
                scene.MapControl.Redraw();
            }
            catch (Exception ex) { CMain.SaveError($"Asset notification failed: {ex.Message}"); }
        }
        private static Task<byte[]> GetRangeAsync(string hash, int length, string relativePath,
            long totalLength, long offset)
        {
            AssetCacheStore store = _store;
            if (store != null && store.Blobs.TryGet(hash, length, out byte[] cached)) return Task.FromResult(cached);
            if (!StreamingAssetIO.IsValidSha256(hash) || length <= 0 || offset < 0 || totalLength < 0 ||
                offset > totalLength || length > totalLength - offset) return Task.FromResult<byte[]>(null);
            return GetSingleFlightAsync(hash, async () =>
            {
                if (!CanAttempt(hash)) return null;
                byte[] bytes = await DownloadSpanAsync(relativePath, totalLength, offset, length).ConfigureAwait(false);
                if (bytes == null) { RecordFailure(hash); return null; }
                if (!string.Equals(StreamingAssetIO.ComputeSha256(bytes), hash, StringComparison.OrdinalIgnoreCase))
                {
                    CMain.SaveError($"Streaming range hash mismatch '{relativePath}'.");
                    RecordFailure(hash);
                    return null;
                }
                _store?.Blobs.Put(hash, bytes);
                _store?.NoteGrowth(bytes.Length);
                RetryStates.TryRemove(hash, out _);
                return bytes;
            });
        }

        private static Task<byte[]> GetFullFileAsync(string hash, long length, string relativePath)
        {
            if (length > int.MaxValue || length < 0 || !StreamingAssetIO.IsValidSha256(hash))
                return Task.FromResult<byte[]>(null);
            AssetCacheStore store = _store;
            if (store != null && store.Blobs.TryGet(hash, length, out byte[] cached)) return Task.FromResult(cached);
            return GetSingleFlightAsync(hash, () => DownloadFullAsync(hash, (int)length, relativePath));
        }

        private static async Task<byte[]> GetSingleFlightAsync(string hash, Func<Task<byte[]>> factory)
        {
            Lazy<Task<byte[]>> lazy = Loads.GetOrAdd(hash, _ => new Lazy<Task<byte[]>>(factory, true));
            try { return await lazy.Value.ConfigureAwait(false); }
            finally { Loads.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(hash, lazy)); }
        }
        private static async Task<byte[]> DownloadSpanAsync(string relativePath, long totalLength, long offset, int length)
        {
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, MakeUrl(relativePath));
                request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using HttpResponseMessage response = await Client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                ContentRangeHeaderValue range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset ||
                    range.To != offset + length - 1 || range.Length != totalLength)
                    throw new InvalidDataException($"Invalid HTTP range response for {relativePath}.");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length != length) throw new InvalidDataException("Unexpected HTTP range length.");
                return bytes;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming range failed '{relativePath}': {ex.Message}");
                return null;
            }
            finally { DownloadSemaphore.Release(); }
        }

        private static async Task<byte[]> DownloadFullAsync(string hash, int length, string relativePath)
        {
            if (!CanAttempt(hash)) return null;
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(relativePath)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length != length ||
                    !string.Equals(StreamingAssetIO.ComputeSha256(bytes), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 or length validation failed for {hash}.");
                _store?.Blobs.Put(hash, bytes);
                _store?.NoteGrowth(bytes.Length);
                RetryStates.TryRemove(hash, out _);
                return bytes;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming download failed '{relativePath}': {ex.Message}");
                RecordFailure(hash);
                return null;
            }
            finally { DownloadSemaphore.Release(); }
        }
        /// <summary>
        /// Fetches a batch of image records from one published Lib. Records are sorted by offset and
        /// merged into spans, so a run of neighbouring images costs one request; unrelated spans are
        /// combined into a single multi-segment request instead of one request each.
        /// </summary>
        private static async Task FetchLibraryImagesAsync(V3LibraryRecord library, List<V3LibraryImageRecord> images)
        {
            if (library == null || images == null || images.Count == 0) return;
            LibraryCacheContainer container = _store?.GetContainer(library);
            if (container == null) return;

            string path = StreamingAssetV3IO.GetLibraryPath(library.FileHash);
            List<V3LibraryImageRecord> wanted = images
                .Where(image => image != null && image.Exists && !container.IsPresent(image.Index) &&
                                CanAttempt(RetryKey(library, image)))
                .OrderBy(image => image.Offset)
                .ToList();
            if (wanted.Count == 0) return;

            bool any = false;
            long written = 0;
            foreach (List<LibrarySpan> group in GroupSpans(Coalesce(wanted, library.FileLength)))
            {
                Dictionary<long, byte[]> payloads = group.Count == 1
                    ? await DownloadSingleSpanAsync(path, library.FileLength, group[0]).ConfigureAwait(false)
                    : await DownloadSegmentsAsync(path, library.FileLength, group).ConfigureAwait(false);

                if (payloads == null)
                {
                    foreach (LibrarySpan span in group)
                        foreach (V3LibraryImageRecord image in span.Images) RecordFailure(RetryKey(library, image));
                    continue;
                }

                foreach (LibrarySpan span in group)
                {
                    if (!payloads.TryGetValue(span.Offset, out byte[] bytes)) continue;
                    foreach (V3LibraryImageRecord image in span.Images)
                    {
                        string key = RetryKey(library, image);
                        int start = (int)(image.Offset - span.Offset);
                        if (start < 0 || image.Length > bytes.Length - start) { RecordFailure(key); continue; }
                        byte[] record = new byte[image.Length];
                        Buffer.BlockCopy(bytes, start, record, 0, image.Length);
                        // The catalog no longer carries a per-image hash, so the bytes are validated
                        // structurally instead: they must parse as exactly one Lib image record whose
                        // header matches the metadata the layout code already relies on.
                        if (!StreamingAssetV3IO.IsLibraryImageRecordValid(image, record))
                        {
                            CMain.SaveError($"Streaming image validation failed in '{library.Id}' #{image.Index}.");
                            RecordFailure(key);
                            continue;
                        }
                        container.Write(image, record);
                        RetryStates.TryRemove(key, out _);
                        written += record.Length;
                        any = true;
                    }
                }
            }
            _store?.NoteGrowth(written);
            if (any) PostAssetsUpdated();
        }

        /// <summary>Retry bookkeeping key for one image of one published Lib.</summary>
        private static string RetryKey(V3LibraryRecord library, V3LibraryImageRecord image) =>
            library.FileHash + "#" + image.Index.ToString();
        private static List<LibrarySpan> Coalesce(List<V3LibraryImageRecord> images, long fileLength)
        {
            List<LibrarySpan> spans = new();
            LibrarySpan current = null;
            foreach (V3LibraryImageRecord image in images)
            {
                if (image.Offset < 0 || image.Length <= 0 || image.Offset + image.Length > fileLength) continue;
                long end = image.Offset + image.Length;
                if (current != null)
                {
                    long currentEnd = current.Offset + current.Length;
                    if (end <= currentEnd)
                    {
                        current.Images.Add(image);
                        continue;
                    }
                    long gap = image.Offset - currentEnd;
                    long merged = end - current.Offset;
                    if (gap <= CoalesceGapBytes && merged <= MaxSpanBytes)
                    {
                        current.Length = (int)merged;
                        current.Images.Add(image);
                        continue;
                    }
                }
                current = new LibrarySpan { Offset = image.Offset, Length = image.Length };
                current.Images.Add(image);
                spans.Add(current);
            }
            return spans;
        }

        private static IEnumerable<List<LibrarySpan>> GroupSpans(List<LibrarySpan> spans)
        {
            List<LibrarySpan> group = new();
            long bytes = 0;
            foreach (LibrarySpan span in spans)
            {
                if (group.Count > 0 && (group.Count >= MaxBatchSegments || bytes + span.Length > MaxBatchBytes))
                {
                    yield return group;
                    group = new List<LibrarySpan>();
                    bytes = 0;
                }
                group.Add(span);
                bytes += span.Length;
            }
            if (group.Count > 0) yield return group;
        }
        private static async Task<Dictionary<long, byte[]>> DownloadSingleSpanAsync(string relativePath,
            long totalLength, LibrarySpan span)
        {
            byte[] bytes = await DownloadSpanAsync(relativePath, totalLength, span.Offset, span.Length)
                .ConfigureAwait(false);
            return bytes == null ? null : new Dictionary<long, byte[]> { [span.Offset] = bytes };
        }

        /// <summary>
        /// Requests several disjoint spans of one file in a single round trip. The response body is a
        /// sequence of [int64 offset][int32 length][bytes] records.
        /// </summary>
        private static async Task<Dictionary<long, byte[]>> DownloadSegmentsAsync(string relativePath,
            long totalLength, List<LibrarySpan> spans)
        {
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                StringBuilder query = new();
                foreach (LibrarySpan span in spans)
                {
                    if (query.Length > 0) query.Append(',');
                    query.Append(span.Offset).Append('-').Append(span.Length);
                }

                using HttpResponseMessage response = await Client
                    .GetAsync(MakeUrl(relativePath) + "?segments=" + query).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                Dictionary<long, byte[]> result = new();
                int position = 0;
                while (position < body.Length)
                {
                    if (body.Length - position < 12) throw new InvalidDataException("Truncated segment header.");
                    long offset = BitConverter.ToInt64(body, position);
                    int length = BitConverter.ToInt32(body, position + 8);
                    position += 12;
                    if (length <= 0 || length > body.Length - position)
                        throw new InvalidDataException("Malformed segment length.");
                    byte[] payload = new byte[length];
                    Buffer.BlockCopy(body, position, payload, 0, length);
                    position += length;
                    if (!result.TryAdd(offset, payload)) throw new InvalidDataException("Duplicate segment offset.");
                }

                foreach (LibrarySpan span in spans)
                    if (!result.TryGetValue(span.Offset, out byte[] payload) || payload.Length != span.Length ||
                        span.Offset + span.Length > totalLength)
                        throw new InvalidDataException("Segment response does not match the request.");
                return result;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming segment batch failed '{relativePath}': {ex.Message}");
                return null;
            }
            finally { DownloadSemaphore.Release(); }
        }
        private static async Task EnsureManifestAsync()
        {
            if (_manifest != null) return;
            Task task;
            lock (ManifestLock)
            {
                if (_manifest != null) return;
                if (_manifestTask != null) task = _manifestTask;
                else
                {
                    if (DateTime.UtcNow < _nextManifestAttemptUtc) return;
                    _manifestTask = LoadManifestAsync();
                    task = _manifestTask;
                }
            }
            try { await task.ConfigureAwait(false); }
            finally
            {
                lock (ManifestLock)
                    if (ReferenceEquals(_manifestTask, task)) _manifestTask = null;
            }
        }

        private static async Task LoadManifestAsync()
        {
            byte[] bytes = null;
            try
            {
                using HttpResponseMessage response = await Client
                    .GetAsync(MakeUrl(StreamingAssetV3Constants.ManifestFileName)).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    byte[] downloaded = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (TryParseManifest(downloaded, out _))
                    {
                        bytes = downloaded;
                        _store?.Blobs.PutMetadata(StreamingAssetV3Constants.ManifestFileName, bytes);
                    }
                }
            }
            catch (Exception ex) { CMain.SaveError($"Streaming manifest download failed: {ex.Message}"); }

            if (bytes == null && _store != null &&
                _store.Blobs.TryGetMetadata(StreamingAssetV3Constants.ManifestFileName, out byte[] stored))
                bytes = stored;

            if (!TryParseManifest(bytes, out StreamingAssetV3Manifest manifest))
            {
                lock (ManifestLock) _nextManifestAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            lock (ManifestLock)
            {
                _manifest = manifest;
                _libraries = null;
                _maps = null;
                _sounds = null;
                _nextManifestAttemptUtc = DateTime.MinValue;
            }
            AssetCacheStore store = _store;
            if (store == null) return;
            _ = Task.Run(() =>
            {
                try
                {
                    store.RemoveOrphans(manifest.Libraries.Select(item => item.Id).ToList());
                    store.Trim();
                }
                catch (Exception ex) { CMain.SaveError($"Streaming cache maintenance failed: {ex.Message}"); }
            });
        }
        private static bool TryParseManifest(byte[] bytes, out StreamingAssetV3Manifest manifest)
        {
            manifest = null;
            if (bytes == null) return false;
            try
            {
                manifest = JsonSerializer.Deserialize<StreamingAssetV3Manifest>(bytes, StreamingAssetIO.JsonOptions);
                if (manifest?.FormatVersion != StreamingAssetV3Constants.FormatVersion ||
                    !StreamingAssetIO.IsValidSha256(manifest.CatalogHash) || manifest.CatalogLength <= 0) return false;
                HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
                foreach (V3LibraryRecord item in manifest.Libraries)
                {
                    item.Id = NormalizeId(item.Id);
                    if (!ids.Add("l:" + item.Id) || item.ImageCount < 0 ||
                        !ValidFile(item.FileHash, item.FileLength) ||
                        item.CatalogHeaderLength <= 0 || item.CatalogHeaderLength > item.CatalogLength ||
                        !ValidCatalogRange(manifest, item.CatalogOffset, item.CatalogLength,
                            item.CatalogHeaderHash)) return false;
                }
                foreach (V3MapRecord item in manifest.Maps)
                {
                    item.Id = NormalizeId(item.Id);
                    if (!ids.Add("m:" + item.Id) || !ValidMapDimensions(item) || item.ChunkSize <= 0 ||
                        !ValidFile(item.FileHash, item.FileLength) || item.FileLength <= 0) return false;
                }
                foreach (V3SoundRecord item in manifest.Sounds)
                {
                    item.Id = NormalizeId(item.Id);
                    if (!ids.Add("s:" + item.Id) || !ValidFile(item.Hash, item.Length) ||
                        item.Extension is not ".wav" and not ".mp3" and not ".lst") return false;
                }
                return true;
            }
            catch { manifest = null; return false; }
        }

        private static bool ValidFile(string hash, long length) =>
            StreamingAssetIO.IsValidSha256(hash) && length >= 0;

        private static bool ValidMapDimensions(V3MapRecord item) =>
            item.Width > 0 && item.Height > 0 && item.Width <= 100_000 && item.Height <= 100_000 &&
            (long)item.Width * item.Height <= 25_000_000;

        private static bool ValidCatalogRange(StreamingAssetV3Manifest manifest, long offset, int length, string hash) =>
            offset >= 0 && length > 0 && offset <= manifest.CatalogLength &&
            length <= manifest.CatalogLength - offset && StreamingAssetIO.IsValidSha256(hash);

        private static void EnsureIndexes()
        {
            if (_libraries != null && _maps != null && _sounds != null) return;
            lock (ManifestLock)
            {
                _libraries ??= _manifest?.Libraries.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new();
                _maps ??= _manifest?.Maps.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new();
                _sounds ??= _manifest?.Sounds.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new();
            }
        }
        private static void QueueBackground(string key, Func<Task> operation)
        {
            if (!Enabled || !BackgroundLoads.TryAdd(key, 0)) return;
            _ = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { CMain.SaveError($"Streaming background operation failed: {ex.Message}"); }
                finally { BackgroundLoads.TryRemove(key, out _); }
            });
        }

        private static IEnumerable<string> GetSoundIdCandidates(string soundName, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(soundName)) yield break;
            string candidate = soundName.Replace('\\', '/').TrimStart('.', '/');
            string soundRoot = Path.GetFullPath(Settings.SoundPath);
            try
            {
                string full = Path.GetFullPath(soundName);
                if (full.StartsWith(soundRoot, StringComparison.OrdinalIgnoreCase))
                    candidate = Path.GetRelativePath(soundRoot, full).Replace('\\', '/');
            }
            catch { }
            if (!string.IsNullOrEmpty(Path.GetExtension(candidate)))
            {
                yield return NormalizeId(candidate);
                yield break;
            }
            foreach (string extension in extensions ?? Array.Empty<string>())
                yield return NormalizeId(candidate + extension);
        }

        private static string MakeUrl(string relativePath) =>
            Settings.AssetBaseUrl.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');

        private static bool CanAttempt(string hash) =>
            !RetryStates.TryGetValue(hash, out RetryState state) || DateTime.UtcNow >= state.NextAttemptUtc;

        private static void RecordFailure(string hash)
        {
            RetryStates.AddOrUpdate(hash,
                _ => new RetryState(1, DateTime.UtcNow.AddSeconds(1)),
                (_, old) =>
                {
                    int failures = Math.Min(old.Failures + 1, 6);
                    return new RetryState(failures, DateTime.UtcNow.AddSeconds(Math.Min(30, 1 << (failures - 1))));
                });
        }

        private static void PostAssetsUpdated() => Interlocked.Exchange(ref _notificationPending, 1);

        /// <summary>Lets the startup phase publish one notification once its prefetch has landed.</summary>
        internal static void NotifyAssetsUpdated() => PostAssetsUpdated();

        private sealed record RetryState(int Failures, DateTime NextAttemptUtc);

        private sealed class LibrarySpan
        {
            public long Offset;
            public int Length;
            public readonly List<V3LibraryImageRecord> Images = new();
        }
        /// <summary>
        /// One queue per library. The render thread only adds indexes here; a single background worker
        /// drains the queue, so requests for the same Lib can be sorted and merged instead of being
        /// issued one image at a time.
        /// </summary>
        private sealed class LibraryFetchQueue
        {
            private readonly ConcurrentDictionary<int, V3LibraryImageRecord> _pending = new();
            private readonly object _sync = new();
            private V3LibraryRecord _library;
            private bool _running;

            public void Enqueue(V3LibraryRecord library, V3LibraryImageRecord image)
            {
                _library = library;
                if (!_pending.TryAdd(image.Index, image)) return;
                lock (_sync)
                {
                    if (_running) return;
                    _running = true;
                }
                _ = Task.Run(RunAsync);
            }

            private async Task RunAsync()
            {
                try
                {
                    while (true)
                    {
                        List<V3LibraryImageRecord> batch = Drain();
                        if (batch.Count == 0)
                        {
                            lock (_sync)
                            {
                                if (!_pending.IsEmpty) continue;
                                _running = false;
                                return;
                            }
                        }
                        await FetchLibraryImagesAsync(_library, batch).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    CMain.SaveError($"Streaming library queue failed: {ex.Message}");
                    lock (_sync) _running = false;
                }
            }

            private List<V3LibraryImageRecord> Drain()
            {
                List<V3LibraryImageRecord> batch = new();
                foreach (KeyValuePair<int, V3LibraryImageRecord> entry in _pending)
                {
                    if (_pending.TryRemove(entry.Key, out V3LibraryImageRecord record)) batch.Add(record);
                    if (batch.Count >= MaxQueueBatch) break;
                }
                return batch;
            }
        }
    }
}
