# 微端流式资源 V3 实现交接文档

> 本文档用于直接交给其他 AI 或开发者实施。开始编码前请完整阅读，尤其是
> “冷缓存首次启动 UI 错位”章节。该问题曾经实际出现，不能只通过重绘或占位图规避。

## 1. 项目与任务背景

项目根目录：

```text
E:\GameSourceCode\YangfeiCrystal
```

完整客户端目录：

```text
E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug
```

资源源目录目前已经拆分为：

```text
E:\GameSourceCode\CrystalMirFullAssets\Data_Full\**\*.Lib
Build\Client\Debug\Map\*.map
Build\Client\Debug\Sound\**\*.wav
Build\Client\Debug\Sound\**\*.mp3
Build\Client\Debug\Sound\SoundList.lst
```

`Data_Full` 已经不在客户端 `Debug` 目录内。`Debug` 目录还包含 `Client.exe`、DLL、
配置和登录器文件，它不是纯资源目录。AssetBuilder V3 必须允许分别设置
`LibraryRoot`、`MapRoot` 和 `SoundRoot`，绝对不能把整个 `Debug` 目录当作资源逐文件发布。

业务目标：

1. 轻量客户端无需完整下载 `Data/Map/Sound` 即可登录并进入游戏。
2. 图片、地图和声音只在实际使用时下载。
3. 当前地图只加载角色视野附近的 `32x32` 地图区块并预取邻近区块。
4. 完整客户端有本地资源时优先使用本地文件，不依赖资源服务器。
5. AssetServer 故障不得影响游戏服务器，也不得让完整客户端无法游戏。
6. 更新资源后客户端自动按版本和哈希刷新，不要求用户删除 `Data` 或 `Cache`。
7. 服务端和客户端缓存中不能再产生数十万/数百万个小文件。
8. 日常只处理新增或修改的资源，不允许每次重新扫描、拆分或写出全部 8 GB 资源。

## 2. 当前代码基线

> 本节记录的是 V3 开工之前的 V2 基线，仅用于理解当初的问题来源。V1/V2 协议、路由和对象小
> 文件缓存都已删除，仓库里只剩一套 V3 实现。

仓库目前已有 V2 实现，关键入口如下：

| 模块 | 文件 | 当前职责 |
| --- | --- | --- |
| 协议模型 | `Shared/StreamingAssets/StreamingAssetModels.cs` | V2 根清单、图库页、地图块模型 |
| 二进制编码 | `Shared/StreamingAssets/StreamingAssetIO.cs` | SHA-256、图库页、图片块和地图块编码 |
| 构建工具 | `Tools/AssetBuilder/Program.cs` | 把 Lib 图片、地图块、声音拆成内容寻址小对象 |
| 客户端资源管理 | `Client/Streaming/AssetManager.cs` | 下载、校验、重试、小文件缓存和 LRU |
| 客户端图库 | `Client/MirGraphics/MLibrary.cs` | 本地 Lib 与 V2 流式图片双加载路径 |
| 客户端地图 | `Client/Streaming/StreamingMapState.cs` | 视野区块下载、占位 Cell 和刷新 |
| 地图接入 | `Client/MirScenes/GameScene.cs` 的 `MapControl` | 本地 map/流式 map 选择 |
| 声音接入 | `Client/MirSounds/*` | 本地声音优先、缺失时流式下载 |
| 资源服务器 | `AssetServer/Program.cs` | `/assets/v2/` 对象与 batch 接口 |
| 客户端配置 | `Client/Settings.cs` | `[Streaming]` 配置 |

V2 当前使用：

```text
StreamingAssetsV2/
  manifest.json
  objects/{hash前两位}/{sha256}.bin
```

V2 的优点是对象级去重和更新粒度小；主要问题是产生海量小文件，导致 NTFS
遍历、压缩、复制、杀毒扫描、部署和客户端 Cache 清理都非常慢。V2 的
`--adopt-existing` 深度校验还会逐个验证图库页面和图片对象，在真实资源上可能运行
数小时。

本任务允许不兼容 V1/V2，也没有线上玩家需要迁移旧缓存。实现 V3 时应删除无用的
V1/V2 兼容分支，不要继续背负旧协议。

## 3. 已经遇到过的问题

以下不是假设，而是开发过程中已经出现过的问题。V3 验收时必须逐项回归。

### 3.1 冷缓存首次启动 UI 和素材错位

复现步骤：

1. 删除客户端 `Data` 中的大资源和整个 `Cache`。
2. 第一次启动微端。
3. 登录窗口、创建/选择角色界面和游戏主界面出现控件偏移、大小错误或素材错位。
4. 退出后第二次启动，因为资源已经缓存，界面恢复正常。

根因：

- `Client/MirControls/MirImageControl.cs` 的 `Size`/`TrueSize` 会在控件构造和布局阶段
  调用 `Library.GetTrueSize(Index)`。
- 本地 Lib 路径会立即读取图片并计算真实可见区域。
- 冷缓存流式路径当时还没有图库索引页或图片像素，`MLibrary.GetTrueSize()` 返回
  `Size.Empty` 或用画布 `Width/Height` 临时代替。
- 很多界面使用父控件尺寸、居中位置或前一个控件位置进行一次性布局。布局完成后，
  下载完成事件只触发 `Redraw()`，不会重新执行所有构造函数和布局计算。
- `MImage.GetTrueSize()` 不是简单返回画布宽高。它会解压图片并扫描 Alpha，计算非透明
  像素包围盒的宽高，因此用 `Width/Height` 代替也可能产生偏移。

