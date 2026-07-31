using Microsoft.Data.Sqlite;
using Shared.StreamingAssets;
using System.Collections.Concurrent;

namespace Client.Streaming
{
    internal sealed class AssetCacheDatabase : IDisposable
    {
        private readonly object _sync = new();
        private readonly string _path;
        private readonly long _maximumBytes;
        private readonly ConcurrentDictionary<string, byte> _verified = new(StringComparer.OrdinalIgnoreCase);
        private SqliteConnection _connection;
        private long _nextCleanupUtcTicks;

        public AssetCacheDatabase(string path, int maximumMegabytes)
            : this(path, Math.Max(256, maximumMegabytes) * 1024L * 1024L)
        {
        }

        private AssetCacheDatabase(string path, long maximumBytes)
        {
            _path = Path.GetFullPath(path);
            _maximumBytes = Math.Max(1, maximumBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            try
            {
                Open();
            }
            catch
            {
                Close();
                DeleteDatabaseFiles();
                Open();
            }
        }

        public bool TryGet(string hash, long expectedLength, out byte[] payload)
        {
            payload = null;
            if (!StreamingAssetIO.IsValidSha256(hash) || expectedLength < 0 || expectedLength > int.MaxValue)
                return false;

            lock (_sync)
            {
                try
                {
                    using SqliteCommand command = _connection.CreateCommand();
                    command.CommandText = "SELECT length, payload FROM asset_cache WHERE hash = $hash";
                    command.Parameters.AddWithValue("$hash", hash.ToLowerInvariant());
                    using SqliteDataReader reader = command.ExecuteReader();
                    if (!reader.Read()) return false;
                    long length = reader.GetInt64(0);
                    if (length != expectedLength)
                    {
                        reader.Close();
                        Delete(hash);
                        return false;
                    }
                    payload = (byte[])reader[1];
                    if (payload.LongLength != expectedLength ||
                        (!_verified.ContainsKey(hash) &&
                         !string.Equals(StreamingAssetIO.ComputeSha256(payload), hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        reader.Close();
                        payload = null;
                        Delete(hash);
                        return false;
                    }
                    _verified[hash] = 0;
                    reader.Close();
                    Touch(hash);
                    return true;
                }
                catch (Exception ex)
                {
                    CMain.SaveError($"Asset cache read failed: {ex.Message}");
                    payload = null;
                    return false;
                }
            }
        }

        public void Put(string hash, int kind, byte[] payload)
        {
            if (!StreamingAssetIO.IsValidSha256(hash) || payload == null ||
                !string.Equals(StreamingAssetIO.ComputeSha256(payload), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing to cache an asset with an invalid SHA-256 hash.");

            lock (_sync)
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                using SqliteCommand command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO asset_cache(hash, kind, length, last_access, payload)
                    VALUES($hash, $kind, $length, $access, $payload)
                    ON CONFLICT(hash) DO UPDATE SET
                        kind = excluded.kind,
                        length = excluded.length,
                        last_access = excluded.last_access,
                        payload = excluded.payload
                    """;
                command.Parameters.AddWithValue("$hash", hash.ToLowerInvariant());
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$length", payload.LongLength);
                command.Parameters.AddWithValue("$access", DateTime.UtcNow.Ticks);
                command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
                command.ExecuteNonQuery();
                transaction.Commit();
                _verified[hash] = 0;
                CleanupIfDue();
            }
        }

        public byte[] GetMetadata(string key)
        {
            lock (_sync)
            {
                try
                {
                    using SqliteCommand command = _connection.CreateCommand();
                    command.CommandText = "SELECT value FROM metadata WHERE key = $key";
                    command.Parameters.AddWithValue("$key", key);
                    return command.ExecuteScalar() as byte[];
                }
                catch { return null; }
            }
        }

        public void PutMetadata(string key, byte[] value)
        {
            lock (_sync)
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO metadata(key, value) VALUES($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.Add("$value", SqliteType.Blob).Value = value;
                command.ExecuteNonQuery();
            }
        }

        public void Cleanup()
        {
            lock (_sync)
            {
                try
                {
                    long target = (long)(_maximumBytes * 0.9);
                    long total = ExecuteInt64("SELECT COALESCE(SUM(length), 0) FROM asset_cache");
                    using SqliteTransaction transaction = _connection.BeginTransaction();
                    while (total > target)
                    {
                        string oldestHash;
                        long oldestLength;
                        using (SqliteCommand select = _connection.CreateCommand())
                        {
                            select.Transaction = transaction;
                            select.CommandText = "SELECT hash, length FROM asset_cache ORDER BY last_access ASC LIMIT 1";
                            using SqliteDataReader reader = select.ExecuteReader();
                            if (!reader.Read()) break;
                            oldestHash = reader.GetString(0);
                            oldestLength = reader.GetInt64(1);
                        }
                        using SqliteCommand delete = _connection.CreateCommand();
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM asset_cache WHERE hash = $hash";
                        delete.Parameters.AddWithValue("$hash", oldestHash);
                        if (delete.ExecuteNonQuery() == 0) break;
                        _verified.TryRemove(oldestHash, out _);
                        total -= oldestLength;
                    }
                    transaction.Commit();
                    using SqliteCommand vacuum = _connection.CreateCommand();
                    vacuum.CommandText = "PRAGMA incremental_vacuum(256)";
                    vacuum.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CMain.SaveError($"Asset cache cleanup failed: {ex.Message}");
                }
            }
        }

        private void Open()
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());
            _connection.Open();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA auto_vacuum=INCREMENTAL;
                CREATE TABLE IF NOT EXISTS asset_cache (
                    hash TEXT PRIMARY KEY NOT NULL,
                    kind INTEGER NOT NULL,
                    length INTEGER NOT NULL,
                    last_access INTEGER NOT NULL,
                    payload BLOB NOT NULL
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_asset_cache_access ON asset_cache(last_access);
                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY NOT NULL,
                    value BLOB NOT NULL
                ) WITHOUT ROWID;
                """;
            command.ExecuteNonQuery();
        }

        private void Touch(string hash)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE asset_cache SET last_access = $access WHERE hash = $hash";
            command.Parameters.AddWithValue("$access", DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$hash", hash.ToLowerInvariant());
            command.ExecuteNonQuery();
        }

        private void Delete(string hash)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM asset_cache WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash.ToLowerInvariant());
            command.ExecuteNonQuery();
            _verified.TryRemove(hash, out _);
        }

        private long ExecuteInt64(string sql)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private void CleanupIfDue()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref _nextCleanupUtcTicks)) return;
            Interlocked.Exchange(ref _nextCleanupUtcTicks, DateTime.UtcNow.AddMinutes(5).Ticks);
            Cleanup();
        }

