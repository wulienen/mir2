using Client.MirObjects;
using Client.MirScenes;
using Shared.StreamingAssets;

namespace Client.Streaming
{
    public sealed class StreamingMapState
    {
        private readonly string _mapId;
        private readonly MapManifest _manifest;
        private readonly Dictionary<string, MapChunkRecord> _chunks;
        private readonly HashSet<string> _loadedChunks = new HashSet<string>();

        private StreamingMapState(string mapId, MapManifest manifest)
        {
            _mapId = mapId;
            _manifest = manifest;
            _chunks = manifest.Chunks.ToDictionary(x => x.Key, x => x);
        }

        public int Width => _manifest.Width;
        public int Height => _manifest.Height;

        public static bool TryCreate(string localMapFile, out StreamingMapState state)
        {
            state = null;
            if (!AssetManager.Enabled) return false;

            string mapId = AssetManager.ToMapId(localMapFile);
            MapManifest manifest = AssetManager.GetMapManifest(mapId);
            if (manifest == null) return false;

            state = new StreamingMapState(mapId, manifest);
            return true;
        }

        public CellInfo[,] CreatePlaceholderCells()
        {
            CellInfo[,] cells = new CellInfo[Width, Height];
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    cells[x, y] = new CellInfo();
                }
            }

            return cells;
        }

        public bool IsLoaded(Point point)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height)
            {
                return false;
            }

            int chunkX = point.X / _manifest.ChunkSize * _manifest.ChunkSize;
            int chunkY = point.Y / _manifest.ChunkSize * _manifest.ChunkSize;
            return _loadedChunks.Contains($"{chunkX}_{chunkY}");
        }

        public void EnsureVisibleChunks(MapControl map)
        {
            if (MapControl.User == null || map.M2CellInfo == null) return;

            int startX = Math.Max(0, MapControl.User.Movement.X - MapControl.ViewRangeX - _manifest.ChunkSize);
            int endX = Math.Min(Width - 1, MapControl.User.Movement.X + MapControl.ViewRangeX + _manifest.ChunkSize);
            int startY = Math.Max(0, MapControl.User.Movement.Y - MapControl.ViewRangeY - _manifest.ChunkSize);
            int endY = Math.Min(Height - 1, MapControl.User.Movement.Y + MapControl.ViewRangeY + _manifest.ChunkSize + 25);

            bool changed = false;

            for (int chunkX = startX / _manifest.ChunkSize * _manifest.ChunkSize; chunkX <= endX; chunkX += _manifest.ChunkSize)
            {
                for (int chunkY = startY / _manifest.ChunkSize * _manifest.ChunkSize; chunkY <= endY; chunkY += _manifest.ChunkSize)
                {
                    string key = $"{chunkX}_{chunkY}";
                    if (_loadedChunks.Contains(key)) continue;
                    if (!_chunks.TryGetValue(key, out MapChunkRecord record)) continue;

                    if (!AssetManager.TryReadCachedMapChunk(_mapId, record, out StreamingMapChunk chunk))
                    {
                        AssetManager.QueueMapChunk(_mapId, record);
                        continue;
                    }

                    ApplyChunk(map, chunk);
                    _loadedChunks.Add(key);
                    changed = true;
                }
            }

            if (changed)
            {
                map.FloorValid = false;
                map.LightsValid = false;
                map.Redraw();
            }
        }

        private static void ApplyChunk(MapControl map, StreamingMapChunk chunk)
        {
            for (int x = 0; x < chunk.Width; x++)
            {
                for (int y = 0; y < chunk.Height; y++)
                {
                    int mapX = chunk.X + x;
                    int mapY = chunk.Y + y;
                    if (mapX < 0 || mapY < 0 || mapX >= map.Width || mapY >= map.Height) continue;

                    List<MapObject> objects = map.M2CellInfo[mapX, mapY].CellObjects;
                    map.M2CellInfo[mapX, mapY] = ToCellInfo(chunk.Cells[x * chunk.Height + y], objects);
                }
            }
        }

        private static CellInfo ToCellInfo(StreamingMapCell source, List<MapObject> objects)
        {
            return new CellInfo
            {
                BackIndex = source.BackIndex,
                BackImage = source.BackImage,
                MiddleIndex = source.MiddleIndex,
                MiddleImage = source.MiddleImage,
                FrontIndex = source.FrontIndex,
                FrontImage = source.FrontImage,
                DoorIndex = source.DoorIndex,
                DoorOffset = source.DoorOffset,
                FrontAnimationFrame = source.FrontAnimationFrame,
                FrontAnimationTick = source.FrontAnimationTick,
                MiddleAnimationFrame = source.MiddleAnimationFrame,
                MiddleAnimationTick = source.MiddleAnimationTick,
                TileAnimationImage = source.TileAnimationImage,
                TileAnimationOffset = source.TileAnimationOffset,
                TileAnimationFrames = source.TileAnimationFrames,
                Light = source.Light,
                Unknown = source.Unknown,
                FishingCell = source.FishingCell,
                CellObjects = objects
            };
        }
    }
}
