#!/usr/bin/env python3
"""Localize high-confidence game text and report remaining English.

This tool intentionally avoids binary databases such as Server.MirDB. It only
edits UTF text files where exact replacements are safe, switches runtime config
language settings to Chinese, and writes a residual-English report for review.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import shutil
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]

TEXT_SUFFIXES = {
    ".cs",
    ".resx",
    ".html",
    ".js",
    ".css",
    ".txt",
    ".ini",
    ".json",
    ".config",
}

SKIP_DIRS = {
    ".git",
    ".vs",
    "bin",
    "obj",
    "Cache",
    "Data",
    "Map",
    "Sound",
    "DirectX",
}

RUNTIME_TEXT_ROOTS = [
    ROOT / "Build" / "Server" / "Debug" / "Envir",
    ROOT / "Build" / "Server" / "Release" / "Envir",
    ROOT / "Build" / "Server" / "Debug" / "Configs",
    ROOT / "Build" / "Server" / "Release" / "Configs",
    ROOT / "Build" / "Client" / "Debug",
    ROOT / "Build" / "Client" / "Release",
]

CLIENT_RUNTIME_CONFIGS = [
    ROOT / "Build" / "Client" / "Debug" / "Mir2Config.ini",
    ROOT / "Build" / "Client" / "Debug" / "Mir2Test.ini",
    ROOT / "Build" / "Client" / "Release" / "Mir2Config.ini",
    ROOT / "Build" / "Client" / "Release" / "Mir2Test.ini",
]

EXACT_REPLACEMENTS = {
    "Welcome to Legend of Mir 2": "欢迎来到传奇2",
    "Welcome to Crystal Mir 2 released by Suprcode.": "欢迎来到 Crystal 传奇2。",
    "Make sure to follow JevLomcn on github for the latest Database releases.": "最新数据库发布请关注 JevLomcn 的 GitHub。",
    "Now in Net.8": "当前已升级至 .NET 8。",
    "!!MUST READ!!": "!!请务必阅读!!",
    "By clicking close and continuing to play the": "点击关闭并继续游戏，即表示你同意",
    "game you are agreeing to the terms of": "上述服务条款。",
    "service above.": "",
    "Recent Commits": "最近更新",
    "Crystal Mir2 Patcher": "传奇2 补丁更新器",
    "No items found in the ItemInfoList.": "物品列表中未找到任何物品。",
    "Monster Count:": "怪物数量：",
    "Currently Editing:": "当前编辑：",
}

CONFIG_REPLACEMENTS = {
    re.compile(r"^(Language\s*=\s*)English\s*$", re.IGNORECASE | re.MULTILINE): r"\1Chinese",
    re.compile(r"^(FontName\s*=\s*)Arial\s*$", re.IGNORECASE | re.MULTILINE): r"\1HarmonyOS Sans SC Medium",
}

PLACEHOLDER_RE = re.compile(r"\{[0-9]+(?::[^}]*)?\}")
ENGLISH_RE = re.compile(r"[A-Za-z]{3,}")
CHINESE_RE = re.compile(r"[\u4e00-\u9fff]")
STRING_LITERAL_RE = re.compile(r'(?<![\w@])"([^"\\]*(?:\\.[^"\\]*)*)"')
RESX_VALUE_RE = re.compile(r"<value>(.*?)</value>")
NPC_LINK_RE = re.compile(r"<([^<>/@][^<>]*?)/(@[^<>]+)>")
NPC_COLORED_RE = re.compile(r"\{([^{}\/]+)\/([A-Za-z]+)\}")

WHITELIST_PATTERNS = [
    re.compile(r"^\s*(#INSERT|#INCLUDE|#CALL|#ACT|#IF|#SAY|\[|@)"),
    re.compile(r"^\s*(GOTO|CHECK|CHECKGOLD|CHECKPKPOINT|CHECKQUEST|MOVE|TAKEGOLD|LEVEL)\b", re.IGNORECASE),
    re.compile(r"https?://|www\.|github|LOMCN|\.NET|Net\.8", re.IGNORECASE),
    re.compile(r"^[A-Za-z0-9_./\\\-[\]{}: <>=,!+*'\"()]+$"),
    re.compile(r"(IPAddress|AssetBaseUrl|PatchFile|Server\.MirDB|Server\.MirADB|Mir2|Crystal)", re.IGNORECASE),
]

NPC_LINK_TEXT = {
    "Close": "关闭",
    "Exit": "退出",
    "Back": "返回",
    "Service": "服务",
    "Time": "下次再说",
    "View": "查看",
    "Repair": "修理",
    "Special": "特殊",
    "Buy Back": "回购",
    "Use.": "使用",
    "News": "消息",
    "Rebirth": "转生",
    "Request Guild creation": "申请创建行会",
    "Request Guild war": "申请行会战争",
    "Request Sabuk conquest war": "申请沙巴克攻城战",
    "Ask how to create a Guild": "询问如何创建行会",
    "Ask about Guild war": "询问行会战争",
    "Apply for Sabuk conquest": "申请沙巴克攻城",
    "Guild war": "行会战争",
    "Awaken": "觉醒",
    "Disassemble": "分解",
    "Downgrade": "降级",
    "Reset": "重置",
    "Dungeon 1": "地下城 1",
    "Dungeon 2": "地下城 2",
    "Dungeon 3": "地下城 3",
    "Ancient Oma Cave": "远古沃玛洞穴",
    "Ancient Wooma Temple": "远古沃玛寺庙",
    "Ancient Stone Temple": "远古石墓",
    "Ancient Zuma Temple": "远古祖玛寺庙",
    "Ancient Prajna Cave": "远古潘夜洞穴",
    "BorderVillage": "边境村",
    "BichonWall": "比奇城",
    "SerpentValley": "蛇谷",
    "MudWall": "盟重土城",
    "TaoistSchool": "道馆",
    "CastleGi-Ryoong": "蟠龙城",
    "WoomyonCamp": "沃玛营地",
}

NPC_COLORED_TEXT = {
    "Gold": "金币",
    "Level": "等级",
    "Required": "需求",
    "PrajnaHeart": "潘夜之心",
    "StoneHeart": "石墓之心",
    "WoomaHeart": "沃玛之心",
    "ZumaHeart": "祖玛之心",
}

NPC_PHRASES = {
    "Hello <$USERNAME>, My name is Jerald.": "你好，<$USERNAME>，我叫杰拉德。",
    "I am the MasterMage Don, What's your name?": "我是大法师唐，你叫什么名字？",
    "I am the Administrator. How may I help you ?": "我是管理员。有什么可以帮你？",
    "I will not help an evil person like you...": "我不会帮助你这样的邪恶之人...",
    "This is the palace of Bichon wall.": "这里是比奇皇宫。",
    "Do you have it?": "你带来了吗？",
    "Welcome <$USERNAME>, I am the Awakening Master.": "欢迎你，<$USERNAME>，我是觉醒大师。",
    "So what do you say?": "你意下如何？",
    "Which place would you like to go?": "你想去哪里？",
    "I'll use this": "我要使用这项",
    "Maybe next": "下次再说",
    "Hello I'm Jason, the wandering warrior.": "你好，我是流浪战士杰森。",
    "Congratulations <$USERNAME>,": "恭喜你，<$USERNAME>，",
    "Hello, Are you looking for something particular?": "你好，你在找什么特别的东西吗？",
    "Which item would you like to Buy or Sell?": "你想买卖哪件物品？",
    "Would you like to repair a weapon?": "你想修理武器吗？",
    "Hello again traveler.. How are you on this fine day?": "又见面了，旅行者。今天过得如何？",
    "How can I trust you? I don't even know who you are.": "我怎么能信任你？我甚至不知道你是谁。",
    "Maybe you could do something for me?": "也许你可以先帮我做点事？",
    "I see you're wearing: {<$WEAPON>/CORAL}": "我看到你装备着：{<$WEAPON>/CORAL}",
    "I shall inform those will <listen/@listen>.": "我会告知那些愿意 <倾听/@listen> 的人。",
    "<Use./@main-1> teleport to the village stores": "<使用/@main-1> 传送到村庄商店",
    "<News/@ask> of the village": "村庄的 <消息/@ask>",
    "Learn about <Rebirth/@RebirthMain>": "了解 <转生/@RebirthMain>",
    "I transport men and goods to other places fast and safe.": "我能快速又安全地把人和货物送到其他地方。",
    "Just pay the fee then I'll escort you to anywhere.": "只要支付费用，我就护送你前往目的地。",
    "My service fee is 2000 gold.": "我的服务费是 2000 金币。",
    "Nothing happens.": "什么也没有发生。",
    "A Mysterious Stone. With Ancient symbols.": "一块刻有远古符号的神秘石头。",
    "Item.": "物品。",
    "Dungeons to come.": "更多地下城即将开放。",
    "In order to qualify for guild creation": "想要获得创建行会的资格，",
    "you need to bring me several items:-": "你需要带来以下物品：",
    "One million {金币/Gold} & the Horn of WoomaTaurus, who lives": "一百万 {金币/Gold}，以及沃玛教主的号角；它栖息在",
    "in deep in the Wooma Temple in Woomyon Woods.": "沃玛森林深处的沃玛寺庙。",
    "Please keep in mind that no special characters": "请记住，行会名称中",
    "are allowed in guild names.": "不允许使用特殊字符。",
    "Guilds made by using alt codes or spaces in the name": "使用 Alt 代码或空格创建的行会",
    "will be deleted and no refund made.": "将被删除，且不会退还费用。",
    "<行会战争/@guildwar2> means war done by legal request.": "<行会战争/@guildwar2> 是通过合法申请发起的战争。",
    "Actually, due to the number of guilds and struggles between": "由于行会众多，彼此争斗不断，",
    "them, the government approve <legal/@warrule> guild war.": "政府允许进行 <合法/@warrule> 的行会战争。",
    "When <requested/@propose>, war will be allowed for 3 hours.": "当战争 <申请/@propose> 通过后，将持续 3 小时。",
    "You'll have to pay <$GUILDWARFEE> {金币/Gold} as request fee.": "你需要支付 <$GUILDWARFEE> {金币/Gold} 作为申请费用。",
    "Required {金币/Gold}: <$GUILDWARFEE> Last's for <$GUILDWARTIME> Minutes.": "需要 {金币/Gold}: <$GUILDWARFEE>，持续 <$GUILDWARTIME> 分钟。",
    "When you request guild war, names of allies will appear in blue,": "申请行会战争后，盟友名称会显示为蓝色，",
    "on the other side, enemy names will appear orange.": "敌方名称会显示为橙色。",
    "If you are connected during a Guild War, the message:": "如果你在行会战争期间上线，会看到提示：",
    "'War with {nnn} guild' will appear in chatting window and": "聊天窗口会显示“与 {nnn} 行会开战”，",
    "if you kill a member of the enemy guild you will not": "击杀敌对行会成员时，",
    "be regarded as PK.": "不会被视为 PK。",
    "Guildwar cannot take place in village.": "行会战争不能在村庄内进行。",
    "It can be executed out of certain ranges from the": "必须离开村庄一定范围，",
    "village or inside contest area (like inside some buildings).": "或在争夺区域内进行（例如某些建筑内）。",
    "However, if you are PK your ID will be red in colour,": "不过，如果你处于 PK 状态，名字仍会显示红色，",
    "even during war.": "即使在战争期间也是如此。",
    "Proposal of guild war only can be requested": "行会战争申请只能由",
    "by Guildchief.": "行会会长提出。",
    "We are not collecting donation money yet.": "目前还不能收取捐款。",
    "To request Sabuk conquest war you should have ZumaRelic.": "申请沙巴克攻城战需要持有祖玛遗物。",
    "Once you request to conquer Sabuk the fight will": "一旦申请攻打沙巴克，战争将会",
    "start 2 days later.": "在 2 天后开始。",
    "Wall conquest war accepted.": "城墙攻城战申请已受理。",
    "Run to Sabuk wall as fast as you can": "尽快赶往沙巴克城墙，",
    "to ask the oldman for the date and time of the war.": "向老人询问战争日期和时间。",
    '"Dungeon of the Ancient <Ones/@omacavea>" {Level10~22./KHAKI}': '"远古之人的地下城 <进入/@omacavea>" {等级10~22./KHAKI}',
    '"The Bones are crunching the Fear is <within/@prajnacavea>"': '"骸骨作响，恐惧潜藏 <其中/@prajnacavea>"',
    '"The noise rumbles the faint whispers are <creepy/@stonetomba>"': '"轰鸣回荡，低语令人 <不寒而栗/@stonetomba>"',
    '"Banished Creatures <within./@Woomaa>"': '"被放逐的生物就在 <其中/@Woomaa>"',
    '"The still dont remain still for <long./@zumatemplea>"': '"静止之物不会永远 <静止/@zumatemplea>"',
}


def read_text(path: Path) -> str | None:
    for encoding in ("utf-8-sig", "utf-8", "gb18030", "cp1252"):
        try:
            return path.read_text(encoding=encoding)
        except UnicodeDecodeError:
            continue
    return None


def write_text(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")


def iter_text_files(roots: list[Path]) -> list[Path]:
    files: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        if root.is_file():
            candidates = [root]
        elif root.match("*/Build/Client/*"):
            candidates = [p for p in root.iterdir() if p.is_file()]
        else:
            candidates = [p for p in root.rglob("*") if p.is_file()]
        for path in candidates:
            if path.suffix.lower() not in TEXT_SUFFIXES:
                continue
            if any(part in SKIP_DIRS for part in path.relative_to(ROOT).parts[:-1]):
                if "Build" not in path.relative_to(ROOT).parts:
                    continue
            files.append(path)
    return sorted(set(files))


def sync_language_json(english_path: Path, chinese_path: Path) -> list[str]:
    messages: list[str] = []
    english = json.loads(english_path.read_text(encoding="utf-8-sig"))
    chinese = json.loads(chinese_path.read_text(encoding="utf-8-sig"))

    changed = False
    for section in ("Text", "Enum"):
        english_section = english.get(section, {})
        chinese_section = chinese.setdefault(section, {})
        for key, value in english_section.items():
            if key not in chinese_section:
                chinese_section[key] = value
                messages.append(f"{chinese_path}: added missing {section}.{key}")
                changed = True

        ordered = {key: chinese_section[key] for key in english_section if key in chinese_section}
        for key, value in chinese_section.items():
            if key not in ordered:
                ordered[key] = value
        if ordered != chinese_section:
            chinese[section] = ordered
            changed = True

    if changed:
        chinese_path.write_text(
            json.dumps(chinese, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    return messages


def validate_placeholders(english_path: Path, chinese_path: Path) -> list[str]:
    issues: list[str] = []
    english = json.loads(english_path.read_text(encoding="utf-8-sig"))
    chinese = json.loads(chinese_path.read_text(encoding="utf-8-sig"))
    for section in ("Text", "Enum"):
        for key, en_value in english.get(section, {}).items():
            zh_value = chinese.get(section, {}).get(key, "")
            if sorted(PLACEHOLDER_RE.findall(en_value)) != sorted(PLACEHOLDER_RE.findall(zh_value)):
                issues.append(f"{chinese_path}:{section}.{key}: placeholder mismatch")
    return issues


def backup_runtime_text() -> list[str]:
    timestamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_root = ROOT / "Build" / f"LocalizationBackup-{timestamp}"
    messages: list[str] = []
    for source in [ROOT / "Build" / "Server" / "Debug" / "Envir", ROOT / "Build" / "Server" / "Release" / "Envir"]:
        if not source.exists():
            continue
        dest = backup_root / source.relative_to(ROOT / "Build")
        shutil.copytree(source, dest)
        messages.append(f"Backed up {source.relative_to(ROOT)} -> {dest.relative_to(ROOT)}")
    return messages


def localize_file(path: Path) -> bool:
    text = read_text(path)
    if text is None:
        return False

    new_text = text
    for pattern, replacement in CONFIG_REPLACEMENTS.items():
        new_text = pattern.sub(replacement, new_text)

    for src, dest in EXACT_REPLACEMENTS.items():
        new_text = new_text.replace(src, dest)

    if path.suffix.lower() == ".txt" and "Envir" in path.parts:
        new_text = localize_npc_script_text(new_text)

    if new_text != text:
        write_text(path, new_text)
        return True
    return False


def localize_npc_script_text(text: str) -> str:
    for src, dest in NPC_PHRASES.items():
        text = text.replace(src, dest)

    def replace_link(match: re.Match[str]) -> str:
        display = match.group(1)
        target = match.group(2)
        translated = NPC_LINK_TEXT.get(display.strip(), display)
        return f"<{translated}/{target}>"

    def replace_colored(match: re.Match[str]) -> str:
        display = match.group(1)
        color = match.group(2)
        translated = NPC_COLORED_TEXT.get(display.strip(), display)
        return f"{{{translated}/{color}}}"

    text = NPC_LINK_RE.sub(replace_link, text)
    text = NPC_COLORED_RE.sub(replace_colored, text)
    text = text.replace(" Required ", " 需求 ")
    text = text.replace(" Required", " 需求")
    text = text.replace(" Repair Weapon.", " 修理武器。")
    text = text.replace(" Store.", " 商店。")
    text = text.replace(" Item.", " 物品。")
    text = re.sub(r"\bLevel\s*", "等级", text)
    return text


def get_report_roots() -> list[Path]:
    return [
        ROOT / "Client",
        ROOT / "Server",
        ROOT / "Server.MirForms",
        ROOT / "AutoPatcherAdmin",
        ROOT / "LibraryEditor",
        ROOT / "LibraryViewer",
        ROOT / "PatcherWebSite",
        ROOT / "Build" / "Server" / "Debug" / "Envir",
        ROOT / "Build" / "Server" / "Release" / "Envir",
        ROOT / "Build" / "Server" / "Debug" / "Configs",
        ROOT / "Build" / "Server" / "Release" / "Configs",
        *CLIENT_RUNTIME_CONFIGS,
    ]


def get_mutation_roots() -> list[Path]:
    return [
        ROOT / "PatcherWebSite",
        ROOT / "Build" / "Server" / "Debug" / "Envir",
        ROOT / "Build" / "Server" / "Release" / "Envir",
        ROOT / "Build" / "Server" / "Debug" / "Configs",
        ROOT / "Build" / "Server" / "Release" / "Configs",
        *CLIENT_RUNTIME_CONFIGS,
    ]


def extract_candidate_texts(path: Path, line: str) -> list[str]:
    suffix = path.suffix.lower()

    if suffix == ".cs":
        stripped = line.lstrip()
        if stripped.startswith((
            "using ",
            "namespace ",
            "public ",
            "private ",
            "protected ",
            "internal ",
            "return ",
            "if ",
            "for ",
            "foreach ",
            "while ",
            "switch ",
            "//",
        )):
            return []
        return [match.group(1) for match in STRING_LITERAL_RE.finditer(line)]

    if suffix == ".resx":
        match = RESX_VALUE_RE.search(line)
        return [match.group(1)] if match else []

    if suffix == ".json":
        try:
            _, value = line.split(":", 1)
        except ValueError:
            return []
        return [value.strip().strip('",')]

    if suffix in {".html", ".js"}:
        candidates = [match.group(1) for match in STRING_LITERAL_RE.finditer(line)]
        text_candidate = re.sub(r"<[^>]+>", " ", line).strip()
        if text_candidate and text_candidate != line.strip():
            candidates.append(text_candidate)
        return candidates

    if suffix in {".txt", ".ini"}:
        stripped = line.strip()
        if not stripped or stripped.startswith(("#", "[", ";", "@")):
            return []
        if "=" in stripped:
            key, value = stripped.split("=", 1)
            if key.isidentifier() or re.fullmatch(r"[A-Za-z0-9_.-]+", key):
                return [value]
        return [stripped]

    return []


def generate_report(files: list[Path], binary_db_paths: list[Path], log: list[str], issues: list[str]) -> None:
    report_dir = ROOT / "Tools" / "Localization" / "Reports"
    report_dir.mkdir(parents=True, exist_ok=True)
    report_path = report_dir / "localization-report.md"

    residual: list[str] = []
    for path in files:
        text = read_text(path)
        if text is None:
            continue
        for index, line in enumerate(text.splitlines(), 1):
            for candidate in extract_candidate_texts(path, line):
                if not ENGLISH_RE.search(candidate):
                    continue
                if CHINESE_RE.search(candidate) and len(ENGLISH_RE.findall(candidate)) <= 2:
                    continue
                if any(pattern.search(candidate) for pattern in WHITELIST_PATTERNS):
                    continue
                residual.append(f"- `{path.relative_to(ROOT)}`:{index}: {candidate.strip()[:180]}")
                break
            if len(residual) >= 300:
                break

    content = [
        "# Localization Report",
        "",
        f"Generated: {dt.datetime.now().isoformat(timespec='seconds')}",
        "",
        "## Actions",
    ]
    content.extend(f"- {item}" for item in log)
    content.extend(["", "## Placeholder Issues"])
    content.extend((f"- {item}" for item in issues) if issues else ["- None"])
    content.extend(["", "## Binary Databases Requiring Dedicated Editor"])
    content.extend(
        (f"- `{path.relative_to(ROOT)}`" for path in binary_db_paths)
        if binary_db_paths
        else ["- None found"]
    )
    content.extend(["", "## Residual English Candidates"])
    content.extend(residual if residual else ["- None outside whitelist"])
    content.append("")
    report_path.write_text("\n".join(content), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-backup", action="store_true", help="Do not backup runtime Envir folders.")
    args = parser.parse_args()

    log: list[str] = []
    issues: list[str] = []

    for english, chinese in [
        (ROOT / "Client" / "Localization" / "English.json", ROOT / "Client" / "Localization" / "Chinese.json"),
        (ROOT / "Server.MirForms" / "Localization" / "English.json", ROOT / "Server.MirForms" / "Localization" / "Chinese.json"),
    ]:
        log.extend(sync_language_json(english, chinese))
        issues.extend(validate_placeholders(english, chinese))

    if not args.no_backup:
        log.extend(backup_runtime_text())

    report_files = iter_text_files(get_report_roots())
    mutation_files = iter_text_files(get_mutation_roots())
    changed_files = [path for path in mutation_files if localize_file(path)]
    log.extend(f"Localized {path.relative_to(ROOT)}" for path in changed_files)

    binary_db_paths = [
        path for path in (ROOT / "Build").rglob("*")
        if path.is_file() and path.name in {"Server.MirDB", "Server.MirADB"}
    ] if (ROOT / "Build").exists() else []

    generate_report(report_files, binary_db_paths, log, issues)
    print(f"Localized {len(changed_files)} text files.")
    print(f"Placeholder issues: {len(issues)}")
    print("Report: Tools/Localization/Reports/localization-report.md")
    return 1 if issues else 0


if __name__ == "__main__":
    raise SystemExit(main())
