# Server.MirDB 汉化工具

此工具使用服务端现有的 `BinaryReader` / `BinaryWriter` 数据结构读取并保存 `Server.MirDB`。它不会对二进制文件做裸文本替换。

先停止服务端，再执行下面的命令。默认运行服数据库是：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- export --database Build\Server\Debug\Server.MirDB --output Tools\DatabaseLocalization\debug-zh-CN.json
```

导出的 JSON 中只编辑 `translation`。不可修改 `key`、`kind`、`index`、`field`、`source`，否则校验会失败。

检查译文和数据库是否仍匹配：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- validate --database Build\Server\Debug\Server.MirDB --input Tools\DatabaseLocalization\debug-zh-CN.json
```

查看完成进度：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- report --input Tools\DatabaseLocalization\debug-zh-CN.json
```

先预演导入，命令不会写入数据库：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- apply --database Build\Server\Debug\Server.MirDB --input Tools\DatabaseLocalization\debug-zh-CN.json
```

确认预演通过后，使用 `--write` 写回。工具会先在运行服目录创建 `LocalizationBackups\Server.MirDB.*.bak`：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- apply --database Build\Server\Debug\Server.MirDB --input Tools\DatabaseLocalization\debug-zh-CN.json --write
```

写入完成后，核对所有译文已经持久化，并确认物品、怪物的英文内部查找名没有改变：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- verify --database Build\Server\Debug\Server.MirDB --input Tools\DatabaseLocalization\debug-zh-CN.json
```

物品名和怪物名现在使用独立的玩家显示名字段。数据库中的英文 `Name` 仍然保留为内部查找键，因此不会破坏掉落表、任务、NPC 脚本及服务器设置。NPC 名称、地图标题、技能名称、任务名称和任务消息、物品 Tooltip 也都可以安全回写。任务详细说明不在数据库内，而在 `Envir\Quests\*.txt`。

工具会把数据库从旧版本自动升级到包含显示名字段的新版本。升级前会创建备份；升级后的服务端和客户端必须使用同一份新代码编译出来的文件。
