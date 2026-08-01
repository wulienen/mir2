# AssetBuilder V3 使用说明

AssetBuilder V3 把完整客户端资源发布成 AssetServer 可按需发送的资源。V3 不再把每张图片拆成一个小文件：图库保留为原始 `.Lib`，客户端只用 HTTP Range 下载当前需要的图片字节；地图按 32x32 格写入 Map Pack；声音按原文件发布。

客户端把流式资源写入一个缓存目录 `Cache\AssetsV3`（内含稀疏图库容器 `libs` 与整体对象 `blobs`），不会向 `Data`、`Map` 或 `Sound` 写入不完整资源。

## 1. 准备目录

下面按当前机器目录举例：

```text
图库：E:\GameSourceCode\CrystalMirFullAssets\Data_Full
地图：E:\GameSourceCode\CrystalMirFullAssets\Map
声音：E:\GameSourceCode\CrystalMirFullAssets\Sound
输出：E:\GameSourceCode\StreamingAssetsV3
```

`--library-root` 只扫描其中的 `*.Lib`，`--map-root` 只扫描顶层 `*.map`，`--sound-root` 只扫描 `*.wav`、`*.mp3` 和 `*.lst`。客户端 EXE、DLL、INI 和登录器文件不会被处理。

如果实际的 Map 或 Sound 在别处，把命令中的对应路径换成真实目录即可。四个路径都可以使用绝对路径，推荐生产环境使用绝对路径。

## 2. 第一次生成

在仓库根目录执行：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3" `
  --version "v3-initial"
```

如果当前目录已经是 `Tools\AssetBuilder`，项目参数可写成：

```powershell
dotnet run --project AssetBuilder.csproj -- build-v3 ...
```

第一次需要读取并复制全部 Lib、地图和声音，耗时取决于磁盘速度。V3 不会生成数百万个图片小文件，因此复制、备份和迁移会比旧版快很多。

完成时会看到类似：

```text
Streaming assets V3 built at E:\GameSourceCode\StreamingAssetsV3
Libraries: 1234, Maps: 468, Sounds: 1608
Rebuilt: 3310, Reused: 0, Failed: 0
Manifest version: v3-initial
```

只有 `Failed: 0` 才算成功。任一资源失败时，正式 `manifest.json` 不会被替换，AssetServer 会继续提供上一版完整资源。

## 3. 日常增量更新

新增或修改资源后，重新执行同一条命令即可。建议每次发布使用新的版本号：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3" `
  --version "20260730-01"
```

AssetBuilder 会读取 `.assetbuilder-v3-state.json` 自动增量处理：

| 源资源变化 | 实际处理 |
| --- | --- |
| 修改一个 Lib | 只重新索引并发布这个 Lib |
| 修改一张地图 | 只重建这张地图的 Map Pack |
| 修改一个声音 | 只发布这个声音 |
| 没有变化 | 全部复用，不重新读取 8GB 内容 |

`Rebuilt` 是本次处理数量，`Reused` 是直接复用数量。正常更新不需要 `--full`，也不需要删除输出目录。

如果怀疑文件内容变了但大小和修改时间被工具保留了，增加 `--verify`。它会计算源文件哈希，速度比普通增量检查慢，但仍只重建内容真正变化的资源：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3" `
  --verify
```

`--full` 会强制重新解析全部资源，只在状态文件丢失、输出损坏或排查构建器问题时使用。

`--prune` 在发布成功后删除新清单不再引用的已发布文件。资源是按内容哈希命名的，格式变更或
资源更新都会把旧文件永久留在输出目录里（一次 Catalog 分段改造 + Map Pack 改造就留下了
295 MB 无法访问的文件）。它是可选开关，因为删除后就无法再对外提供上一版清单：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3" `
  --prune
