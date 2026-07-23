from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

from manual_script_translations import MANUAL_TRANSLATIONS, SKILL_NAMES


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DATABASE_TRANSLATIONS = ROOT / "Tools" / "DatabaseLocalization" / "Translations" / "Debug.zh-CN.json"


EXACT_PLAIN = {
    "Main": "主菜单",
    "Main Menu": "主菜单",
    "Go to Main": "返回主菜单",
    "Go to Next": "下一页",
    "Buy": "购买",
    "Buy.": "购买。",
    "Sell": "出售",
    "Sell.": "出售。",
    "BuyBack": "回购",
    "Purchase": "购买",
    "Repair": "修理",
    "Special Repair": "特殊修理",
    "Access": "打开",
    "Send": "发送",
    "Collect": "领取",
    "Ask": "询问",
    "Help": "帮助",
    "Craft": "制作",
    "Crafting": "制作",
    "Okay.": "好的。",
    "Ok": "好的",
    "Yes": "是",
    "No": "否",
    "Close": "关闭",
    "Exit": "退出",
    "Storage": "仓库",
    "Parcel": "包裹",
    "Parcels": "包裹",
    "Weapon": "武器",
    "Jewellery": "首饰",
    "Poison": "毒药",
    "Meat": "肉",
    "Item": "物品",
    "Drapery pieces.": "衣物。",
    "Weapon.": "武器。",
    "Jewellery.": "首饰。",
    "Bracelets or Gloves.": "手镯或手套。",
    "Poison.": "毒药。",
    "Meat.": "肉。",
    "Hello!": "你好！",
    "You are not the right level or to poor.": "你的等级不符或金币不足。",
    "MP will be consumed.": "将消耗魔法值。",
    "MP and amulet will be consumed.": "将消耗魔法值和护身符。",
    "These are the items still available to purchase back.": "以下是仍可回购的物品。",
    "Sorry you don't meet the requirements to store your pets!": "抱歉，你不符合寄存宠物的条件！",
    "You need to bring me your token!": "你需要把凭证带给我！",
    "You haven't beat them yet.": "你还没有击败它们。",
    "Before deafeating the monsters,": "在击败这些怪物之前，",
    "You cannot proceed to the next stage.": "你无法进入下一阶段。",
    "You defeated them all already.": "你已经把它们全部击败了。",
    "But there are still more to go.": "但后面还有更多挑战。",
    "Will you continue the challenge?": "你要继续挑战吗？",
    "I've had enough. Let me go.": "我受够了，让我离开吧。",
    "What Item would you like to buy or sell?": "你想买卖什么物品？",
    "What item would you like to buy or sell?": "你想买卖什么物品？",
    "What item do you want to store or withdraw?": "你想存入或取出什么物品？",
    "Hello traveller, How may I help you?": "你好，旅行者。需要什么帮助？",
    "Hello traveller, how may I help you?": "你好，旅行者。需要什么帮助？",
    "Hello traveller. How can i help you?": "你好，旅行者。需要什么帮助？",
    "Hello Traveller. What can I do for you?": "你好，旅行者。需要什么帮助？",
    "Welcome, What can I do for you?": "欢迎光临，需要什么帮助？",
    "Welcome. What can I do for you?": "欢迎光临，需要什么帮助？",
    "Welcome, what can I do for you?": "欢迎光临，需要什么帮助？",
    "Welcome. Thanks for dropping in.": "欢迎光临。",
    "How can I be of assistance.": "有什么可以帮你的吗？",
    "How can I help you?": "有什么可以帮你的吗？",
    "What pets would you like to purchase?": "你想购买哪只宠物？",
    "What pets would you like to purchase?  ": "你想购买哪只宠物？  ",
    "Show me the weapon that needs it.": "把需要修理的武器给我看看。",
    "Would you like to repair a drapery piece?": "你想修理衣物吗？",
    "What kind of Poison would you like to purchase?": "你想购买哪种毒药？",
    "What Posion's do you wish to buy?": "你想购买哪种毒药？",
    "You can repair various kinds of Jewellery.": "这里可以修理各种首饰。",
    "You can repair various kinds of Bracelets and Gloves.": "这里可以修理各种手镯和手套。",
    "What would you like to sell?": "你想出售什么？",
    "What would like to purchase?": "你想购买什么？",
    "What would you like to purchase?": "你想购买什么？",
    "How can I help you?": "有什么可以帮你的吗？",
    "Come back only if u do have one.": "等你拿到后再回来吧。",
    "You Dont Have enough {金币/Gold} to use my Service!": "你的{金币/Gold}不足，无法使用这项服务！",
    "Be gone, don't waste my time again!": "走开，别再浪费我的时间！",
    "I will buy high quality for high price.": "品质越好的肉，我给的价格越高。",
    "but if the meat is stained with soil or burned with fire": "但如果肉沾了泥土或被火烧焦，",
    "I'll buy it at low price.": "我只能低价收购。",
    "What would you like to sell?": "你想出售什么？",
    "What would like to purchase?": "你想购买什么？",
    "Skill will be ineffective at higher level enemy.": "面对等级更高的敌人时，技能效果会降低。",
    "Once the skill has been used, you will have to wait to use it again.": "技能使用后需要等待一段时间才能再次施放。",
    "Defense power and duration time will depend on the skill level.": "防御效果和持续时间取决于技能等级。",
    "The damage inflicted raises with the skill level.": "造成的伤害会随技能等级提高。",
    "Its duration will be extended according to practice levels and SC power.": "持续时间会随技能等级和道术力提高。",
    "I am craft lady, specialized in the creation of new items.": "我是制作师，专门打造各种新物品。",
    "If you also want anything to be made, you may as well ask me.": "如果你想制作什么物品，尽管来找我。",
    "Please select the Recipe you either want to Buy or Sell.": "请选择你想购买或出售的配方。",
    "Just pay the fee then ill escort you to anywhere.": "只要支付费用，我就能护送你前往目的地。",
    "You can not see anything.": "你什么也看不见。",
    "you can not see anything.": "你什么也看不见。",
    "from monsters only": "只能从怪物身上获得。",
    "drop sometimes.": "有时会掉落。",
    "Quick Entry to:": "快速前往：",
    "Rental days remaining:": "剩余租期：",
    "Total summon": "全部召唤",
    "Individual summon": "单独召唤",
    "Extend": "续租",
    "Summon": "召唤",
    "Apply or Cancel": "申请或取消",
    "Extend rental period": "延长租期",
    "Cancel sale of Guild territory": "取消出售行会领地",
    "Apply for sale of Guild territory": "申请出售行会领地",
    "Look at": "查看",
    "Skip": "跳过",
}


