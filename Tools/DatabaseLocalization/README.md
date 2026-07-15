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

可安全回写的字段包括：NPC 名称、地图标题、技能名称、任务名称和任务消息、物品 Tooltip。任务详细说明不在数据库内，而在 `Envir\Quests\*.txt`。

物品名和怪物名是高风险字段。它们还被掉落表、任务、NPC 脚本及部分服务器设置用作查找键。工具会导出它们供审阅，但默认拒绝写回。只有在同步处理完引用后，才可明确传入 `--allow-unsafe-names`：

```powershell
dotnet run --project Tools\DatabaseLocalization\DatabaseLocalization.csproj -- apply --database Build\Server\Debug\Server.MirDB --input Tools\DatabaseLocalization\debug-zh-CN.json --allow-unsafe-names --write
```

这项危险开关不会自动重写脚本、掉落表或配置。怪物改名尤其需要检查 `Envir\Drops\<怪物英文名>.txt` 和 `Configs\Setup.ini` 中的特殊怪物名。