```

如果输出目录下存在录制文件 `workset-usage.txt`，构建会自动据此发布首启工作集包，无需额外参数；也可以用 `--usage` 指定别处的录制文件。详见第 6 节。

## 4. 启动 AssetServer

修改 `AssetServer\appsettings.json`：

```json
{
  "Urls": "http://0.0.0.0:8088",
  "AssetRoot": "E:\\GameSourceCode\\StreamingAssetsV3",
  "CacheSeconds": 31536000
}
```

`AssetRoot` 必须指向包含 V3 `manifest.json`、`catalogs`、`libraries`、`maps`、`sounds` 和（发布过工作集时）`worksets` 的输出目录，不能指向 `Data_Full`。

启动服务：

```powershell
dotnet run --project AssetServer\AssetServer.csproj
```

检查：

```text
http://127.0.0.1:8088/health
```

返回 `ok=true`、`formatVersion=3` 才表示目录和清单有效。对外使用时还要在防火墙或云安全组放行 TCP 8088；客户端中的 IP 应填写资源服务器对玩家可访问的地址，不能填写玩家自己的 `127.0.0.1`。

## 5. 客户端配置

```ini
[Streaming]
Enabled=True
AssetBaseUrl=http://资源服务器IP:8088/assets/v3/
PreferLocalAssets=True
ConcurrentDownloads=4
RequestTimeoutSeconds=30
CachePath=.\Cache\AssetsV3
CacheMaxMB=4096
RecordWorkingSet=False
```

- `ConcurrentDownloads` 是所有流式请求的总并发上限（1–32，默认 16）。客户端内部分两条通道：地图包、Catalog 索引和首启工作集走优先通道，图片批量和声音走普通通道，普通通道最多只能占用其中的 3/4，另外 1/4 永远留给优先通道。这样进图时地图包不会排在几百个图片请求后面——`GameScene.IsCellLoaded` 在地图包应用完之前一直拦着走路，本机看不出来，真实线路上就是"进图卡几秒"。
- `PreferLocalAssets=True`：本地存在结构完整的 `Data\*.Lib` 或 `Map\*.map` 时优先使用本地文件，完整客户端不依赖资源服。
- 本地 Lib 会覆盖资源服同名 Lib。要让某个本地 Lib 使用资源服新版本，应替换/删除这个完整 Lib，或把 `PreferLocalAssets` 改为 `False`。
- 微端不要把部分下载的 Lib 放进 `Data`。流式字节只进入 `Cache\AssetsV3`，避免半个 Lib 被误判为完整资源。
- `CachePath` 是目录，不是 `.db` 文件；配置里残留旧的 `AssetsV3.db` 会被客户端自动纠正为默认目录。
- 资源更新后根清单会引用新的 SHA-256。客户端自动下载变化部分并继续复用旧缓存，不需要手工删除 `Data` 或 `Cache`。
- `RecordWorkingSet` 只在录制首启工作集时打开，见第 6 节；玩家保持 `False`。
- `Cache\AssetsV3\libs\*.libpart` 是稀疏文件：文件长度等于整个 Lib 的长度，但只有已下载的字节占用磁盘（例如 `tiles.libpart` 长度 429.5 MB、实际占用 0.3 MB）。资源管理器的「大小」列和 `dir` 显示的是前者，「占用空间」才是真实开销；`CacheMaxMB` 统计的也是真实字节。客户端不要装在 FAT32 或 exFAT 分区上，那里无法创建稀疏文件，容器会按 Lib 全长真实占盘。

## 6. 首启工作集（可选，但强烈建议）

冷启动最慢的不是某一张图片，而是「登录 → 选角 → 第一张地图」这段时间里几百次零散的 Range 往返。工作集包把这段时间真实用到的图片记录合成一个文件，客户端一次 GET 取回并直接写进本地稀疏容器。整库预取已被排除：七个启动图库的像素合计 88.96 MB，工作集只有其中很小一部分。

录制一次（在发布者自己的机器上做，玩家不需要）：

1. 用一个干净微端：删掉 `Cache\AssetsV3`，并确认 `PreferLocalAssets=False` 或者没有本地 `Data`，否则录不到流式访问。
2. `Mir2Test.ini` 里把 `[Streaming] RecordWorkingSet` 改为 `True`。
3. 正常启动，走完登录、创建/选择角色、进入出生地图，走动几步，然后**正常退出**（不要强杀进程，否则不会落盘）。
4. 得到 `Cache\AssetsV3\workset-usage.txt`。多次录制会自动合并累加，可以分几次补上创角、第二张地图等场景。
5. 录完把 `RecordWorkingSet` 改回 `False`，否则每次退出都会继续往这个文件里累加。

发布：把这个文件复制到输出目录（构建会自动发现），或用 `--usage` 指定路径：

```powershell
copy .\Build\Client\Debug\Cache\AssetsV3\workset-usage.txt E:\GameSourceCode\StreamingAssetsV3\

dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3" `
  --usage "E:\GameSourceCode\StreamingAssetsV3\workset-usage.txt" `
  --workset-max-mb 64 `
  --workset-max-image-kb 256