强制解决方式：

1. V3 图库索引必须为每张图片保存：`Width`、`Height`、`X`、`Y`、
   `TrueWidth`、`TrueHeight`、是否存在、Lib 内偏移、记录长度和内容 SHA-256。
2. AssetBuilder 生成索引时必须解压图片并按本地 `MImage.GetTrueSize()` 相同算法计算
   `TrueWidth/TrueHeight`。算法结果必须与完整客户端逐图一致。
3. 启动 UI 使用的图库元数据必须在创建登录 Scene 之前同步准备完成。至少包括：
   `ChrSel`、`Prguse`、`Prguse2`、`Prguse3`、`XiayiUi`、`UI_32bit`、`Title`。
4. `GetSize()`、`GetOffSet()`、`GetTrueSize()` 只能依赖轻量索引，不能依赖图片像素是否
   已下载。对于索引中存在的图片，禁止在正常网络下返回临时 `Size.Empty`。
5. 图片像素可以稍后异步下载。像素未到达时可以透明占位，但布局尺寸必须从第一帧起
   就正确。
6. 下载完成后的通知必须切回游戏主线程，只设置重绘/重试标记。禁止后台下载线程直接
   操作 SlimDX、Scene 控件树或纹理。
7. 不接受“下载完以后重新打开窗口”“第二次启动正常”作为修复。

应增加自动化对照测试：对启动图库中的所有图片，同时使用本地 Lib 解析器和 V3 索引
解析器，断言六项尺寸/偏移元数据完全相同。还要在无像素缓存的条件下构造登录、选角
和主界面，断言关键控件位置与完整客户端一致。

### 3.2 首次启动紫屏或黑屏

原因曾包括启动图库没有被构建、资源根目录选错、根清单或图库元数据未就绪时 Scene
已经开始绘制。V3 必须设置“启动必需元数据”门槛：元数据成功后才创建登录 Scene；
像素可并行下载。资源服不可用时显示稳定的加载/重试状态，不能进入一个永久黑屏 Scene。

### 3.3 选角点击 Start 后永久 Loading

进入游戏需要地图清单和角色出生点附近首批区块。V3 要有明确状态机：

```text
WaitingMapMetadata -> WaitingSpawnChunks -> Ready -> BackgroundPrefetch
```

不得在 UI 线程做无超时的 `.GetAwaiter().GetResult()`。初始地图元数据和出生区块请求必须
有超时、重试和错误状态；资源服恢复后可继续。完整客户端本地 map 存在时直接进入本地
路径，不经过这个状态机。

### 3.4 AssetBuilder 解析单张地图异常导致整次构建崩溃

曾有 map 格式与预期不符，触发 `BitConverter.ToInt32` 越界。所有读取必须先检查剩余字节，
错误中包含资源文件路径、地图类型和读取位置。单项失败要被统计，构建最后返回非零；根
清单只能在所有资源成功后原子替换，不能发布半成品。

### 3.5 构建没有可见进度且重复处理 8 GB

每个阶段必须显示：资源数量、当前文件、当前文件内部进度、已用时间、预计剩余时间、
重建/复用/失败数。没有变化的第二次构建不得读取每张图片内容或验证所有输出对象。

默认增量判断使用：规范化相对路径、文件长度、UTC 修改时间。只有元数据发生变化的源
文件才读取并计算 SHA-256。提供显式 `--verify` 做全内容校验，但不能把它放进日常流程。
不要再设计需要运行数小时的 `--adopt-existing`。

### 3.6 Cache 和 Data 同时增长、清单文件巨大

V3 流式资源只能写入专用缓存，不能把下载内容拼回 `Data/*.Lib` 或 `Map/*.map`。
`Settings.Load()` 可以创建空目录，但流式模式下 `Data` 不应因下载资源而增长。

V2 曾为图库产生几 MB 到几十 MB 的 JSON manifest，并把大量对象作为独立缓存文件。
V3 必须使用二进制索引和少量缓存容器，禁止每个图片一个缓存文件、每个版本复制一份
manifest，禁止让 `Cache` 因元数据重复而大于实际资源。

### 3.7 本地旧 Lib 掩盖资源服新版本

当 `PreferLocalAssets=True` 且本地 `Data/Title.Lib` 存在时，本地文件必须赢，这是完整
客户端故障隔离的要求。因此看到旧素材不代表资源服没更新。V3 配置和日志必须明确记录
每个资源最终来源是 `local` 还是 `streaming`。

纯微端测试应移走本地大资源，或设置 `PreferLocalAssets=False`。资源更新依靠清单和哈希
自动刷新，不能要求删除 Data/Cache。

## 4. V3 最终架构

采用“原生 Lib + 地图区块 Pack + 原始声音 + 单文件客户端缓存”的混合方案。

### 4.1 图库直接使用原始 Lib

原始 `.Lib` 已经是图片容器，格式为：

```text
int32 version
int32 imageCount
[version >= 3] int32 frameSeek
int32 imageOffsets[imageCount]
image records...
frame records...
```

当前本地加载代码本身就是通过 `_indexList[index]` 跳到图片偏移。因此不应再把每张图片
拆成服务器小文件。AssetBuilder 只需要生成二进制 sidecar 索引，客户端通过 HTTP Range
读取对应 Lib 区间。

当前图库源目录为：

```text
E:\GameSourceCode\CrystalMirFullAssets\Data_Full
```

