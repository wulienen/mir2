# NPC 与任务脚本汉化工具

该工具只导出玩家可见的 NPC 对话、NPC 自动喊话和任务描述，不修改脚本命令、页面名、物品/怪物内部名或任务配置。

导出：

```powershell
python Tools\ScriptLocalization\script_localization.py export --envir Build\Server\Debug\Envir --output Tools\ScriptLocalization\Translations\Debug.zh-CN.json
```

填写 JSON 中的 `translation` 后，先校验和预演：

```powershell
python Tools\ScriptLocalization\fill_script_translations.py Tools\ScriptLocalization\Translations\Debug.zh-CN.json
python Tools\ScriptLocalization\script_localization.py validate --envir Build\Server\Debug\Envir --input Tools\ScriptLocalization\Translations\Debug.zh-CN.json
python Tools\ScriptLocalization\script_localization.py apply --envir Build\Server\Debug\Envir --input Tools\ScriptLocalization\Translations\Debug.zh-CN.json
```

`fill_script_translations.py` 使用 `manual_script_translations.py` 中维护的人工整句译文，不下载、调用或依赖任何翻译模型。以 `@` 开头的 GM 命令说明会被扫描器跳过，避免汉化命令名后无法执行。

正式写入前必须停止服务端。`--write` 会先在 `Build\Server\Debug\ScriptLocalizationBackups` 创建 ZIP 备份：

```powershell
python Tools\ScriptLocalization\script_localization.py apply --envir Build\Server\Debug\Envir --input Tools\ScriptLocalization\Translations\Debug.zh-CN.json --write
python Tools\ScriptLocalization\script_localization.py verify --envir Build\Server\Debug\Envir --input Tools\ScriptLocalization\Translations\Debug.zh-CN.json
```

`protectedTokens` 中的跳转目标、变量、颜色和数字必须保持原样，否则校验会拒绝写入。