```

成功时会打印：

```text
Working set: 1092 image(s) from 28 library(ies), 3.6 MB.
Working set: 20 image(s) over 256 KB left to ranged requests (40.8 MB).
```

两个上限的含义：

- `--workset-max-image-kb`（默认 256）：单张超过这个大小的图不进包。兆字节级的大图自身传输
  比它省下的一次往返贵得多，打包只会让每个冷客户端把可能根本不显示的立绘一起下载。本机首次
  真实录制 1112 张图，其中 `chrsel` 的 20 张登录/选角大图就占 40.8 MB；设上限后包体只有
  3.6 MB，仍覆盖 1092 张，被排除的 20 张按老路走 Range。
- `--workset-max-mb`（默认 64）：整包上限，超出按库顺序截断并提示。包体接近这个值通常说明
  录制跑得太久，录进了本不属于「首次进入」的内容。

录制里已经不存在的库或图片会被跳过并在同一行后面报出数量。

客户端行为：启动阶段与索引预取并发地拉一次工作集包，写进容器后即结束，之后由 `workset.applied` 标记避免重复下载。工作集缺失、损坏或服务端 404 都不影响启动，只是回到逐张 Range 的老路。资源重新发布后某个库的 `FileHash` 变化，该库的旧工作集条目会被整体跳过，不会把新 Lib 的字节写到旧偏移上。

日志里出现 `Streaming working set applied: N image(s).` 表示这次冷启动用上了工作集。

## 7. 首次进入错位问题

旧微端曾出现第一次登录、选角和进入游戏时 UI 错位，重启后正常。原因是控件创建时图片尺寸元数据尚未下载，首次布局使用了零尺寸或错误尺寸。

V3 的处理方式是：

1. 启动 Scene 前并发预取 `ChrSel`、`Prguse`、`Prguse2`、`Prguse3`、`XiayiUi`、`UI_32bit` 和 `Title` 的索引。
2. 索引保存每张图片的画布尺寸、偏移和按 Alpha 扫描得到的 `TrueWidth/TrueHeight`。
3. 布局只依赖索引，不等待图片像素下载；图片像素稍后到达只触发重绘，不重新改变控件位置。

发布前应使用一个没有 `Data` 且没有 `Cache\AssetsV3` 的干净微端，完整走一遍登录、创建/选择角色和进入地图。若启动时报“无法加载启动界面资源索引”，先检查 `/health`、客户端 URL 和上述七个 Lib 是否包含在 V3 清单中，不要靠重启掩盖问题。

## 8. 自检与排错

运行构建器内置自测，不会读取正式 8GB 资源：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- self-test-v3
```

客户端侧的本地缓存自测（稀疏容器、位图跨重启、非正常退出后的结构校验、LRU 淘汰、工作集解包、下载优先通道），同样不需要资源服：

```powershell
.\Build\Client\Debug\Client.exe --asset-cache-self-test
```

如果正式构建失败但终端中的错误已经找不到，可以执行只读诊断。它使用与正式构建相同的解析器检查所有源资源，但不会复制资源、生成 Map Pack 或修改 `StreamingAssetsV3`：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- diagnose-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound"
```

正式构建失败时还会自动写入：

```text
E:\GameSourceCode\StreamingAssetsV3\assetbuilder-v3-failures.log
```

修复失败资源后保留整个输出目录，重新执行正常构建命令。不要为了重试而删除输出目录或添加 `--full`。

常见问题：

| 现象 | 检查 |
| --- | --- |
| `directory not found` | 四个命令行目录是否真实存在 |
| 地图构建失败 | 错误会给出地图文件、格式类型和偏移，检查该 `.map` 是否截断 |
| `/health` 为 `ok=false` | `AssetRoot` 是否指向正确 V3 输出、构建是否 `Failed: 0` |
| HTTP 返回 416 | Lib、Catalog 必须使用 Range 请求；浏览器直接整文件访问返回 416 是正常保护。Map Pack、声音和工作集包是整体 GET，返回 200 |
| 完整客户端仍显示旧图片 | `PreferLocalAssets=True` 时本地 Lib 优先，更新本地 Lib 或关闭本地优先 |
| Cache 超过预期 | 检查 `CacheMaxMB`，客户端按最久未使用顺序淘汰整库容器并清理 blob；稀疏容器占用的是已下载字节，不是 Lib 全长 |
| 工作集没生效 | 日志有没有 `Streaming working set applied`；清单里 `workingSetHash` 是否为空；容器里是否已经有这些图片（此时无需再写） |

不要把旧 `StreamingAssets` 或 `StreamingAssetsV2` 与 V3 输出混用。V3 客户端、V3 AssetServer 和 V3 `manifest.json` 必须配套使用。