这里的“直接使用 Lib”是指保留 Lib 原始容器格式，不再拆成图片小文件。它不表示微端
把不完整 Lib 写进客户端 `Data`，也不表示微端可以完全取消持久缓存：

- 完整客户端的 `Data/**/*.Lib` 存在且有效时，客户端直接本地读取，不需要为这些图片写
  流式 Cache。
- 轻量客户端没有完整 Lib 时，从 AssetServer 对 Lib 发起 Range 请求，并把取得的单张图片
  记录写入 `Cache/AssetsV3/libs` 下的稀疏容器（见 6.4），否则每次启动都会重复下载。
- 稀疏回填只发生在 `Cache` 里的 `.libpart`，不会在 `Data/*.Lib` 内原地填充半个 Lib：只有
  完整且验证通过的文件才能使用 `.Lib` 后缀放进 `Data`。`MLibrary.HasUsableLocalFile()`
  必须按结构校验，不能只看存在和最小长度。
- Data 是用户预装的完整资源；Cache 是微端自动管理、可删除并可重新下载的按需数据。

每个图片索引记录至少包含：

```text
Index             int32
Exists            byte
Offset            int64
RecordLength      int32
Width/Height      int16 + int16
X/Y               int16 + int16
ShadowX/ShadowY   int16 + int16
Shadow            byte
TrueWidth/Height  int16 + int16
ContentHash       32 raw bytes (SHA-256)
```

`RecordLength` 必须覆盖图片头、主图压缩数据以及可选 Mask 的头和压缩数据，使客户端一个
Range 请求即可取得完整图片。图片数据在 Lib 内已经按层 GZip 压缩，Pack 或 HTTP 层不要
再次整体压缩。

注意检查并修复本地 `MImage.CreateTexture(BinaryReader)` 的 Mask 读取：Mask 像素必须使用
`MaskLength`，不能误用主图 `Length`。V3 远程解析器和本地解析器应共享同一个经过边界检查
的图片记录解析函数，避免两套格式逻辑漂移。

### 4.2 地图使用每张地图一个 Map Pack，整包一次拉取

原始 `.map` 中一个 `32x32` 矩形不是一个连续字节区间，不适合客户端直接做单次 Range。
AssetBuilder 对发生变化的 map 解析一次，生成一个不可变文件：

```text
maps/{mapContentHash}.mappack
```

Map Pack 是自描述的 `YMP3` 结构，客户端不需要任何外部索引即可解析：

```text
[magic "YMP3"][formatVersion=3][width][height][chunkSize][chunkCount]   // 24 字节头
[chunkCount × (compressedLength int32, uncompressedLength int32)]        // 区块目录
[各区块压缩载荷，按目录顺序紧邻排列]
```

区块顺序为列优先：第 `i` 块覆盖 `X = i / rows * chunkSize`、`Y = i % rows * chunkSize`。
每个载荷是单独 GZip 的「20 字节区块头 + 每格 32 字节」数据；Pack 整体不再压缩，因此仍可
被 Range 读取，只是客户端默认不需要。

**整包一次拉取**：实测 468 张地图共 348 个唯一 Pack、24.2 MB，最大一张 953,943 字节
（`n0`：700×700，484 块，未压缩 15.7 MB，压缩比 17.1×）。一张地图一次 GET 的成本低于
按块多次 Range 的往返与调度开销，所以客户端进入地图时一次拉整包，落进 blob 缓存，
再按视野解压区块。因此 Catalog 中不再存在地图索引块（原来这部分占 1,631,456 字节）。

一张 map 更新只重建这一张 map 的 Pack，不重建其他地图。

### 4.3 声音直接使用原文件

声音已经是独立 `.wav/.mp3`，不需要二次拆包。清单记录路径、长度和 SHA-256；首次播放
时下载完整声音并写入统一客户端缓存。`SoundList.lst` 同样作为普通资源管理。

### 4.4 发布目录建议

```text
StreamingAssetsV3/
  manifest.json
  catalogs/
    {catalogHash}.bin
  libraries/
    {wholeLibHash}.lib
  maps/
    {mapHash}.mappack
  sounds/
    {soundHash}.wav
    {soundHash}.mp3
  worksets/
    {worksetHash}.wsp
```

这是几千个中大文件，不再是几十万小文件。发布文件以内容哈希命名并保持不可变；根清单
最后原子替换。为保证不可变性，不能对仍可能被编辑器原地修改的源 Lib 使用硬链接。
同卷复制可以提供显式 `--hardlink-immutable-source`，但默认应复制或使用原子快照。

如果部署环境决定由 AssetServer 直接读取 `Data_Full`，则请求必须携带 Lib 版本哈希，
AssetServer 在文件长度或修改时间与已发布清单不符时返回 `412/503`，绝不能把新 Lib 的
字节按旧索引返回。内容哈希命名副本更安全，应作为默认实现。

### 4.5 二进制 Catalog

根 `manifest.json` 只保存资源 ID、版本、文件位置、长度、哈希，以及资源在二进制 Catalog
中的 offset/length。不要把数百万图片记录放进 JSON。

`catalogs/{catalogHash}.bin` 只包含图库索引块（地图 Pack 自描述，不再有地图索引块）。根
清单记录每个块的 `CatalogOffset`、`CatalogLength`，以及块头长度 `CatalogHeaderLength` 和
块头 SHA-256 `CatalogHeaderHash`，客户端只 Range 读取正在使用的索引块。

索引块本身分段（magic `YLI4`）：未压缩的块头 + 若干 Brotli 压缩的分段载荷。

