# AssetBuilder V3 使用说明

AssetBuilder V3 把完整客户端资源发布成 AssetServer 可按需发送的资源。V3 不再把每张图片拆成一个小文件：图库保留为原始 `.Lib`，客户端只用 HTTP Range 下载当前需要的图片字节；地图按 32x32 格写入 Map Pack；声音按原文件发布。

客户端只使用一个 SQLite 缓存文件 `Cache\AssetsV3.db`，不会向 `Data`、`Map` 或 `Sound` 写入不完整资源。

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

## 4. 启动 AssetServer

修改 `AssetServer\appsettings.json`：

```json
{
  "Urls": "http://0.0.0.0:8088",
  "AssetRoot": "E:\\GameSourceCode\\StreamingAssetsV3",
  "CacheSeconds": 31536000
}
```

`AssetRoot` 必须指向包含 V3 `manifest.json`、`catalogs`、`libraries`、`maps` 和 `sounds` 的输出目录，不能指向 `Data_Full`。

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
CachePath=.\Cache\AssetsV3.db
CacheMaxMB=4096
```

- `PreferLocalAssets=True`：本地存在结构完整的 `Data\*.Lib` 或 `Map\*.map` 时优先使用本地文件，完整客户端不依赖资源服。
- 本地 Lib 会覆盖资源服同名 Lib。要让某个本地 Lib 使用资源服新版本，应替换/删除这个完整 Lib，或把 `PreferLocalAssets` 改为 `False`。
- 微端不要把部分下载的 Lib 放进 `Data`。流式字节只进入 `AssetsV3.db`，避免半个 Lib 被误判为完整资源。
- 资源更新后根清单会引用新的 SHA-256。客户端自动下载变化部分并继续复用旧哈希缓存，不需要手工删除 `Data` 或 `AssetsV3.db`。

## 6. 首次进入错位问题

旧微端曾出现第一次登录、选角和进入游戏时 UI 错位，重启后正常。原因是控件创建时图片尺寸元数据尚未下载，首次布局使用了零尺寸或错误尺寸。

V3 的处理方式是：

1. 启动 Scene 前并发预取 `ChrSel`、`Prguse`、`Prguse2`、`Prguse3`、`XiayiUi`、`UI_32bit` 和 `Title` 的索引。
2. 索引保存每张图片的画布尺寸、偏移和按 Alpha 扫描得到的 `TrueWidth/TrueHeight`。
3. 布局只依赖索引，不等待图片像素下载；图片像素稍后到达只触发重绘，不重新改变控件位置。

发布前应使用一个没有 `Data` 且没有 `Cache\AssetsV3.db` 的干净微端，完整走一遍登录、创建/选择角色和进入地图。若启动时报“无法加载启动界面资源索引”，先检查 `/health`、客户端 URL 和上述七个 Lib 是否包含在 V3 清单中，不要靠重启掩盖问题。

## 7. 自检与排错

运行构建器内置自测，不会读取正式 8GB 资源：

```powershell
dotnet run --project Tools\AssetBuilder\AssetBuilder.csproj -- self-test-v3
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
| HTTP 返回 416 | Lib、Catalog 和 Map Pack 必须使用 Range 请求；浏览器直接整文件访问返回 416 是正常保护 |
| 完整客户端仍显示旧图片 | `PreferLocalAssets=True` 时本地 Lib 优先，更新本地 Lib 或关闭本地优先 |
| Cache 超过预期 | 检查 `CacheMaxMB`，客户端按 LRU 清理；SQLite 文件释放空间后通常复用页，不保证立刻缩小文件尺寸 |

不要把旧 `StreamingAssets` 或 `StreamingAssetsV2` 与 V3 输出混用。V3 客户端、V3 AssetServer 和 V3 `manifest.json` 必须配套使用。