EXACT_LINE = {
    "Heal several players at once 3X3 square max. 9 people.": "可同时治疗 3X3 范围内最多 9 名玩家。",
    "(Fundraising Campaign/http://www.lost-hours.co.uk/fundraising/luanthompson2022?fbclid=IwAR3i4257oBmQtlvLksYhwSS3kJiEn8JjZyZaaEpB29c75DyYDiNeveHou2Q)":
        "(募捐活动/http://www.lost-hours.co.uk/fundraising/luanthompson2022?fbclid=IwAR3i4257oBmQtlvLksYhwSS3kJiEn8JjZyZaaEpB29c75DyYDiNeveHou2Q)",
    "(Title For Link/https://trello.com/c/9xZB8U2c/162-viperpath-cave)":
        "(链接标题/https://trello.com/c/9xZB8U2c/162-viperpath-cave)",
    "Meat can be gained from {Hens/Crimson}, {Deer/Crimson}, {Sheep/Crimson}, and {Wolves/Crimson}.":
        "可从{鸡/Crimson}、{鹿/Crimson}、{羊/Crimson}和{狼/Crimson}身上取得肉。",
    "Meat can be gained from {Hens/Crimson}, Deer/Crimson}, {Sheep/Crimson}, and {Wolves/Crimson}.":
        "可从{鸡/Crimson}、{鹿/Crimson}、{羊/Crimson}和{狼/Crimson}身上取得肉。",
}