```text
块头：magic, formatVersion, id, imageCount, segmentSize(4096), segmentCount, frameCount,
      segmentCount × (compressedLength int32, uncompressedLength int32, SHA-256 32 字节),
      frameCount × 35 字节帧记录
载荷：segmentCount 个 Brotli 段，每段 segmentSize 条 25 字节图片记录
```

单条图片记录固定 25 字节，不再逐图存 SHA-256：

```text
[uint32 Offset][uint32 Length][int16 Width][int16 Height][int16 X][int16 Y]
[int16 ShadowX][int16 ShadowY][byte Shadow][int16 TrueWidth][int16 TrueHeight]
```

`Index` 由段内位置隐含，`Offset=0 && Length=0` 表示该图不存在（解码为 `Offset=-1`）。
去掉的逐图哈希由结构校验替代：`StreamingAssetV3IO.IsLibraryImageRecordValid` 要求下载
回来的字节恰好解析成一条 Lib 图片记录，且 Width/Height/X/Y/ShadowX/ShadowY/Shadow 与
索引元数据完全一致。每个块头和每个分段都有独立 SHA-256，因此任何一次独立取回的片段
都可校验、都可进 blob 缓存。

分段的收益是客户端只需拉正在用的那一段：92% 的图库只有一段，最大的
`map/wemademir2/tiles`（155,305 张图）有 38 段。实测 Catalog 从 141,551,988 B 降到
16,464,917 B；七个启动图库合计只需拉约 45 KB。

所有二进制结构必须有：

- 4 字节 magic，例如 `YAC3`、`YLI4`、`YMP3`。
- 明确的 `formatVersion=3`。
- 数量和长度上限检查。
- little-endian 定义。
- 解析结束位置检查，拒绝截断和尾随垃圾。
- 对应的 round-trip、自损坏和越界单元测试。

### 4.6 首启工作集包（已完成）

冷启动最贵的不是单张图片，而是「登录界面 → 选角 → 第一张地图」这段时间里成百次零散的
Range 往返。整库预取已被实测排除：七个启动图库的像素合计 88.96 MB。工作集包只发布这段
时间**真实触达过**的图片记录，一次整文件 GET 取回。

录制在客户端完成（`Client/Streaming/WorkingSetRecorder.cs`）：`[Streaming]
RecordWorkingSet=True` 时，绘制路径上每次访问都记下 `(libraryId, imageIndex)`，命中和未命中
都记，因此可以对着热缓存录制。录制到 48 MB 记录后自行停止，退出时（`AssetManager.Shutdown`）
把结果合并写入 `<CachePath>/workset-usage.txt`：

```text
# 一行一个 "libraryId<TAB>imageIndex"，# 注释和空行忽略
chrsel	0
map/wemademir2/tiles	1837
```

合并而不是覆盖，因此登录、创角、进入第二张地图可以分几次录制累加。把这个文件交给
AssetBuilder（`--usage <file>`，或直接放在发布目录下由构建自动发现）即可发布工作集包：

```text
包头：magic `YWS3`, formatVersion, libraryCount
每库：id, fileHash, entryCount, entryCount × (int32 index, int64 offset, int32 length)
载荷：int64 payloadLength + 依次拼接的原始 Lib 图片记录
```

要点：

- 载荷字节取自**已发布**的 `libraries/{fileHash}.lib`，偏移取自本次构建的 Catalog 块，因此
  复用未变化的 Lib 也能生成工作集。
- 每库嵌入 `fileHash`：客户端发现该库已重新发布就整库跳过这些条目，绝不会把新 Lib 的字节
  按旧偏移写回。
- 真实性由根清单里工作集整文件的 SHA-256 保证，逐条只做结构校验。
- **单图上限 `--workset-max-image-kb`（默认 256 KB）**：兆字节级的大图自身传输远大于它能省下
  的一次往返，打包进去只会强迫每个冷客户端把可能根本不显示的立绘一起下载。首次真实录制
  1112 张里，仅 `chrsel` 的 20 张登录/选角大图就占 40.8 MB；设上限后包体 3.6 MB / 1092 张，
  被排除的 20 张仍走正常 Range。
- 总上限 `--workset-max-mb`（默认 64 MB），超出即截断并打印提示；录制中已不存在的库/图片会
  被跳过并计数。
- 客户端只读一次，**不进 blob 缓存**；`blobs` 元数据里的 `workset.applied` 记录已应用过的
  工作集哈希，热客户端不会重复下载。即使一条都没写入也会写标记，重复下载比少量 Range 更贵。

实测（本机一次真实冷启动录制：登录 → 选角 → 出生地图走动几步）：29 个图库 1112 张图，
包体 3.6 MB，覆盖 1092 张；发布后一次 GET 取代原来这段时间上千次零散 Range。

## 5. V3 HTTP 接口

建议统一前缀：

```text
/assets/v3/
```

接口：

```text
GET /assets/v3/manifest.json
GET /assets/v3/catalogs/{hash}.bin
GET /assets/v3/libraries/{hash}.lib
GET /assets/v3/libraries/{hash}.lib?segments=offset-length,offset-length,…
GET /assets/v3/maps/{hash}.mappack
GET /assets/v3/sounds/{hash}.{ext}
GET /assets/v3/worksets/{hash}.wsp
GET /health
```

要求：

