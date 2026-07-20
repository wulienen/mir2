using Client.MirControls;
using Client.MirScenes;
using Shared.StreamingAssets;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Client.Streaming
{
    public static class AssetManager
    {
        private static readonly HttpClient Client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        private static readonly ConcurrentDictionary<string, Task> Downloads = new ConcurrentDictionary<string, Task>();
        private static readonly ConcurrentDictionary<string, byte> PendingMapDownloads = new ConcurrentDictionary<string, byte>();
        private static readonly object ManifestLock = new object();
        private static SemaphoreSlim DownloadSemaphore = new SemaphoreSlim(3);
        private static SemaphoreSlim MapDownloadSemaphore = new SemaphoreSlim(1);

        private static AssetManifest _manifest;
        private static Dictionary<string, AssetLibraryRecord> _libraries;
        private static Dictionary<string, AssetMapRecord> _maps;
        private static Dictionary<string, AssetSoundRecord> _sounds;
        private static Task _manifestTask;
        private static DateTime _nextManifestAttemptUtc = DateTime.MinValue;

        public static event Action AssetsUpdated;

        public static bool Enabled => Settings.StreamingEnabled && !string.IsNullOrWhiteSpace(Settings.AssetBaseUrl);

        public static string CacheRoot => Path.Combine(Settings.AssetCachePath, GetManifestVersion());

        public static string NormalizeId(string id)
        {
            return (id ?? string.Empty).Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();
        }

        public static void Initialize()
        {
            if (!Enabled) return;

            Client.Timeout = TimeSpan.FromSeconds(Math.Max(5, Settings.AssetRequestTimeoutSeconds));

            int concurrency = Math.Max(1, Settings.AssetDownloadConcurrency);
            DownloadSemaphore = new SemaphoreSlim(Math.Max(1, concurrency - 1));
            MapDownloadSemaphore = new SemaphoreSlim(1);
        }

        public static AssetManifest Manifest
        {
            get
            {
                if (!Enabled) return null;
                EnsureManifestAsync().GetAwaiter().GetResult();
                return _manifest;
            }
        }

        public static bool TryGetLibraryRecord(string id, out AssetLibraryRecord record)
        {
            record = null;
            AssetManifest manifest = Manifest;
            if (manifest == null) return false;

            EnsureIndexes();
            return _libraries.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetMapRecord(string id, out AssetMapRecord record)
        {
            record = null;
            AssetManifest manifest = Manifest;
            if (manifest == null) return false;

            EnsureIndexes();
            return _maps.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetSoundRecord(string id, out AssetSoundRecord record)
        {
            record = null;
            AssetManifest manifest = Manifest;
            if (manifest == null) return false;

            EnsureIndexes();
            return _sounds.TryGetValue(NormalizeId(id), out record);
        }

        public static int GetStreamingLibraryCount(string localDirectory, string suffix)
        {
            AssetManifest manifest = Manifest;
            if (manifest == null) return 0;

            EnsureIndexes();

            string dataRoot = Path.GetFullPath(Settings.DataPath);
            string directory = Path.GetFullPath(localDirectory);
            string prefix = directory.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
                ? NormalizeId(Path.GetRelativePath(dataRoot, directory))
                : NormalizeId(new DirectoryInfo(directory).Name);

            prefix = prefix.TrimEnd('/');
            string normalizedSuffix = (suffix ?? string.Empty).ToLowerInvariant();
            int maxIndex = -1;

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

                if (int.TryParse(name, out int index) && index > maxIndex)
                {
                    maxIndex = index;
                }
            }

            return maxIndex + 1;
        }

        public static async Task<LibraryManifest> GetLibraryManifestAsync(string libraryId)
        {
            if (!TryGetLibraryRecord(libraryId, out AssetLibraryRecord record)) return null;

            string cachePath = Path.Combine(CacheRoot, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = await GetAssetBytesAsync(record.ManifestPath, record.Hash, cachePath).ConfigureAwait(false);
            if (bytes == null) return null;

            return JsonSerializer.Deserialize<LibraryManifest>(bytes, StreamingAssetIO.JsonOptions);
        }

        public static LibraryManifest GetLibraryManifest(string libraryId)
        {
            return GetLibraryManifestAsync(libraryId).GetAwaiter().GetResult();
        }

        public static bool TryGetCachedLibraryManifest(string libraryId, out LibraryManifest manifest)
        {
            manifest = null;
            if (!TryGetLibraryRecord(libraryId, out AssetLibraryRecord record)) return false;

            string cachePath = Path.Combine(CacheRoot, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            if (!TryReadValidCache(cachePath, record.Hash, out byte[] bytes)) return false;

            manifest = JsonSerializer.Deserialize<LibraryManifest>(bytes, StreamingAssetIO.JsonOptions);
            return manifest != null;
        }

        public static void QueueLibraryManifest(string libraryId)
        {
            if (!Enabled || !TryGetLibraryRecord(libraryId, out AssetLibraryRecord record)) return;

            string cachePath = Path.Combine(CacheRoot, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            QueueDownload(record.ManifestPath, record.Hash, cachePath);
        }

        public static async Task<MapManifest> GetMapManifestAsync(string mapId)
        {
            if (!TryGetMapRecord(mapId, out AssetMapRecord record)) return null;

            string cachePath = Path.Combine(CacheRoot, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = await GetAssetBytesAsync(record.ManifestPath, record.Hash, cachePath).ConfigureAwait(false);
            if (bytes == null) return null;

            return JsonSerializer.Deserialize<MapManifest>(bytes, StreamingAssetIO.JsonOptions);
        }

        public static MapManifest GetMapManifest(string mapId)
        {
            return GetMapManifestAsync(mapId).GetAwaiter().GetResult();
        }

        public static bool TryReadCachedLibraryImage(string libraryId, LibraryImageRecord image, out StreamingLibraryImageChunk chunk)
        {
            chunk = null;
            if (image == null || string.IsNullOrEmpty(image.Path)) return false;

            string cachePath = GetLibraryImageCachePath(libraryId, image);
            if (!TryReadValidCache(cachePath, image.Hash, out byte[] bytes)) return false;

            chunk = StreamingAssetIO.ReadLibraryImageChunk(bytes);
            return true;
        }

        public static void QueueLibraryImage(string libraryId, LibraryImageRecord image)
        {
            if (!Enabled || image == null || string.IsNullOrEmpty(image.Path)) return;

            string relative = GetLibraryImageRelativePath(libraryId, image);
            string cachePath = GetLibraryImageCachePath(libraryId, image);
            QueueDownload(relative, image.Hash, cachePath);
        }

        public static bool TryReadCachedMapChunk(string mapId, MapChunkRecord record, out StreamingMapChunk chunk)
        {
            chunk = null;
            if (record == null || string.IsNullOrEmpty(record.Path)) return false;

            string cachePath = GetMapChunkCachePath(mapId, record);
            if (!TryReadValidCache(cachePath, record.Hash, out byte[] bytes)) return false;

            chunk = StreamingAssetIO.ReadMapChunk(bytes);
            return true;
        }

        public static void QueueMapChunk(string mapId, MapChunkRecord record)
        {
            QueueMapChunks(mapId, new[] { record });
        }

        public static void QueueMapChunks(string mapId, IEnumerable<MapChunkRecord> records)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(mapId) || records == null) return;

            List<MapChunkDownload> pending = records
                .Where(record => record != null && !string.IsNullOrEmpty(record.Path))
                .Select(record => new MapChunkDownload(
                    record,
                    GetMapChunkRelativePath(mapId, record),
                    GetMapChunkCachePath(mapId, record)))
                .Where(download => !TryReadValidCache(download.CachePath, download.Record.Hash, out _) &&
                                   PendingMapDownloads.TryAdd(download.CachePath, 0))
                .ToList();

            if (pending.Count == 0) return;

            _ = Task.Run(async () =>
            {
                bool updated = false;
                try
                {
                    await MapDownloadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        byte[] batch = await DownloadAssetAsync(
                            $"{StreamingAssetConstants.MapsDirectory}/{NormalizeId(mapId)}/{StreamingAssetConstants.MapChunksDirectory}/batch?keys=" +
                            Uri.EscapeDataString(string.Join(',', pending.Select(download => download.Record.Key)))).ConfigureAwait(false);

                        if (batch != null && TrySaveMapBatch(mapId, pending, batch))
                        {
                            updated = true;
                        }
                        else
                        {
                            foreach (MapChunkDownload download in pending)
                            {
                                byte[] bytes = await DownloadAssetAsync(download.RelativePath, download.Record.Hash).ConfigureAwait(false);
                                if (bytes == null) continue;
                                if (await SaveVerifiedAssetAsync(bytes, download.Record.Hash, download.CachePath, download.RelativePath).ConfigureAwait(false))
                                    updated = true;
                            }
                        }
                    }
                    finally
                    {
                        MapDownloadSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    CMain.SaveError(ex.ToString());
                }
                finally
                {
                    foreach (MapChunkDownload download in pending)
                        PendingMapDownloads.TryRemove(download.CachePath, out _);

                    if (updated)
                        NotifyAssetsUpdated();
                }
            });
        }

        public static bool TryGetCachedSoundPath(string soundId, out string path)
        {
            path = null;
            if (!TryGetSoundRecord(soundId, out AssetSoundRecord record)) return false;

            string cachePath = Path.Combine(CacheRoot, record.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!TryReadValidCache(cachePath, record.Hash, out _)) return false;

            path = cachePath;
            return true;
        }

        public static bool TryGetCachedSoundPath(string soundName, IEnumerable<string> extensions, out string path)
        {
            foreach (string soundId in GetSoundIdCandidates(soundName, extensions))
            {
                if (TryGetCachedSoundPath(soundId, out path))
                {
                    return true;
                }
            }

            path = null;
            return false;
        }

        public static void QueueSound(string soundId)
        {
            if (!Enabled || !TryGetSoundRecord(soundId, out AssetSoundRecord record)) return;

            string cachePath = Path.Combine(CacheRoot, record.Path.Replace('/', Path.DirectorySeparatorChar));
            QueueDownload(record.Path, record.Hash, cachePath);
        }

        public static void QueueSound(string soundName, IEnumerable<string> extensions)
        {
            foreach (string soundId in GetSoundIdCandidates(soundName, extensions))
            {
                if (TryGetSoundRecord(soundId, out _))
                {
                    QueueSound(soundId);
                    return;
                }
            }
        }

        public static string ToLibraryId(string localFileName)
        {
            string data = Path.GetFullPath(Settings.DataPath);
            string path = Path.GetFullPath(localFileName);
            string id = path.StartsWith(data, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(data, Path.ChangeExtension(path, null))
                : Path.GetFileNameWithoutExtension(path);

            return NormalizeId(id);
        }

        public static string ToMapId(string localFileName)
        {
            return NormalizeId(Path.GetFileNameWithoutExtension(localFileName));
        }

        private static async Task EnsureManifestAsync()
        {
            if (_manifest != null) return;

            Task manifestTask;
            lock (ManifestLock)
            {
                if (_manifest != null) return;
                if (_manifestTask != null)
                {
                    manifestTask = _manifestTask;
                }
                else
                {
                    if (DateTime.UtcNow < _nextManifestAttemptUtc)
                    {
                        return;
                    }

                    _manifestTask = LoadManifestAsync();
                    manifestTask = _manifestTask;
                }
            }

            try
            {
                await manifestTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());

                lock (ManifestLock)
                {
                    _nextManifestAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                }
            }
            finally
            {
                lock (ManifestLock)
                {
                    if (ReferenceEquals(_manifestTask, manifestTask))
                    {
                        _manifestTask = null;
                    }
                }
            }
        }

        private static async Task LoadManifestAsync()
        {
            string cachePath = Path.Combine(Settings.AssetCachePath, StreamingAssetConstants.ManifestFileName);
            byte[] bytes = null;

            try
            {
                string url = MakeUrl(StreamingAssetConstants.ManifestFileName);
                using HttpResponseMessage response = await Client.GetAsync(url).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
                    await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());
            }

            if (bytes == null && File.Exists(cachePath))
            {
                bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }

            if (bytes == null)
            {
                lock (ManifestLock)
                {
                    _nextManifestAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                }

                return;
            }

            AssetManifest manifest = JsonSerializer.Deserialize<AssetManifest>(bytes, StreamingAssetIO.JsonOptions);
            lock (ManifestLock)
            {
                _manifest = manifest;
                _libraries = null;
                _maps = null;
                _sounds = null;
                _nextManifestAttemptUtc = DateTime.MinValue;
            }
        }

        private static void EnsureIndexes()
        {
            if (_libraries != null && _maps != null && _sounds != null) return;

            lock (ManifestLock)
            {
                if (_libraries != null && _maps != null && _sounds != null) return;
                _libraries = _manifest?.Libraries.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new Dictionary<string, AssetLibraryRecord>();
                _maps = _manifest?.Maps.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new Dictionary<string, AssetMapRecord>();
                _sounds = _manifest?.Sounds.ToDictionary(x => NormalizeId(x.Id), x => x) ?? new Dictionary<string, AssetSoundRecord>();
            }
        }

        private static string GetManifestVersion()
        {
            return _manifest?.Version?.Length > 0 ? _manifest.Version : "default";
        }

        private static string GetLibraryImageRelativePath(string libraryId, LibraryImageRecord image)
        {
            return $"{StreamingAssetConstants.LibrariesDirectory}/{NormalizeId(libraryId)}/{image.Path}";
        }

        private static string GetLibraryImageCachePath(string libraryId, LibraryImageRecord image)
        {
            return Path.Combine(CacheRoot, GetLibraryImageRelativePath(libraryId, image).Replace('/', Path.DirectorySeparatorChar));
        }

        private static string GetMapChunkRelativePath(string mapId, MapChunkRecord record)
        {
            return $"{StreamingAssetConstants.MapsDirectory}/{NormalizeId(mapId)}/{record.Path}";
        }

        private static string GetMapChunkCachePath(string mapId, MapChunkRecord record)
        {
            return Path.Combine(CacheRoot, GetMapChunkRelativePath(mapId, record).Replace('/', Path.DirectorySeparatorChar));
        }

        private static void QueueDownload(string relativePath, string hash, string cachePath)
        {
            if (TryReadValidCache(cachePath, hash, out _)) return;

            string key = $"{hash}:{cachePath}";
            Downloads.GetOrAdd(key, downloadKey => Task.Run(async () =>
            {
                try
                {
                    await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (TryReadValidCache(cachePath, hash, out _)) return;

                        byte[] bytes = await DownloadAssetAsync(relativePath, hash).ConfigureAwait(false);
                        if (bytes == null) return;

                        if (!string.IsNullOrEmpty(hash) && StreamingAssetIO.ComputeSha256(bytes) != hash)
                        {
                            CMain.SaveError($"Streaming asset hash mismatch: {relativePath}");
                            return;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
                        string temp = cachePath + ".tmp";
                        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);

                        if (File.Exists(cachePath)) File.Delete(cachePath);
                        File.Move(temp, cachePath);

                        NotifyAssetsUpdated();
                    }
                    finally
                    {
                        DownloadSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    CMain.SaveError(ex.ToString());
                }
                finally
                {
                    Downloads.TryRemove(key, out _);
                }
            }));
        }

        private static async Task<byte[]> GetAssetBytesAsync(string relativePath, string hash, string cachePath)
        {
            if (TryReadValidCache(cachePath, hash, out byte[] cached)) return cached;

            byte[] bytes = await DownloadAssetAsync(relativePath, hash).ConfigureAwait(false);
            if (bytes == null) return null;

            if (!string.IsNullOrEmpty(hash) && StreamingAssetIO.ComputeSha256(bytes) != hash)
            {
                CMain.SaveError($"Streaming asset hash mismatch: {relativePath}");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
            await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
            return bytes;
        }

        private static async Task<byte[]> DownloadAssetAsync(string relativePath, string hash = null)
        {
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(relativePath, hash)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());
                return null;
            }
        }

        private static bool TryReadValidCache(string path, string hash, out byte[] bytes)
        {
            bytes = null;
            if (!File.Exists(path)) return false;

            try
            {
                bytes = File.ReadAllBytes(path);
                if (!string.IsNullOrEmpty(hash) && StreamingAssetIO.ComputeSha256(bytes) != hash)
                {
                    bytes = null;
                    return false;
                }

                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static string MakeUrl(string relativePath, string hash = null)
        {
            string url = Settings.AssetBaseUrl.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrWhiteSpace(hash))
                url += (url.Contains('?') ? "&" : "?") + "h=" + Uri.EscapeDataString(hash);

            return url;
        }

        private static bool TrySaveMapBatch(string mapId, List<MapChunkDownload> pending, byte[] bytes)
        {
            try
            {
                Dictionary<string, MapChunkDownload> byKey = pending.ToDictionary(download => download.Record.Key);
                using MemoryStream stream = new(bytes);
                using BinaryReader reader = new(stream);
                int count = reader.ReadInt32();
                if (count < 0 || count > 64) return false;

                bool updated = false;
                for (int i = 0; i < count; i++)
                {
                    string key = reader.ReadString();
                    int length = reader.ReadInt32();
                    if (length < 0 || length > 64 * 1024 * 1024 || stream.Length - stream.Position < length)
                        return false;

                    byte[] chunk = reader.ReadBytes(length);
                    if (!byKey.TryGetValue(key, out MapChunkDownload download))
                        continue;

                    if (StreamingAssetIO.ComputeSha256(chunk) != download.Record.Hash)
                    {
                        CMain.SaveError($"Streaming asset hash mismatch: {download.RelativePath}");
                        continue;
                    }

                    WriteCacheFile(download.CachePath, chunk);
                    updated = true;
                }

                return updated;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid map chunk batch {mapId}: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> SaveVerifiedAssetAsync(byte[] bytes, string hash, string cachePath, string relativePath)
        {
            if (!string.IsNullOrEmpty(hash) && StreamingAssetIO.ComputeSha256(bytes) != hash)
            {
                CMain.SaveError($"Streaming asset hash mismatch: {relativePath}");
                return false;
            }

            await Task.Run(() => WriteCacheFile(cachePath, bytes)).ConfigureAwait(false);
            return true;
        }

        private static void WriteCacheFile(string cachePath, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
            string temp = cachePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(temp, cachePath);
        }

        private sealed record MapChunkDownload(MapChunkRecord Record, string RelativePath, string CachePath);

        private static IEnumerable<string> GetSoundIdCandidates(string soundName, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(soundName)) yield break;

            string candidate = soundName.Replace('\\', '/').TrimStart('.', '/');
            string soundRoot = Path.GetFullPath(Settings.SoundPath);

            try
            {
                string fullPath = Path.GetFullPath(soundName);
                if (fullPath.StartsWith(soundRoot, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.GetRelativePath(soundRoot, fullPath).Replace('\\', '/');
                }
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(Path.GetExtension(candidate)))
            {
                yield return NormalizeId(candidate);
                yield break;
            }

            foreach (string extension in extensions ?? Array.Empty<string>())
            {
                yield return NormalizeId(candidate + extension);
            }
        }

        private static void NotifyAssetsUpdated()
        {
            try
            {
                AssetsUpdated?.Invoke();

                if (MirScene.ActiveScene != null)
                    MirScene.ActiveScene.Redraw();

                if (GameScene.Scene?.MapControl != null && !GameScene.Scene.MapControl.IsDisposed)
                {
                    GameScene.Scene.MapControl.FloorValid = false;
                    GameScene.Scene.MapControl.Redraw();
                }
            }
            catch
            {
            }
        }
    }
}
