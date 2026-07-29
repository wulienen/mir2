# AssetBuilder v2 使用说明

AssetBuilder 把完整客户端资源转换成 AssetServer 可以按需发送的流式资源。

v2 使用 SHA-256 内容寻址。同一个资源即使跨版本也只保存和下载一次；修改
`Title.Lib` 不会让客户端重新下载未变化的地图、怪物和声音。

## 目录约定

完整客户端：

```text
E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug
```

其中只读取：

- `Data_Full\**\*.Lib`，没有 `Data_Full` 时才读取 `Data`
- `Map\*.map`
- `Sound\*.wav`、`*.mp3` 和 `SoundList.lst`

`Client.exe`、DLL 和登录器配置不会被当成资源。

建议将 v2 输出放在新目录：

```text
E:\GameSourceCode\StreamingAssetsV2
```

## 从现有资源快速迁移

已有 v1 `StreamingAssets` 时，不需要重新解析 8 GB 的 `Data_Full`。

```powershell
cd E:\GameSourceCode\YangfeiCrystal\Tools\AssetBuilder

dotnet run --project AssetBuilder.csproj -- migrate-v2 `
  "E:\GameSourceCode\StreamingAssets" `
  "E:\GameSourceCode\StreamingAssetsV2"
```

迁移会读取已经拆好的图片、地图块和声音，生成二进制索引与 v2 对象目录。
在同一个磁盘上优先使用硬链接，不会再复制一份大资源；硬链接不可用时自动复制。

迁移只有全部校验成功后才会发布 `manifest.json`。中途失败不会产生可用的半成品版本。

迁移完成后，执行一次以下命令登记源文件状态：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssetsV2" `
  --adopt-existing
```

这一步不会重新拆分资源。以后日常更新就不再需要 `--adopt-existing`。

## 第一次全新生成

没有旧 StreamingAssets 时执行：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssetsV2"
```

第一次需要读取全部源资源，耗时较长。后续更新使用增量模式。

## 日常更新

`Data_Full`、`Map` 或 `Sound` 有新增或修改后，仍执行同一条命令：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssetsV2"
```

AssetBuilder 会自动判断：

| 变化 | 处理方式 |
| --- | --- |
| 新增或修改一个 `.Lib` | 只处理这个图库 |
| 新增或修改一个 `.map` | 只处理这张地图 |
| 新增或修改一个声音 | 只处理这个声音 |
| 没有变化 | 直接复用已有对象 |

输出中的 `rebuilt` 是本次重建数量，`reused` 是直接复用数量。

如果构建中任意资源失败，AssetBuilder 不会替换已发布的根清单，AssetServer 会继续
提供上一版完整资源。

## AssetServer 设置

修改 `AssetServer\appsettings.json`：

```json
{
  "Urls": "http://0.0.0.0:8088",
  "AssetRoot": "E:\\GameSourceCode\\StreamingAssetsV2",
  "CacheSeconds": 31536000
}
```

启动：

```powershell
cd E:\GameSourceCode\YangfeiCrystal\AssetServer
dotnet run
```

检查：

```text
http://127.0.0.1:8088/health
```

`ok=true` 且 `formatVersion=2` 表示资源目录正确。

## 客户端设置

```ini
[Streaming]
Enabled=True
AssetBaseUrl=http://127.0.0.1:8088/assets/v2/
PreferLocalAssets=True
ConcurrentDownloads=4
RequestTimeoutSeconds=30
CachePath=.\Cache\Assets\
CacheMaxMB=4096
```

旧配置中的 `/assets/v1/` 会自动升级为 `/assets/v2/`，IP 和端口保持不变。

`CacheMaxMB` 是流式缓存上限，最低 256 MB，默认 4096 MB。超过上限时优先删除
最久没有使用的对象。旧的按版本缓存目录会在客户端启动时自动清理。

客户端不需要删除 `Data` 或 `Cache` 才能刷新资源：

- 本地 `Data\*.Lib` 存在且 `PreferLocalAssets=True` 时，始终优先本地文件。
- 流式资源更新后，新清单引用新哈希；客户端自动下载变化对象。
- 哈希没有变化的对象直接复用，不重复下载。

## 检查与排错

运行内置协议自测：

```powershell
dotnet run --project AssetBuilder.csproj -- self-test
```

怀疑源文件内容变化但修改时间没变时：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssetsV2" `
  --verify
```

只有输出损坏或需要从源资源完全重建时才使用 `--full`。它会重新读取全部资源：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssetsV2" `
  --full
```