1. Catalog、Lib 必须支持标准 HTTP Range，正确返回 `206`、`Content-Range`、
   `Accept-Ranges: bytes`；这两类资源缺少 `Range` 头时返回 `416`。Map Pack、声音和工作集包
   是整体获取的对象，普通 GET 必须正常返回 `200`，不能因为没有 `Range` 就返回 `416`。
2. `?segments=` 是一次取多条分散图片记录的批量读：至多 64 段、载荷至多 4 MB，响应体是
   请求顺序的 `[int64 offset][int32 length][bytes]` 序列，一次往返代替 N 次 Range。越界、
   超段数、超载荷或格式错误一律 `400`。
3. 哈希资源响应：`Cache-Control: public,max-age=31536000,immutable`，`ETag` 为内容哈希。
4. 根清单：`Cache-Control: no-cache`，支持 `If-None-Match` 和 `304`。
5. Range 越界返回 `416`。
6. 只接受严格的十六进制 SHA-256 文件名和允许的扩展名，禁止 `..`、绝对路径、编码后的
   路径穿越和任意文件读取。
7. 使用 Kestrel `Results.File(..., enableRangeProcessing: true)` 或等价的流式发送，禁止
   `File.ReadAllBytes` 读取大型 Lib/Pack。
8. AssetServer 仍是独立项目，默认 `http://0.0.0.0:8088`。不要把接口加进
   `Server/Utils/HttpServer.cs`，不要占用游戏服 TCP 7000 或原 HTTP 5679。

### 5.1 服务端实现（已完成）

首次进入游戏是一串针对少数几个大 Lib 的密集小 Range 请求，请求本身的固定开销比读盘更贵，
因此服务端按以下三点实现，不要退回到"每个请求打开文件、读进数组再返回"的写法：

1. `AssetServer/ManifestCache.cs`：根清单常驻内存，只在文件的写入时间或长度变化时重读并
   重算 ETag。此前每个请求都要读 930 KB JSON 再算一次 SHA-256。
2. `AssetServer/PublishedFiles.cs`：已发布文件按内容哈希命名、永不变更，因此句柄可以池化
   （上限 256，LRU 淘汰）。句柄用 `FileShare.Read | FileShare.Delete` 打开，`--prune` 和
   重新发布仍可以在服务运行时删除旧文件（已实测：句柄在池中时删除成功）。
3. `AssetServer/RangeResponses.cs`：Range 与 `?segments=` 都从池化句柄经 64 KB
   `ArrayPool` 缓冲直接写响应体，不再为一次 4 MB 的批量读分配大对象堆数组。`?segments=`
   会先写出 `Content-Length`（`12 × 段数 + Σ长度`）。多段 Range（`bytes=a-b,c-d`）一律
   返回 `416`，不实现 multipart 响应。

实测（本机，8 并发 × 150 次随机 Range，覆盖 1440 个 Lib，池上限被反复触发）：
1956 req/s，p50 3.9 ms、p99 5.8 ms、0 失败。

## 6. 客户端实现要求

### 6.1 本地资源优先级

统一决策：

```text
PreferLocalAssets=True 且本地文件有效 -> local
否则 Streaming.Enabled=True 且清单有记录 -> streaming
否则 -> missing placeholder / retry
```

本地文件读取失败时可以回退流式资源。每次决定应写一次低频诊断日志，便于确认客户端
到底使用了哪个来源。

必须加强当前 `MLibrary.HasUsableLocalFile()`。禁止继续只判断 `Exists && Length >= 8`。
最低限度要校验 Lib version、imageCount、索引表是否完整、所有非零图片 offset 是否位于
文件范围内，以及 V3 manifest 可用时的文件长度/版本。若以后提供“下载完整 Lib”功能，
下载期间使用 `.partial` 后缀，全部长度和 SHA-256 验证通过后再原子改名为 `.Lib`。

### 6.2 启动顺序

正确顺序必须是：

```text
Settings.Load
AssetManager.Initialize
加载并验证根 manifest（网络失败可用最近有效缓存）
加载启动图库的 Catalog 索引块
确认所有布局元数据可查询
创建 Libraries 和 LoginScene
异步加载实际图片像素并逐帧刷新
```

不要在图库静态构造函数内部发起一串不可控的同步 HTTP 请求。已实现显式
`Client/Streaming/StartupAssetBootstrapper.cs`：`AssetManager.Initialize` 末尾调用
`Begin()` 启动一个异步阶段，并发取回根 manifest、七个启动图库的全部索引分段，以及
（尽力而为的）`soundlist.lst`；`Program.Main` 在 `Settings.Load` 之后只做一次有超时的
`Wait(...)`，失败时把 `Status` 写进日志并继续启动，由后台重试补齐。

同一阶段里还会**并发**发起一次首启工作集应用（`AssetManager.ApplyWorkingSetAsync`）。它是
像素而不是布局元数据，所以绝不参与 `StartupMetadataReady`：清单里没有工作集、下载失败或
只应用了一部分都不影响登录界面，只是多几次后续 Range。放在索引取回之前发起，是为了让这
一次整文件 GET 与索引请求重叠而不是排在它们后面。

因此 `AssetManager` 里不再有任何 UI 线程上的同步等待：`Manifest` getter 在未就绪时只发起
后台请求并返回 `null`；`GetSoundBytes` 只读缓存，未命中时排队下载；只有
`WaitForLibraryIndex(id, timeout)` 会等待，且只由启动图库的阻塞初始化调用，此时
bootstrapper 通常已经预取完毕，等待立即返回。`SoundManager` 的静态构造函数早于预取完成，
所以 `OnAssetsUpdated` 会在 `SoundList.Indexes` 仍为空时重新加载一次声音列表。