PHRASES = {
    "Go to Main": "返回主菜单",
    "Main Menu": "主菜单",
    "Main": "主菜单",
    "Young traveller": "年轻的旅行者",
    "young traveller": "年轻的旅行者",
    "Hello traveller": "你好，旅行者",
    "Hello Traveller": "你好，旅行者",
    "Welcome to the Mir world": "欢迎来到传奇世界",
    "Mir world": "传奇世界",
    "Mir continent": "传奇大陆",
    "Bichon Province": "比奇省",
    "Bichon-Province": "比奇省",
    "BichonProvince": "比奇省",
    "Bichon Wall": "比奇城",
    "BichonWall": "比奇城",
    "Border Village": "边境村",
    "Mongchon Province": "盟重省",
    "Mongchon-Province": "盟重省",
    "MongchonProvince": "盟重省",
    "Mud Wall": "盟重土城",
    "MudWall": "盟重土城",
    "Serpent Valley": "蛇谷",
    "SerpentValley": "蛇谷",
    "Tao Village": "道馆村",
    "TaoVillage": "道馆村",
    "Woomyon Woods": "沃玛森林",
    "WoomyonWoods": "沃玛森林",
    "Prajna Island": "潘夜岛",
    "PrajnaIsland": "潘夜岛",
    "Past Bichon": "古代比奇",
    "PastBichon": "古代比奇",
    "Guild Territory": "行会领地",
    "Guild territory": "行会领地",
    "guild territory": "行会领地",
    "Guild Territory Bulletin Board": "行会领地公告板",
    "TownTeleport": "回城卷",
    "Townteleport": "回城卷",
    "Dungeonescape": "地牢逃脱卷",
    "DungeonEscape": "地牢逃脱卷",
    "RandomTeleport": "随机传送卷",
    "Randomteleport": "随机传送卷",
    "RepairOil": "修复油",
    "Candles": "蜡烛",
    "Candle": "蜡烛",
    "Warrior": "战士",
    "Wizard": "法师",
    "Taoist": "道士",
    "Assassin": "刺客",
    "Archer": "弓箭手",
    "experience": "经验",
    "Experience": "经验",
    "gold": "金币",
    "Gold": "金币",
    "level": "等级",
    "Level": "等级",
    "item": "物品",
    "Item": "物品",
    "monster": "怪物",
    "Monster": "怪物",
    "monsters": "怪物",
    "Monsters": "怪物",
    "skill": "技能",
    "Skill": "技能",
    "skills": "技能",
    "Skills": "技能",
    "pet": "宠物",
    "pets": "宠物",
    "Pets": "宠物",
    "right click": "鼠标右键",
    "left clicking": "点击鼠标左键",
    "ALT button": "Alt 键",
}