        internal static void SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), $"YangfeiAssetCacheSelfTest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "AssetsV3.db");
            try
            {
                byte[] first = CreatePayload(1, 1024 * 1024);
                byte[] second = CreatePayload(2, 1024 * 1024);
                byte[] third = CreatePayload(3, 1024 * 1024);
                string firstHash = StreamingAssetIO.ComputeSha256(first);
                string secondHash = StreamingAssetIO.ComputeSha256(second);
                string thirdHash = StreamingAssetIO.ComputeSha256(third);

                using (AssetCacheDatabase cache = new(path, 5L * 1024 * 1024))
                {
                    cache.Put(firstHash, 1, first);
                    cache.PutMetadata("manifest-v3", new byte[] { 1, 2, 3 });
                    Require(cache.TryGet(firstHash, first.Length, out byte[] read) && read.SequenceEqual(first),
                        "Put/Get failed.");
                }

                using (AssetCacheDatabase cache = new(path, 5L * 1024 * 1024))
                {
                    Require(cache.TryGet(firstHash, first.Length, out byte[] read) && read.SequenceEqual(first),
                        "Restart reuse failed.");
                    Require(cache.GetMetadata("manifest-v3")?.SequenceEqual(new byte[] { 1, 2, 3 }) == true,
                        "Metadata reuse failed.");

                    cache.Put(secondHash, 1, second);
                    Thread.Sleep(2);
                    Require(cache.TryGet(firstHash, first.Length, out _), "LRU touch failed.");
                    Thread.Sleep(2);
                    cache.Put(thirdHash, 1, third);
                }

                using (AssetCacheDatabase cache = new(path, 2500L * 1024))
                {
                    cache.Cleanup();
                    Require(cache.TryGet(firstHash, first.Length, out _), "LRU removed a recently used entry.");
                    Require(!cache.TryGet(secondHash, second.Length, out _), "LRU did not remove the oldest entry.");
                    Require(cache.TryGet(thirdHash, third.Length, out _), "LRU removed the newest entry.");
                }

                File.WriteAllBytes(path, new byte[] { 0x42, 0x41, 0x44 });
                using (AssetCacheDatabase cache = new(path, 5L * 1024 * 1024))
                {
                    Require(cache.GetMetadata("manifest-v3") == null, "Corrupt database was not rebuilt.");
                    cache.Put(firstHash, 1, first);
                    Require(cache.TryGet(firstHash, first.Length, out _), "Rebuilt database is not usable.");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static byte[] CreatePayload(int seed, int length)
        {
            byte[] payload = new byte[length];
            new Random(seed).NextBytes(payload);
            return payload;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private void DeleteDatabaseFiles()
        {
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(_path + suffix); } catch { }
            }
        }

        private void Close()
        {
            try { _connection?.Dispose(); } catch { }
            _connection = null;
        }

        public void Dispose()
        {
            lock (_sync) Close();
        }
    }
}