### 6.3 Range 下载

图库图片请求必须带 `Range: bytes=start-end`，严格校验：

- HTTP 状态必须为 `206`。
- `Content-Range` 起止与请求一致。
- 返回长度等于索引 `Length`。
- 图片记录解析不越界。
- 结构校验通过：`IsLibraryImageRecordValid`（记录整体长度、单条记录解析、以及与索引元数据
  的尺寸/偏移一致性）。索引里已不存在逐图 SHA-256。

同一资源并发请求需要 single-flight 去重。相邻区间应在下载调度器中合并：间隔 ≤16 KB 的
记录合并成一次 Range，单次合并上限 4 MB；分散记录用 `?segments=` 一次取回，上限 64 段
/4 MB。合并的目的是减少往返，不是下载整个大 Lib——超出上限就拆成多批。

地图 Pack 与声音是整体对象，用普通 GET 一次取回并写入 blob 缓存，不做分块 Range。首启
工作集包同样是整体 GET，但只读一次、不进 blob 缓存（见 4.6）。

### 6.4 客户端缓存

禁止继续使用：

```text
Cache/Assets/objects/ab/abcdef....bin
```

也不要使用单一 SQLite 数据库（`Cache/AssetsV3.db`）。渲染线程每帧都要判断「这张图在本地
吗」，SQLite 的全局锁会与下载线程的写入争用，这正是首次进入游戏卡顿的主因。V3 采用两层
本地布局：

```text
Cache/AssetsV3/
  libs/{libraryId}.libpart      # 稀疏文件，长度等于已发布 Lib
  libs/{libraryId}.bits         # 位图边车：每张图 1 bit + 72 字节头
  blobs/ab/abcdef….bin          # Catalog 块、Map Pack、声音
```

图片：`.libpart` 是与已发布 `.Lib` 等长的 NTFS 稀疏文件（`FSCTL_SET_SPARSE` +
`SetLength`），下载到的图片记录按其**原始 Lib 偏移**写回原位。因此：

- 「是否已缓存」是内存位图的一次 bit 读取，渲染线程不加锁、不查库、不算哈希。
- 命中就是一次 `RandomAccess.Read`，读出的字节即原始 Lib 记录，可直接解析建纹理。
- 写入顺序是「写载荷 → `FlushToDisk` → 置位」，崩溃不可能暴露半条记录。
- `.bits` 头（72 字节，magic `YLB4`）依次是 magic、FormatVersion、ImageCount、FileLength、
  32 字节 Lib `FileHash`（偏移 20）、已存字节数（偏移 52）、干净关闭标志（偏移 60）、
  最后使用时间 UTC ticks（偏移 64）。发布的 Lib 一变，容器整体重置；上次非正常退出则每条
  记录首次读取时按结构校验一次再信任。
- 退出时必须走 `AssetManager.Shutdown()`，否则下次启动全部记录都要重新校验。

整体对象（Catalog 块、Map Pack、声音）：一个不可变内容寻址文件一个 blob，读取时校验长度
和 SHA-256，命中后由调用方常驻内存。

要求：

- 位图和 blob 写入都在后台线程，渲染线程只做内存位图判断和一次定位读。
- 单库容器的粒度决定淘汰粒度：超过 `CacheMaxMB` 时按「blob 预算 = max(64 MB, 1/8 上限)」
  先 LRU 清 blob，剩余预算不足再整库淘汰，启动图库（UI/选角等）永不淘汰。
- 容量统计必须包含**本次运行没有打开**的容器：`AssetCacheStore.CollectEntries` 会扫描
  `libs/**/*.bits` 并读取头部的已存字节数，否则刚启动时预算等于形同虚设（此前只统计了
  内存里已打开的容器）。无法解析的边车、缺少 `.libpart` 的边车在扫描时直接删除。
- 淘汰顺序必须是**最久未使用优先**，不能按容器大小从大到小：最大的容器往往正是玩家当前
  所在地图的 tiles 库，删掉立刻要重下。最后使用时间由 `LibraryCacheContainer.Touch()`
  维护，最多每 5 分钟落盘一次。
- 淘汰不能只在应用清单时触发，否则一次长时间游戏永远不会清理。写入累计超过 256 MB 时由
  `AssetCacheStore.NoteGrowth` 在后台触发一次 `Trim()`。
- 容器或位图损坏、长度不符时直接重置该库，不影响本地完整客户端启动。
- 已下线的图库容器由 `RemoveOrphans` 清理，不依赖用户手工删目录。
- 清理在后台执行，不能阻塞渲染线程。
- `Client.exe --asset-cache-self-test` 覆盖上述行为：稀疏读写、位图跨重启、发布版本变更
  重置、磁盘上未打开容器计入预算、LRU 淘汰顺序、启动图库不被淘汰，以及首启工作集包的解包
  （合法记录落位、畸形记录被拒、已存在的记录不重写）。

流式缓存不得写入 `Data`、`Map`、`Sound`。

### 6.5 下载失败与线程模型

- 指数退避建议：1、2、4、8、16、30 秒封顶。
- 资源服不可用时客户端网络连接和游戏逻辑保持运行。
- 图片缺失时透明占位，地图缺失区块保持空 Cell，声音缺失时静音并稍后重试。
- 所有下载、哈希和数据库写入在后台线程。
- 后台线程只投递 `AssetReady` 消息；Scene、SlimDX 纹理、`Redraw()` 和地图 Cell 替换在
  游戏主线程消费。
