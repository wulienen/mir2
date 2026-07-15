# AssetBuilder 使用说明

`AssetBuilder` 的作用是把完整客户端中的资源转换为资源服务器可以提供的 `StreamingAssets` 文件。

客户端不会直接下载完整的 `Data_Full`、`Map`、`Sound` 目录，而是根据这里生成的文件，按需要下载图片、地图区块和音频。

## 一、准备工作

本项目中的完整客户端目录是：

```text
E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug
```

资源服务器目录建议使用：

```text
E:\GameSourceCode\StreamingAssets
```

运行命令前，先进入工具目录：

```powershell
cd E:\GameSourceCode\YangfeiCrystal\Tools\AssetBuilder
```

资源来源规则：

- 图库：优先读取 `Debug\Data_Full`。只有不存在 `Data_Full` 时才读取 `Debug\Data`。
- 地图：读取 `Debug\Map`。
- 音频：读取 `Debug\Sound` 中的 `.wav`、`.mp3` 和 `SoundList.lst`。

`Debug` 目录中的 `Client.exe`、DLL、配置文件等登录器文件不会被当作资源转换。

## 二、第一次使用增量功能

如果你已经成功生成过 `StreamingAssets`，只是之前的 AssetBuilder 没有增量状态文件，先执行一次下面的命令：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssets" `
  --adopt-existing
```

这一步只登记当前的资源输出状态，生成：

```text
E:\GameSourceCode\StreamingAssets\.assetbuilder-state.json
```

它不会重新拆分全部资源，不会重新生成 8G 文件。

注意：只有当当前 `StreamingAssets` 确实是从现在这份完整客户端资源生成时，才使用 `--adopt-existing`。

## 三、日常更新资源

以后只要 `Data_Full`、`Map` 或 `Sound` 有新增或修改，执行：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssets"
```

AssetBuilder 会自动判断哪些源文件变了。

| 资源变化 | 会发生什么 |
| --- | --- |
| 新增一个 `.Lib` | 只拆分这个图库 |
| 修改一个 `.Lib` | 只重新生成这个图库的图片 chunk 和 manifest |
| 新增或修改一个 `.map` | 只重新生成这张地图的区块 |
| 新增或修改一个音频 | 只复制这个音频 |
| 没有修改的资源 | 直接复用，不重新拆分 |

运行时会显示进度，例如：

```text
[Libraries]   34.7% (301/1439) rebuilt 3, reused 298, elapsed 00:02:14 | Objects12.Lib
```

其中：

- `34.7%`：当前阶段进度。
- `301/1439`：当前处理到第几个文件。
- `rebuilt 3`：本次重新转换的文件数。
- `reused 298`：直接复用旧输出的文件数。
- 最后的文件名：当前正在处理的文件。

正常的小范围更新结束时，应该看到大部分文件是 `reused`，只有变更的文件是 `rebuilt`。

## 四、版本号

日常更新时不需要手动填写版本号。只要发现资源变化，AssetBuilder 会自动生成一个新的资源版本号；没有资源变化时会保留当前版本号。

如果你需要指定版本号，例如 `v4`，可以在最后加上：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssets" `
  v4
```

## 五、需要全量重建时

通常不需要全量重建。以下情况才建议使用：

- 第一次创建一个全新的 `StreamingAssets` 目录。
- 你怀疑现有输出目录被误删、损坏或混入了错误资源。
- AssetBuilder 的资源格式以后发生了不兼容升级。

强制全量重建命令：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssets" `
  --full
```

这个命令会重新处理所有资源，耗时会比较长。

## 六、深度校验模式

普通增量判断使用文件大小和最后修改时间，正常编辑资源后会自动识别。

如果你担心某个工具修改了资源内容却没有修改时间，或者希望做一次更严格的检查，可以加 `--verify`：

```powershell
dotnet run --project AssetBuilder.csproj -- `
  "E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug" `
  "E:\GameSourceCode\StreamingAssets" `
  --verify
```

`--verify` 会额外计算源文件 SHA-256，读取量较大，只在排查问题时使用，不建议每次更新都使用。

## 七、和 AssetServer 的关系

AssetBuilder 只负责生成资源文件，不启动资源服务器。

AssetServer 的 `AssetRoot` 应该指向 AssetBuilder 的输出目录：

```json
{
  "AssetRoot": "E:\\GameSourceCode\\StreamingAssets"
}
```

客户端配置中的 `AssetBaseUrl` 则指向 AssetServer，例如：

```ini
[Streaming]
Enabled=True
AssetBaseUrl=http://127.0.0.1:8088/assets/v1/
PreferLocalAssets=True
```

资源更新完成后，新启动的客户端会读取最新的顶层 manifest；已经在线的客户端通常在重新启动后会拿到新版本。

## 八、常见问题

### 每次都显示大量 `rebuilt`

检查以下内容：

- `StreamingAssets\.assetbuilder-state.json` 是否存在。
- 是否每次都带了 `--full`。
- 输出目录是否和上次运行时相同。
- 完整客户端文件是否被重新复制或解压，导致大量文件修改时间变化。

如果 `StreamingAssets` 是以前全量生成的、但没有 `.assetbuilder-state.json`，执行一次 `--adopt-existing` 即可。

### Data_Full 新增资源后没有被处理

确认新增的是 `.Lib` 文件，并且放在：

```text
E:\GameSourceCode\YangfeiCrystal\Build\Client\Debug\Data_Full
```

然后重新执行“日常更新资源”命令。

### 想只修复顶层 manifest

可以使用现有命令：

```powershell
dotnet run --project AssetBuilder.csproj -- fix-manifest `
  "E:\GameSourceCode\StreamingAssets" `
  v4
```

这个命令只修复顶层 manifest 中的 hash 和版本号，不重新拆分资源。
