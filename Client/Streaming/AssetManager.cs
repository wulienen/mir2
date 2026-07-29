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
        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> ObjectLoads = new();
        private static readonly ConcurrentDictionary<string, byte> BackgroundLoads = new();
        private static readonly ConcurrentDictionary<string, RetryState> RetryStates = new();
        private static readonly ConcurrentDictionary<string, LibraryManifestPage> PageCache = new();
        private static readonly ConcurrentDictionary<string, long> VerifiedObjects = new();
        private static readonly ConcurrentDictionary<string, byte> PendingBatchObjects = new();
        private static readonly object ManifestLock = new();
        private static SemaphoreSlim DownloadSemaphore = new(4);
        private static readonly SemaphoreSlim BatchDownloadSemaphore = new(1);

        private static AssetManifest _manifest;
        private static Dictionary<string, AssetLibraryRecord> _libraries;
        private static Dictionary<string, AssetMapRecord> _maps;
        private static Dictionary<string, AssetSoundRecord> _sounds;
        private static Task _manifestTask;
        private static DateTime _nextManifestAttemptUtc = DateTime.MinValue;

        public static event Action AssetsUpdated;

        public static bool Enabled => Settings.StreamingEnabled && !string.IsNullOrWhiteSpace(Settings.AssetBaseUrl);
        public static string CacheRoot => Settings.AssetCachePath;

        public static string NormalizeId(string id)
        {
            return (id ?? string.Empty).Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();
        }

        public static void Initialize()
        {
            if (!Enabled) return;

            Client.Timeout = TimeSpan.FromSeconds(Math.Max(5, Settings.AssetRequestTimeoutSeconds));
            DownloadSemaphore = new SemaphoreSlim(Math.Max(1, Settings.AssetDownloadConcurrency));
            _ = Task.Run(CleanupCache);
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
            if (Manifest == null) return false;
            EnsureIndexes();
            return _libraries.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetMapRecord(string id, out AssetMapRecord record)
        {
            record = null;
            if (Manifest == null) return false;
            EnsureIndexes();
            return _maps.TryGetValue(NormalizeId(id), out record);
        }

        public static bool TryGetSoundRecord(string id, out AssetSoundRecord record)
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

                if (int.TryParse(name, out int index) && index > maxIndex) maxIndex = index;
            }

            return maxIndex + 1;
        }

        public static async Task<LibraryManifest> GetLibraryManifestAsync(string libraryId)
        {
            if (!TryGetLibraryRecord(libraryId, out AssetLibraryRecord record)) return null;
            byte[] bytes = await GetObjectBytesAsync(record.Hash, record.Length).ConfigureAwait(false);
            if (bytes == null) return null;

            try
            {
                LibraryManifest manifest = JsonSerializer.Deserialize<LibraryManifest>(bytes, StreamingAssetIO.JsonOptions);
                return IsValidLibraryManifest(manifest, libraryId, record.ImageCount) ? manifest : null;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid library manifest {libraryId}: {ex.Message}");
                return null;
            }
        }

        public static LibraryManifest GetLibraryManifest(string libraryId)
        {
            return GetLibraryManifestAsync(libraryId).GetAwaiter().GetResult();
        }

        public static bool TryGetCachedLibraryManifest(string libraryId, out LibraryManifest manifest)
        {
            manifest = null;
            if (!TryGetLibraryRecord(libraryId, out AssetLibraryRecord record) ||
                !TryReadValidObject(record.Hash, record.Length, null, out byte[] bytes)) return false;

            try
            {
                manifest = JsonSerializer.Deserialize<LibraryManifest>(bytes, StreamingAssetIO.JsonOptions);
                return IsValidLibraryManifest(manifest, libraryId, record.ImageCount);
            }
            catch
            {
                manifest = null;
                return false;
            }
        }

        public static void QueueLibraryManifest(string libraryId)
        {
            if (TryGetLibraryRecord(libraryId, out AssetLibraryRecord record))
                QueueObject(record.Hash, record.Length);
        }

        public static bool TryGetCachedLibraryPage(string libraryId, LibraryManifest manifest, int imageIndex,
            out LibraryManifestPage page)
        {
            page = null;
            if (!TryGetPageRecord(manifest, imageIndex, out int pageIndex, out LibraryManifestPageRecord record))
                return false;
            if (PageCache.TryGetValue(record.Hash, out page)) return true;
            if (!TryReadValidObject(record.Hash, record.Length, null, out byte[] bytes)) return false;

            page = ParseLibraryPage(bytes, pageIndex, record);
            if (page == null) return false;
            PageCache.TryAdd(record.Hash, page);
            return true;
        }

        public static void QueueLibraryPage(string libraryId, LibraryManifest manifest, int imageIndex)
        {
            if (!TryGetPageRecord(manifest, imageIndex, out int pageIndex, out LibraryManifestPageRecord record) ||
                PageCache.ContainsKey(record.Hash) || !CanAttempt(record.Hash) ||
                !BackgroundLoads.TryAdd("page:" + record.Hash, 0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = await GetObjectBytesAsync(record.Hash, record.Length).ConfigureAwait(false);
                    LibraryManifestPage page = bytes == null ? null : ParseLibraryPage(bytes, pageIndex, record);
                    if (page != null)
                    {
                        PageCache[record.Hash] = page;
                        NotifyAssetsUpdated();
                    }
                }
                finally
                {
                    BackgroundLoads.TryRemove("page:" + record.Hash, out _);
                }
            });
        }

        public static List<LibraryImageRecord> GetAllLibraryImages(LibraryManifest manifest)
        {
            if (manifest == null) return null;
            Task<LibraryManifestPage>[] tasks = manifest.Pages.Select(record =>
                GetLibraryPageAsync(record.Start / manifest.PageSize, record)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            if (tasks.Any(task => task.Result == null)) return null;
            return tasks.SelectMany(task => task.Result.Images).ToList();
        }

        public static async Task<MapManifest> GetMapManifestAsync(string mapId)
        {
            if (!TryGetMapRecord(mapId, out AssetMapRecord record)) return null;
            byte[] bytes = await GetObjectBytesAsync(record.Hash, record.Length).ConfigureAwait(false);
            if (bytes == null) return null;

            try
            {
                MapManifest manifest = JsonSerializer.Deserialize<MapManifest>(bytes, StreamingAssetIO.JsonOptions);
                return manifest?.FormatVersion == StreamingAssetConstants.CurrentFormatVersion &&
                       string.Equals(NormalizeId(manifest.Id), NormalizeId(mapId), StringComparison.Ordinal)
                    ? manifest
                    : null;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid map manifest {mapId}: {ex.Message}");
                return null;
            }
        }

        public static MapManifest GetMapManifest(string mapId)
        {
            return GetMapManifestAsync(mapId).GetAwaiter().GetResult();
        }

        public static bool TryReadCachedLibraryImage(string libraryId, LibraryImageRecord image,
            out StreamingLibraryImageChunk chunk)
        {
            chunk = null;
            if (image == null || !image.Exists ||
                !TryReadValidObject(image.Hash, image.Length, null, out byte[] bytes)) return false;

            try
            {
                chunk = StreamingAssetIO.ReadLibraryImageChunk(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void QueueLibraryImage(string libraryId, LibraryImageRecord image)
        {
            if (image?.Exists == true) QueueObject(image.Hash, image.Length);
        }

        public static bool TryReadCachedMapChunk(string mapId, MapChunkRecord record, out StreamingMapChunk chunk)
        {
            chunk = null;
            if (record == null || !StreamingAssetIO.IsValidSha256(record.Hash) ||
                !TryReadValidObject(record.Hash, record.Length, null, out byte[] bytes)) return false;
            try
            {
                chunk = StreamingAssetIO.ReadMapChunk(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void QueueMapChunk(string mapId, MapChunkRecord record)
        {
            if (record != null) QueueObject(record.Hash, record.Length);
        }

        public static void QueueMapChunks(string mapId, IEnumerable<MapChunkRecord> records)
        {
            if (records == null) return;
            List<MapChunkRecord> pending = records.Where(record => record != null &&
                    StreamingAssetIO.IsValidSha256(record.Hash) && CanAttempt(record.Hash) &&
                    !IsValidCachedObject(record.Hash, record.Length, null))
                .GroupBy(record => record.Hash).Select(group => group.First())
                .Where(record => PendingBatchObjects.TryAdd(record.Hash, 0))
                .Take(64)
                .ToList();
            if (pending.Count == 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await BatchDownloadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!await DownloadObjectBatchAsync(pending).ConfigureAwait(false))
                            foreach (MapChunkRecord record in pending) QueueObject(record.Hash, record.Length);
                        else
                            NotifyAssetsUpdated();
                    }
                    finally
                    {
                        BatchDownloadSemaphore.Release();
                    }
                }
                finally
                {
                    foreach (MapChunkRecord record in pending) PendingBatchObjects.TryRemove(record.Hash, out _);
                }
            });
        }

        public static bool TryGetCachedSoundPath(string soundId, out string path)
        {
            path = null;
            if (!TryGetSoundRecord(soundId, out AssetSoundRecord record)) return false;
            string extension = NormalizeSoundExtension(record.Extension);
            if (!IsValidCachedObject(record.Hash, record.Length, extension)) return false;
            path = GetObjectCachePath(record.Hash, extension);
            return true;
        }

        public static bool TryGetCachedSoundPath(string soundName, IEnumerable<string> extensions, out string path)
        {
            foreach (string soundId in GetSoundIdCandidates(soundName, extensions))
                if (TryGetCachedSoundPath(soundId, out path)) return true;
            path = null;
            return false;
        }

        public static void QueueSound(string soundId)
        {
            if (TryGetSoundRecord(soundId, out AssetSoundRecord record))
                QueueObject(record.Hash, record.Length, NormalizeSoundExtension(record.Extension));
        }

        public static void QueueSound(string soundName, IEnumerable<string> extensions)
        {
            foreach (string soundId in GetSoundIdCandidates(soundName, extensions))
            {
                if (!TryGetSoundRecord(soundId, out _)) continue;
                QueueSound(soundId);
                return;
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

        private static async Task<LibraryManifestPage> GetLibraryPageAsync(int pageIndex,
            LibraryManifestPageRecord record)
        {
            if (PageCache.TryGetValue(record.Hash, out LibraryManifestPage cached)) return cached;
            byte[] bytes = await GetObjectBytesAsync(record.Hash, record.Length).ConfigureAwait(false);
            LibraryManifestPage page = bytes == null ? null : ParseLibraryPage(bytes, pageIndex, record);
            if (page != null) PageCache[record.Hash] = page;
            return page;
        }

        private static LibraryManifestPage ParseLibraryPage(byte[] bytes, int pageIndex,
            LibraryManifestPageRecord record)
        {
            try
            {
                LibraryManifestPage page = StreamingAssetIO.ReadLibraryIndexPage(bytes);
                if (page.PageIndex != pageIndex || page.Start != record.Start || page.Images.Count != record.Count)
                    return null;
                for (int i = 0; i < page.Images.Count; i++)
                    if (page.Images[i].Index != record.Start + i) return null;
                return page;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Invalid library index page {record.Hash}: {ex.Message}");
                return null;
            }
        }

        private static bool TryGetPageRecord(LibraryManifest manifest, int imageIndex, out int pageIndex,
            out LibraryManifestPageRecord record)
        {
            pageIndex = -1;
            record = null;
            if (manifest == null || manifest.PageSize != StreamingAssetConstants.LibraryIndexPageSize ||
                imageIndex < 0 || imageIndex >= manifest.ImageCount) return false;
            pageIndex = imageIndex / manifest.PageSize;
            if (pageIndex < 0 || pageIndex >= manifest.Pages.Count) return false;
            record = manifest.Pages[pageIndex];
            return record.Start <= imageIndex && imageIndex < record.Start + record.Count &&
                   StreamingAssetIO.IsValidSha256(record.Hash);
        }

        private static bool IsValidLibraryManifest(LibraryManifest manifest, string libraryId, int imageCount)
        {
            if (manifest == null || manifest.FormatVersion != StreamingAssetConstants.CurrentFormatVersion ||
                manifest.PageSize != StreamingAssetConstants.LibraryIndexPageSize ||
                manifest.ImageCount != imageCount ||
                !string.Equals(NormalizeId(manifest.Id), NormalizeId(libraryId), StringComparison.Ordinal)) return false;

            int expectedStart = 0;
            foreach (LibraryManifestPageRecord page in manifest.Pages)
            {
                if (page.Start != expectedStart || page.Count <= 0 || page.Count > manifest.PageSize ||
                    page.Length <= 0 || !StreamingAssetIO.IsValidSha256(page.Hash)) return false;
                expectedStart += page.Count;
            }
            return expectedStart == imageCount;
        }

        private static void QueueObject(string hash, long length, string extension = null)
        {
            if (!Enabled || !StreamingAssetIO.IsValidSha256(hash) || length < 0 ||
                IsValidCachedObject(hash, length, extension) || !CanAttempt(hash) ||
                !BackgroundLoads.TryAdd("object:" + hash + extension, 0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (await GetObjectBytesAsync(hash, length, extension).ConfigureAwait(false) != null)
                        NotifyAssetsUpdated();
                }
                finally
                {
                    BackgroundLoads.TryRemove("object:" + hash + extension, out _);
                }
            });
        }

        private static async Task<byte[]> GetObjectBytesAsync(string hash, long length, string extension = null)
        {
            if (!StreamingAssetIO.IsValidSha256(hash) || length < 0) return null;
            if (TryReadValidObject(hash, length, extension, out byte[] cached)) return cached;

            string key = GetObjectCachePath(hash, extension);
            Lazy<Task<byte[]>> lazy = ObjectLoads.GetOrAdd(key,
                _ => new Lazy<Task<byte[]>>(() => DownloadObjectAsync(hash, length, extension), true));
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                ObjectLoads.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy));
            }
        }

        private static async Task<byte[]> DownloadObjectAsync(string hash, long length, string extension)
        {
            if (!CanAttempt(hash)) return null;
            await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryReadValidObject(hash, length, extension, out byte[] cached)) return cached;

                string relative = StreamingAssetIO.GetObjectRelativePath(hash);
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(relative)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    RecordFailure(hash);
                    return null;
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.LongLength != length || StreamingAssetIO.ComputeSha256(bytes) != hash)
                {
                    CMain.SaveError($"Streaming asset validation failed: {hash}");
                    RecordFailure(hash);
                    return null;
                }

                WriteCacheFile(GetObjectCachePath(hash, extension), bytes);
                RetryStates.TryRemove(hash, out _);
                return bytes;
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());
                RecordFailure(hash);
                return null;
            }
            finally
            {
                DownloadSemaphore.Release();
            }
        }

        private static async Task<bool> DownloadObjectBatchAsync(List<MapChunkRecord> records)
        {
            try
            {
                string hashes = string.Join(',', records.Select(record => record.Hash));
                using HttpResponseMessage response = await Client.GetAsync(
                    MakeUrl($"{StreamingAssetConstants.ObjectsDirectory}/batch?hashes={Uri.EscapeDataString(hashes)}"))
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;

                byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Dictionary<string, MapChunkRecord> expected = records.ToDictionary(record => record.Hash);
                using MemoryStream input = new(data, false);
                using BinaryReader reader = new(input);
                int count = reader.ReadInt32();
                if (count != records.Count) return false;

                for (int i = 0; i < count; i++)
                {
                    string hash = reader.ReadString();
                    int length = reader.ReadInt32();
                    if (!expected.TryGetValue(hash, out MapChunkRecord record) || length < 0 ||
                        length != record.Length || input.Length - input.Position < length) return false;
                    byte[] bytes = reader.ReadBytes(length);
                    if (StreamingAssetIO.ComputeSha256(bytes) != hash) return false;
                    WriteCacheFile(GetObjectCachePath(hash), bytes);
                    RetryStates.TryRemove(hash, out _);
                }

                return input.Position == input.Length;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming batch download failed: {ex.Message}");
                foreach (MapChunkRecord record in records) RecordFailure(record.Hash);
                return false;
            }
        }

        private static bool TryReadValidObject(string hash, long length, string extension, out byte[] bytes)
        {
            bytes = null;
            string path = GetObjectCachePath(hash, extension);
            try
            {
                FileInfo info = new(path);
                if (!info.Exists || info.Length != length) return false;
                bytes = File.ReadAllBytes(path);
                if (VerifiedObjects.TryGetValue(path, out long verifiedLength) && verifiedLength == length)
                    return true;
                if (StreamingAssetIO.ComputeSha256(bytes) == hash)
                {
                    VerifiedObjects[path] = length;
                    TryTouch(path);
                    return true;
                }
                bytes = null;
                VerifiedObjects.TryRemove(path, out _);
                File.Delete(path);
            }
            catch
            {
                bytes = null;
            }
            return false;
        }

        private static bool IsValidCachedObject(string hash, long length, string extension)
        {
            string path = GetObjectCachePath(hash, extension);
            try
            {
                FileInfo info = new(path);
                if (!info.Exists || info.Length != length) return false;
                if (VerifiedObjects.TryGetValue(path, out long verifiedLength) && verifiedLength == length)
                    return true;
                using FileStream stream = File.OpenRead(path);
                if (StreamingAssetIO.ComputeSha256(stream) != hash) return false;
                VerifiedObjects[path] = length;
                TryTouch(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetObjectCachePath(string hash, string extension = null)
        {
            string suffix = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.ToLowerInvariant();
            return Path.Combine(CacheRoot, StreamingAssetConstants.ObjectsDirectory, hash[..2], hash + suffix);
        }

        private static string NormalizeSoundExtension(string extension)
        {
            string normalized = (extension ?? string.Empty).ToLowerInvariant();
            return normalized is ".wav" or ".mp3" ? normalized : ".bin";
        }

        private static bool CanAttempt(string hash)
        {
            return !RetryStates.TryGetValue(hash, out RetryState state) || DateTime.UtcNow >= state.NextAttemptUtc;
        }

        private static void RecordFailure(string hash)
        {
            RetryStates.AddOrUpdate(hash,
                _ => new RetryState(1, DateTime.UtcNow.AddSeconds(1)),
                (_, old) =>
                {
                    int failures = Math.Min(old.Failures + 1, 6);
                    int seconds = Math.Min(30, 1 << Math.Min(failures - 1, 5));
                    return new RetryState(failures, DateTime.UtcNow.AddSeconds(seconds));
                });
        }

        private static async Task EnsureManifestAsync()
        {
            if (_manifest != null) return;
            Task manifestTask;
            lock (ManifestLock)
            {
                if (_manifest != null) return;
                if (_manifestTask != null) manifestTask = _manifestTask;
                else
                {
                    if (DateTime.UtcNow < _nextManifestAttemptUtc) return;
                    _manifestTask = LoadManifestAsync();
                    manifestTask = _manifestTask;
                }
            }

            try
            {
                await manifestTask.ConfigureAwait(false);
            }
            finally
            {
                lock (ManifestLock)
                    if (ReferenceEquals(_manifestTask, manifestTask)) _manifestTask = null;
            }
        }

        private static async Task LoadManifestAsync()
        {
            string cachePath = Path.Combine(CacheRoot, StreamingAssetConstants.ManifestFileName);
            byte[] bytes = null;
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(MakeUrl(StreamingAssetConstants.ManifestFileName))
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    byte[] downloaded = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (TryParseRootManifest(downloaded, out _))
                    {
                        bytes = downloaded;
                        WriteCacheFile(cachePath, bytes);
                    }
                }
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());
            }

            if (bytes == null && File.Exists(cachePath))
            {
                try { bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false); }
                catch { }
            }

            if (!TryParseRootManifest(bytes, out AssetManifest manifest))
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

        private static bool TryParseRootManifest(byte[] bytes, out AssetManifest manifest)
        {
            manifest = null;
            if (bytes == null) return false;
            try
            {
                manifest = JsonSerializer.Deserialize<AssetManifest>(bytes, StreamingAssetIO.JsonOptions);
                return manifest?.FormatVersion == StreamingAssetConstants.CurrentFormatVersion &&
                       manifest.Libraries.All(record => StreamingAssetIO.IsValidSha256(record.Hash)) &&
                       manifest.Maps.All(record => StreamingAssetIO.IsValidSha256(record.Hash)) &&
                       manifest.Sounds.All(record => StreamingAssetIO.IsValidSha256(record.Hash));
            }
            catch
            {
                manifest = null;
                return false;
            }
        }

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

        private static string MakeUrl(string relativePath)
        {
            return Settings.AssetBaseUrl.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');
        }

        private static void WriteCacheFile(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            string temp = path + $".{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
            VerifiedObjects[path] = bytes.LongLength;
            TryTouch(path);
        }

        private static void TryTouch(string path)
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch { }
        }

        private static void CleanupCache()
        {
            try
            {
                Directory.CreateDirectory(CacheRoot);
                string objectsRoot = Path.GetFullPath(Path.Combine(CacheRoot, StreamingAssetConstants.ObjectsDirectory));
                foreach (string directory in Directory.EnumerateDirectories(CacheRoot))
                {
                    if (string.Equals(Path.GetFullPath(directory), objectsRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.Delete(directory, true);
                }

                long maximumBytes = Math.Max(256, Settings.AssetCacheMaxMB) * 1024L * 1024L;
                List<FileInfo> files = Directory.Exists(objectsRoot)
                    ? Directory.EnumerateFiles(objectsRoot, "*", SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path)).Where(info => !info.Name.EndsWith(".tmp")).ToList()
                    : new();
                long total = files.Sum(info => info.Length);
                foreach (FileInfo file in files.OrderBy(info => info.LastAccessTimeUtc))
                {
                    if (total <= maximumBytes) break;
                    try
                    {
                        long length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Streaming cache cleanup failed: {ex.Message}");
            }
        }

        private static IEnumerable<string> GetSoundIdCandidates(string soundName, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(soundName)) yield break;
            string candidate = soundName.Replace('\\', '/').TrimStart('.', '/');
            string soundRoot = Path.GetFullPath(Settings.SoundPath);
            try
            {
                string fullPath = Path.GetFullPath(soundName);
                if (fullPath.StartsWith(soundRoot, StringComparison.OrdinalIgnoreCase))
                    candidate = Path.GetRelativePath(soundRoot, fullPath).Replace('\\', '/');
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

        private static void NotifyAssetsUpdated()
        {
            try
            {
                AssetsUpdated?.Invoke();
                MirScene.ActiveScene?.Redraw();
                if (GameScene.Scene?.MapControl != null && !GameScene.Scene.MapControl.IsDisposed)
                {
                    GameScene.Scene.MapControl.FloorValid = false;
                    GameScene.Scene.MapControl.Redraw();
                }
            }
            catch { }
        }

        private sealed record RetryState(int Failures, DateTime NextAttemptUtc);
    }
}