- 退出时取消请求并有界等待，不允许进程因后台任务挂住。

## 7. AssetBuilder V3 要求

建议命令。图库、地图、声音必须分别指定，因为 `Data_Full` 已移出客户端目录：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- build-v3 `
  --library-root "E:\GameSourceCode\CrystalMirFullAssets\Data_Full" `
  --map-root "E:\GameSourceCode\CrystalMirFullAssets\Map" `
  --sound-root "E:\GameSourceCode\CrystalMirFullAssets\Sound" `
  --output "E:\GameSourceCode\StreamingAssetsV3"
```

只读取 `LibraryRoot/**/*.Lib`、`MapRoot/*.map` 和上述 Sound 文件。不要自行回退或混用
`Build\Client\Debug\Data`；若操作者需要改用另一套图库，必须显式修改 `--library-root`。

状态文件只保存源资源级别记录，不保存每个输出小对象：

```text
kind, id, relativePath, length, lastWriteUtcTicks, sourceHash,
publishedHash, catalogBlockHash
```

增量规则：

1. 无变化 Lib：复用已发布 Lib 和 Catalog block，不打开图片数据区。
2. 变化 Lib：解析这一份 Lib、计算图片元数据/真实尺寸/图片哈希并发布新 Lib。
3. 无变化 map：复用原 mappack 和索引。
4. 变化 map：只重建这一张地图。
5. 变化声音：只复制/发布这一份声音。
6. 所有资源完成后才写临时 Catalog 和根清单，再原子 rename。
7. 任一失败时保留上一版根清单，进程返回非零。

必须有真实进度：

```text
[Libraries] 23/1440 Title.Lib images 1200/8431 14.2% elapsed 00:01:32 ETA 00:09:16
[Maps]      51/468  0.map chunks 12/88
[Sounds]    800/1608
Rebuilt: 2, Reused: 3514, Failed: 0
```

初次构建计算所有图片 `TrueSize` 会消耗 CPU，这是一次性成本。后续构建绝不能因工具版本
小改动自动丢弃状态；只有 `formatVersion` 或真实尺寸算法版本变化时才明确要求重建相关
索引，并提前输出原因。

提供命令：

```text
build-v3              默认增量构建
build-v3 --verify     全量读取源内容并核对哈希，不重建未变化资源
build-v3 --full       明确的全量重建
build-v3 --prune      删除当前清单不再引用的已发布文件
build-v3 --usage <f>  按录制文件发布首启工作集包（省略时自动发现 <output>\workset-usage.txt）
build-v3 --workset-max-mb <n>  工作集包上限，默认 64，允许 1~4096
build-v3 --workset-max-image-kb <n>  单图上限，默认 256，超过的图不进包、仍走 Range
self-test-v3          使用小型夹具完成协议、Lib、map、工作集、发布原子性测试
gc-v3 --dry-run       列出不再被当前/保留版本引用的哈希文件
gc-v3                 显式清理旧文件，不能在正常 build 中冒险删除
```

不要要求用户为了工具代码的一般修改再次转换全部资源。

## 8. 建议实施顺序

1. 在 `Shared/StreamingAssets` 定义 V3 模型、Catalog 二进制格式和严格解析测试。
2. 抽取一个共享的安全 Lib 记录解析器，修正 MaskLength，并加入截断/恶意长度测试。
3. 实现 AssetBuilder V3：先完成 Lib sidecar、TrueSize 对照测试和增量状态。
4. 实现每图 Map Pack、声音发布和根清单原子发布。
5. 修改 AssetServer 为 V3 哈希文件静态 Range 服务，加入路径穿越和 Range 集成测试。
6. 实现客户端缓存数据库和 Range 下载器。
7. 改造 `MLibrary`，让尺寸/偏移完全来自 V3 索引，像素按需下载。
8. 加入显式 StartupAssetBootstrapper，解决冷缓存 UI 布局问题。
9. 接入 StreamingMapState、声音和主线程资源就绪队列。
10. 删除 V1/V2 路由、自动 URL 升级、对象小文件缓存及不再使用的模型。
11. 执行完整冷缓存、故障隔离和更新测试。

不要一开始就删除 V2 代码。先让 V3 测试通过和客户端可运行，再做一次集中删除；最终代码
不能保留两套活动协议和大量 `if formatVersion` 分支。

## 9. 验收测试

### 9.1 构建与协议

- `dotnet build "Legend of Mir.sln"` 成功。
- `self-test-v3` 成功。
- 第二次无修改构建只 stat 源文件并快速复用，不读取 8 GB 内容。
- 修改一个 `Title.Lib` 只处理该 Lib；修改一张 map 只重建该 Map Pack。
- 单项损坏时构建失败，已发布 manifest 的哈希和时间不变。
- V3 发布目录文件数量控制在万级以下，不出现每图一个物理文件。

### 9.2 冷缓存 UI（最高优先级）

- 移走本地大资源并删除整个 Cache 数据库。
- 第一次启动登录界面位置与完整客户端一致，不紫屏、不黑屏、不偏移。
- 创建/选择角色界面第一次进入即正确。
- 第一次进入游戏主界面即正确，不需要重启。
- 在限速和 200 ms 网络延迟下重复以上测试，布局仍稳定，只允许图片像素逐步出现。
- 对启动图库执行本地/V3 的 Width、Height、X、Y、TrueWidth、TrueHeight 全量对照。
- 发布过工作集包之后重复一次冷启动：日志出现 `Streaming working set applied: N image(s).`，
  这段时间的 Range 请求数明显下降；删除 `workset.applied` 之外的缓存后仍可重现。
- 清单里没有工作集、工作集哈希损坏或服务端 404 时，冷启动行为与未发布工作集时完全一致。

### 9.3 地图和进入游戏

- 点击 Start 后拉取出生地图的整个 Pack 并进入游戏，不永久 Loading。
- 拉取期间空块占位，可见区块解压后立即出现；Pack 已在 blob 缓存时不再请求网络。
- 移动跨块后新块自动出现，旧 CellObjects 不因替换 CellInfo 丢失。
- AssetServer 中途关闭：客户端保持连接、空块占位；恢复后自动继续。

### 9.4 更新和缓存

- 更新 `Title.Lib`/`Prguse.Lib` 并发布后，不删除客户端任何目录也能自动显示新素材。
- 未变化的图片命中容器位图，不重复下载；发布的 Lib 变化后该库容器重置。
- Cache 只增长 `AssetsV3/libs` 稀疏容器和少量 blob，不写 Data。
- 超过 `CacheMaxMB` 后 blob LRU 与整库淘汰生效，容器损坏可自动重置。
- 正常退出后重启不触发全量 SHA-256 复检；强杀进程后仅首次读取各记录时校验一次。
- 日志能看出资源来自 local、streaming-network 或 streaming-cache。

### 9.5 完整客户端和故障隔离

- 保留完整 `Data/Map/Sound`，设置 `PreferLocalAssets=True`。
- 关闭 AssetServer 仍可登录、选角、进入地图、播放声音。
- 资源请求不进入游戏服务器；游戏服 CPU、网络和日志不受资源下载影响。

### 9.6 安全和 HTTP

- Range 正常返回精确字节和 `206`。
- Catalog/Lib 无 `Range` 时返回 `416`；Map Pack、声音和工作集包的普通 GET 返回 `200`，
  `ETag` 等于内容哈希，`If-None-Match` 命中返回 `304`。
- `?segments=` 越界、超 64 段、超 4 MB 载荷或格式错误返回 `400`。
- `../`、URL 编码穿越、错误哈希、错误扩展名、超大 Range 均被拒绝。
- 客户端拒绝长度、Content-Range 或 SHA-256 不匹配的数据。

## 10. 配置建议

AssetServer：

```json
{
  "Urls": "http://0.0.0.0:8088",
  "AssetRoot": "E:\\GameSourceCode\\StreamingAssetsV3",
  "CacheSeconds": 31536000
}
```

客户端：

```ini
[Streaming]
Enabled=True
AssetBaseUrl=http://127.0.0.1:8088/assets/v3/
PreferLocalAssets=True
ConcurrentDownloads=16
RequestTimeoutSeconds=30
CachePath=.\Cache\AssetsV3
CacheMaxMB=4096
RecordWorkingSet=False
```

`CachePath` 是目录（内含 `libs` 与 `blobs`），不是 `.db` 文件；配置里残留旧的
`AssetsV3.db` 会被客户端自动纠正为默认目录。`ConcurrentDownloads` 允许 1~32。

`RecordWorkingSet` 只给发布者用，默认关闭：打开后本次运行触达的图片会在退出时写入
`<CachePath>\workset-usage.txt`，供 `build-v3 --usage` 生成首启工作集包（见 4.6）。玩家开着
它没有任何收益。

V3 不需要自动把 `/assets/v1/` 或 `/assets/v2/` 改写为 V3。配置错误应清楚记录并失败，
避免客户端静默连接到与自身模型不匹配的协议。

## 11. 不要采用的实现

- 不要把每张图片、每个地图块作为单独服务器文件或客户端缓存文件。
- 不要要求客户端下载完整 Lib 后才能使用其中一张图片。Map Pack 是例外：单包只有几十 KB
  到 1 MB，整包一次 GET 比按块 Range 更快，允许并且推荐整包拉取。
- 不要在渲染线程上查数据库、加全局锁或算哈希来判断「这张图在不在本地」。
- 不要对整个 Lib/Pack 再做 ZIP/GZip，破坏随机 Range；只压缩内部独立记录。
- 不要在 UI 构造期间让尺寸查询依赖图片像素下载。
- 不要用 `Width/Height` 冒充 `TrueWidth/TrueHeight`。
- 不要从后台下载线程直接操作 Scene、SlimDX 或地图控件。
- 不要在资源服离线时阻塞游戏网络线程。
- 不要把资源 HTTP 路由加入游戏服务器。
- 不要日常执行全量哈希、全量深度验证或数小时的 adopt。
- 不要让资源刷新依赖用户手工删除 Data/Cache。
- 不要在构建失败时覆盖上一版 manifest。

## 12. 完成定义

只有当以下条件同时满足，任务才算完成：

1. V3 不再产生海量小文件，发布目录和客户端缓存可快速复制、备份和迁移。
2. 冷缓存第一次启动的登录、选角和主界面布局与完整客户端一致。
3. 图片按 Lib Range（或 `?segments=` 批量）下载，地图按整包下载，声音按需下载，首启阶段
   由工作集包一次取回已录制的图片。
4. 更新资源无需清缓存，未变化内容可复用。
5. 无变化的日常构建快速完成，修改单项只处理单项。
6. AssetServer 故障与游戏服隔离，完整客户端完全不受影响。
7. 所有协议、损坏输入、路径安全、增量构建和冷缓存测试通过。
