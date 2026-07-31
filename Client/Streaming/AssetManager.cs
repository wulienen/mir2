using Shared.StreamingAssets;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Client.Streaming
{
    public static class AssetManager
    {
        private const int CacheKindCatalog = 1;
        private const int CacheKindLibraryImage = 2;
        private const int CacheKindMapChunk = 3;
        private const int CacheKindSound = 4;

        private static readonly string[] StartupLibraryIds =
        {
            "chrsel", "prguse", "prguse2", "prguse3", "xiayiui", "ui_32bit", "title"
        };

        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Loads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> BackgroundLoads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, RetryState> RetryStates = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, V3LibraryIndex> LibraryIndexes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, V3MapIndex> MapIndexes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object ManifestLock = new();
        private static SemaphoreSlim DownloadSemaphore = new(4);
        private static AssetCacheDatabase _cache;
        private static StreamingAssetV3Manifest _manifest;
        private static Dictionary<string, V3LibraryRecord> _libraries;
        private static Dictionary<string, V3MapRecord> _maps;
        private static Dictionary<string, V3SoundRecord> _sounds;
        private static Task _manifestTask;
        private static DateTime _nextManifestAttemptUtc = DateTime.MinValue;
        private static int _notificationPending;

        public static event Action AssetsUpdated;
        public static bool Enabled => Settings.StreamingEnabled && !string.IsNullOrWhiteSpace(Settings.AssetBaseUrl);
        public static bool StartupMetadataReady { get; private set; }

        public static void Initialize()
        {
            if (!Enabled) return;
            Client.Timeout = TimeSpan.FromSeconds(Math.Max(5, Settings.AssetRequestTimeoutSeconds));
            DownloadSemaphore = new SemaphoreSlim(Math.Max(1, Settings.AssetDownloadConcurrency));
            _cache = new AssetCacheDatabase(Settings.AssetCachePath, Settings.AssetCacheMaxMB);
            _ = Task.Run(_cache.Cleanup);
            StartupMetadataReady = PreloadStartupMetadata();
        }

        public static bool PreloadStartupMetadata()
        {
            if (!Enabled) return true;
            List<string> required = StartupLibraryIds.Where(id =>
            {
                string local = Path.Combine(Settings.DataPath, id + ".Lib");
                return !Settings.PreferLocalAssets || !HasUsableLocalLibrary(local);
            }).ToList();
            if (required.Count == 0) return true;

            try
            {
                if (Manifest == null) return false;
                Task<V3LibraryIndex>[] tasks = required.Select(GetLibraryIndexAsync).ToArray();
                Task.WhenAll(tasks).GetAwaiter().GetResult();
                bool ready = tasks.All(task => task.Result != null);
                if (!ready) CMain.SaveError("Streaming startup metadata is incomplete.");
                return ready;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming startup metadata failed: {ex.Message}");
                return false;
            }
        }

        public static StreamingAssetV3Manifest Manifest
        {
            get
            {
                if (!Enabled || _cache == null) return null;
                EnsureManifestAsync().GetAwaiter().GetResult();
                return _manifest;
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

        public static async Task<V3LibraryIndex> GetLibraryIndexAsync(string libraryId)
        {
            string id = NormalizeId(libraryId);
            if (LibraryIndexes.TryGetValue(id, out V3LibraryIndex cached)) return cached;
            if (!TryGetLibraryRecord(id, out V3LibraryRecord record)) return null;
            byte[] bytes = await GetCatalogBlockAsync(record.CatalogOffset, record.CatalogLength,
                record.CatalogBlockHash).ConfigureAwait(false);
            if (bytes == null) return null;
            try
            {
                V3LibraryIndex index = StreamingAssetV3IO.ReadLibraryIndex(bytes, id,
                    record.ImageCount, record.FileLength);
                LibraryIndexes[id] = index;
                return index;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid V3 library index '{id}': {ex.Message}");
                return null;
            }
        }

        public static V3LibraryIndex GetLibraryIndex(string libraryId) =>
            GetLibraryIndexAsync(libraryId).GetAwaiter().GetResult();

        public static bool TryGetCachedLibraryIndex(string libraryId, out V3LibraryIndex index) =>
            LibraryIndexes.TryGetValue(NormalizeId(libraryId), out index);

        public static void QueueLibraryIndex(string libraryId)
        {
            string id = NormalizeId(libraryId);
            QueueBackground("library-index:" + id, async () =>
            {
                if (await GetLibraryIndexAsync(id).ConfigureAwait(false) != null) PostAssetsUpdated();
            });
        }

        public static async Task<V3MapIndex> GetMapIndexAsync(string mapId)
        {
            string id = NormalizeId(mapId);
            if (MapIndexes.TryGetValue(id, out V3MapIndex cached)) return cached;
            if (!TryGetMapRecord(id, out V3MapRecord record)) return null;
            byte[] bytes = await GetCatalogBlockAsync(record.CatalogOffset, record.CatalogLength,
                record.CatalogBlockHash).ConfigureAwait(false);
            if (bytes == null) return null;
            try
            {
                V3MapIndex index = StreamingAssetV3IO.ReadMapIndex(bytes, id, record.FileLength);
                if (index.Width != record.Width || index.Height != record.Height || index.ChunkSize != record.ChunkSize)
                    throw new InvalidDataException("Map dimensions do not match the root manifest.");
                MapIndexes[id] = index;
                return index;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid V3 map index '{id}': {ex.Message}");
                return null;
            }
        }

        public static V3MapIndex GetMapIndex(string mapId) => GetMapIndexAsync(mapId).GetAwaiter().GetResult();

        public static bool TryGetCachedMapIndex(string mapId, out V3MapIndex index) =>
            MapIndexes.TryGetValue(NormalizeId(mapId), out index);

        public static void QueueMapIndex(string mapId)
        {
            string id = NormalizeId(mapId);
            QueueBackground("map-index:" + id, async () =>
            {
                if (await GetMapIndexAsync(id).ConfigureAwait(false) != null) PostAssetsUpdated();
            });
        }

        public static bool TryReadCachedLibraryImage(V3LibraryImageRecord image, out V3LibraryImagePayload payload)
        {
            payload = null;
            if (image?.Exists != true || _cache == null || !_cache.TryGet(image.Hash, image.Length, out byte[] bytes))
                return false;
            try
            {
                payload = StreamingAssetV3IO.ReadLibraryImageRecord(bytes);
                return payload.Width == image.Width && payload.Height == image.Height &&
                       payload.X == image.X && payload.Y == image.Y;
            }
            catch { return false; }
        }

        public static void QueueLibraryImage(V3LibraryRecord library, V3LibraryImageRecord image)
        {
            if (library == null || image?.Exists != true) return;
            string path = StreamingAssetV3IO.GetLibraryPath(library.FileHash);
            QueueRange(image.Hash, image.Length, CacheKindLibraryImage, path,
                library.FileLength, image.Offset);
        }

        public static bool TryReadCachedMapChunk(V3MapChunkRecord chunk, out StreamingMapChunk value)
        {
            value = null;
            if (chunk == null || _cache == null || !_cache.TryGet(chunk.Hash, chunk.Length, out byte[] bytes))
                return false;
            try
            {
                value = StreamingAssetV3IO.ReadMapChunk(bytes, chunk);
                return true;
            }
            catch { return false; }
        }

        public static void QueueMapChunks(V3MapRecord map, IEnumerable<V3MapChunkRecord> chunks)
        {
            if (map == null || chunks == null) return;
            string path = StreamingAssetV3IO.GetMapPath(map.FileHash);
            foreach (V3MapChunkRecord chunk in chunks.Where(item => item != null).DistinctBy(item => item.Hash))
                QueueRange(chunk.Hash, chunk.Length, CacheKindMapChunk, path, map.FileLength, chunk.Offset);
        }

        public static bool TryGetCachedSoundBytes(string soundId, out byte[] bytes, out string extension)
        {
            bytes = null;
            extension = null;
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record) || _cache == null ||
                !_cache.TryGet(record.Hash, record.Length, out bytes)) return false;
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

        public static bool GetSoundBytes(string soundId, out byte[] bytes, out string extension)
        {
            if (TryGetCachedSoundBytes(soundId, out bytes, out extension)) return true;
            bytes = null;
            extension = null;
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record)) return false;
            bytes = GetFullFileAsync(record.Hash, record.Length, CacheKindSound,
                StreamingAssetV3IO.GetSoundPath(record.Hash, record.Extension)).GetAwaiter().GetResult();
            extension = bytes == null ? null : record.Extension;
            return bytes != null;
        }

        public static void QueueSound(string soundId)
        {
            if (!TryGetSoundRecord(soundId, out V3SoundRecord record)) return;
            QueueBackground("sound:" + record.Hash, async () =>
            {
                byte[] bytes = await GetFullFileAsync(record.Hash, record.Length, CacheKindSound,
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

        public static void ProcessNotifications()
        {
            if (Interlocked.Exchange(ref _notificationPending, 0) == 0) return;
            try
            {
                AssetsUpdated?.Invoke();
                MirControls.MirScene.ActiveScene?.Redraw();
                MirScenes.GameScene scene = MirScenes.GameScene.Scene;
                if (scene?.MapControl != null && !scene.MapControl.IsDisposed)
                {
                    scene.MapControl.FloorValid = false;
                    scene.MapControl.LightsValid = false;
                    scene.MapControl.Redraw();
                }
            }
            catch (Exception ex) { CMain.SaveError($"Asset notification failed: {ex.Message}"); }
        }

        private static async Task<byte[]> GetCatalogBlockAsync(long offset, int length, string blockHash)
        {
            StreamingAssetV3Manifest manifest = Manifest;
            if (manifest == null) return null;
            return await GetRangeAsync(blockHash, length, CacheKindCatalog,
                StreamingAssetV3IO.GetCatalogPath(manifest.CatalogHash), manifest.CatalogLength, offset)
                .ConfigureAwait(false);
        }

        private static void QueueRange(string hash, int length, int kind, string path, long totalLength, long offset)
        {
            if (_cache?.TryGet(hash, length, out _) == true || !CanAttempt(hash)) return;
            QueueBackground("range:" + hash, async () =>
            {
                if (await GetRangeAsync(hash, length, kind, path, totalLength, offset).ConfigureAwait(false) != null)
                    PostAssetsUpdated();
            });
        }

        private static Task<byte[]> GetRangeAsync(string hash, int length, int kind,
            string relativePath, long totalLength, long offset)
        {
            if (_cache != null && _cache.TryGet(hash, length, out byte[] cached)) return Task.FromResult(cached);
            if (!StreamingAssetIO.IsValidSha256(hash) || length <= 0 || offset < 0 ||
                totalLength < 0 || offset > totalLength || length > totalLength - offset) return Task.FromResult<byte[]>(null);
            return GetSingleFlightAsync(hash, () => DownloadRangeAsync(hash, length, kind, relativePath, totalLength, offset));
        }

        private static Task<byte[]> GetFullFileAsync(string hash, long length, int kind, string relativePath)
        {
            if (length > int.MaxValue || length < 0) return Task.FromResult<byte[]>(null);
            if (_cache != null && _cache.TryGet(hash, length, out byte[] cached)) return Task.FromResult(cached);
            return GetSingleFlightAsync(hash, () => DownloadFullAsync(hash, (int)length, kind, relativePath));
        }

        private static async Task<byte[]> GetSingleFlightAsync(string hash, Func<Task<byte[]>> factory)
        {
            Lazy<Task<byte[]>> lazy = Loads.GetOrAdd(hash, _ => new Lazy<Task<byte[]>>(factory, true));
            try { return await lazy.Value.ConfigureAwait(false); }
            finally { Loads.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(hash, lazy)); }
        }

        private static async Task<byte[]> DownloadRangeAsync(string hash, int length, int kind,
            string relativePath, long totalLength, long offset)
        {
            if (!CanAttempt(hash)) return null;
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cache.TryGet(hash, length, out byte[] cached)) return cached;
                using HttpRequestMessage request = new(HttpMethod.Get, MakeUrl(relativePath));
                request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                ContentRangeHeaderValue range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset ||
                    range.To != offset + length - 1 || range.Length != totalLength)
                    throw new InvalidDataException($"Invalid HTTP range response for {relativePath}.");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                ValidatePayload(hash, length, bytes);
                _cache.Put(hash, kind, bytes);
                RetryStates.TryRemove(hash, out _);
                return bytes;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming range failed '{relativePath}': {ex.Message}");
                RecordFailure(hash);
                return null;
            }
            finally { DownloadSemaphore.Release(); }
        }

        private static async Task<byte[]> DownloadFullAsync(string hash, int length, int kind, string relativePath)
        {
            if (!CanAttempt(hash)) return null;
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cache.TryGet(hash, length, out byte[] cached)) return cached;
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(relativePath)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                ValidatePayload(hash, length, bytes);
                _cache.Put(hash, kind, bytes);
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

        private static void ValidatePayload(string hash, int length, byte[] bytes)
        {
            if (bytes == null || bytes.Length != length ||
                !string.Equals(StreamingAssetIO.ComputeSha256(bytes), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 or length validation failed for {hash}.");
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
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(StreamingAssetV3Constants.ManifestFileName))
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    byte[] downloaded = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (TryParseManifest(downloaded, out _))
                    {
                        bytes = downloaded;
                        _cache?.PutMetadata("manifest-v3", bytes);
                    }
                }
            }
            catch (Exception ex) { CMain.SaveError($"Streaming manifest download failed: {ex.Message}"); }
            bytes ??= _cache?.GetMetadata("manifest-v3");
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
                    if (!ids.Add("l:" + NormalizeId(item.Id)) || item.ImageCount < 0 ||
                        !ValidFile(item.FileHash, item.FileLength) || !ValidCatalogRange(manifest, item.CatalogOffset,
                            item.CatalogLength, item.CatalogBlockHash)) return false;
                }
                foreach (V3MapRecord item in manifest.Maps)
                {
                    if (!ids.Add("m:" + NormalizeId(item.Id)) || !ValidMapDimensions(item) ||
                        item.ChunkSize <= 0 || !ValidFile(item.FileHash, item.FileLength) ||
                        !ValidCatalogRange(manifest, item.CatalogOffset, item.CatalogLength,
                            item.CatalogBlockHash)) return false;
                }
                foreach (V3SoundRecord item in manifest.Sounds)
                {
                    if (!ids.Add("s:" + NormalizeId(item.Id)) || !ValidFile(item.Hash, item.Length) ||
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
        private sealed record RetryState(int Failures, DateTime NextAttemptUtc);
    }
}