MARKUP_RE = re.compile(
    r"(?P<button><+(?P<button_text>[^<>]*?)/(?P<button_target>[@A-Za-z_][^<>]*?)>+)"
    r"|(?P<color>\{(?P<color_text>[^{}]*?)/(?P<color_name>[A-Za-z]+)\})"
    r"|(?P<angle_var><\$[^>]+>)|(?P<brace_var>\{\$[^}]+\})|(?P<var>\$[A-Za-z_]\w*)"
)
LATIN_RE = re.compile(r"[A-Za-z]")
ALLOWED_ENGLISH_RE = re.compile(r"\b(?:PK|HP|MP|EXP|GM|NPC|DC|MC|SC|Alt|Ctrl|F\d+)\b", re.IGNORECASE)
INLINE_COLOR_RE = re.compile(
    r"(?<=/)(?:Aqua|Black|Blue|Brown|Coral|Crimson|Cyan|DarkBlue|DarkGray|DarkGreen|"
    r"DarkMagenta|DarkOrange|DarkRed|DarkViolet|DeepSkyBlue|DodgerBlue|Gold|Gray|Green|"
    r"GreenYellow|Khai|Khaki|LimeGreen|LightBlue|LightGray|LightGreen|LightPink|LightSalmon|"
    r"LightSeaGreen|LightSkyBlue|LightSteelBlue|Magenta|Orange|Pink|Purple|Red|RoyalBlue|"
    r"SeaGreen|Silver|Snow|SteelBlue|Violet|White|Yellow)(?=[},>]|$)",
    re.IGNORECASE,
)
INLINE_COMPOSITE_RE = re.compile(r"\b\d+[A-Za-z]+\d*\b")
INLINE_URL_RE = re.compile(r"https?://[^\s)>）]+")
LEVEL_SKILL_RE = re.compile(r"^等级(?P<level>\d+):\s*(?P<skill>[A-Za-z][A-Za-z0-9]*)$")
PROCEED_RE = re.compile(r"^<Proceed\./(?P<target>@start\d+)>$")
SKILL_LINK_RE = re.compile(r"<(?P<display>[^<>/]+?)/(?P<target>@[^<>]+)>")
SKILL_DISPLAY_RE = re.compile(r"^(?P<name>[A-Za-z][A-Za-z0-9]*)\s+(?P<level>\d+)$")


def translate_line(source: str) -> str:
    leading = source[: len(source) - len(source.lstrip())]
    trailing = source[len(source.rstrip()) :]
    stripped = source.strip()
    translation = MANUAL_TRANSLATIONS.get(stripped)
    if translation is None:
        translation = EXACT_LINE.get(stripped)
    if translation is None:
        translation = EXACT_PLAIN.get(stripped)
    if translation is None:
        match = LEVEL_SKILL_RE.fullmatch(stripped)
        if match:
            skill_name = SKILL_NAMES.get(match.group("skill"))
            if skill_name:
                translation = f"等级{match.group('level')}: {skill_name}"
    if translation is None:
        match = PROCEED_RE.fullmatch(stripped)
        if match:
            translation = f"<继续。/{match.group('target')}>"
    if translation is None and "<" in stripped:
        matches = list(SKILL_LINK_RE.finditer(stripped))
        if matches:
            cursor = 0
            parts: list[str] = []
            valid = True
            for match in matches:
                parts.append(stripped[cursor : match.start()])
                display = match.group("display").strip()
                skill_match = SKILL_DISPLAY_RE.fullmatch(display)
                if skill_match:
                    skill_name = SKILL_NAMES.get(skill_match.group("name"))
                    if skill_name:
                        display = f"{skill_name} {skill_match.group('level')}"
                    else:
                        valid = False
                else:
                    valid = False
                parts.append(f"<{display}/{match.group('target')}>" if valid else match.group(0))
                cursor = match.end()
            parts.append(stripped[cursor:])
            candidate = "".join(parts)
            visible_remainder = SKILL_LINK_RE.sub("", candidate)
            if valid and not LATIN_RE.search(visible_remainder):
                translation = candidate
    if translation is None:
        return ""
    return leading + translation + trailing


def main() -> int:
    parser = argparse.ArgumentParser(description="Fill Crystal NPC and quest script translations")
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--database-translations", type=Path, default=DEFAULT_DATABASE_TRANSLATIONS)
    args = parser.parse_args()

    document = json.loads(args.input.read_text(encoding="utf-8-sig"))
    cache: dict[str, str] = {}

    for entry in document["entries"]:
        cache_key = entry["source"]
        translation = cache.get(cache_key)
        if translation is None:
            translation = translate_line(entry["source"])
            cache[cache_key] = translation
        entry["translation"] = translation

    output = args.output or args.input
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    translated = sum(1 for entry in document["entries"] if entry["translation"])
    print(f"Filled {translated}/{len(document['entries'])} entries ({len(cache)} unique source/kind pairs).")
    print(f"Output: {output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
