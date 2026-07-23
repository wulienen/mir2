#!/usr/bin/env python3
"""Fill the database translation template with Legend of Mir terminology.

The database keeps English lookup names in ``Name``. This script only fills
the exported player-facing ``translation`` values; the C# importer writes
them to the separate display-name fields.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


TOKEN_MAP = {
    "accessory": "首饰", "accuracy": "准确", "acid": "酸液", "adventurer": "冒险者", "administrator": "管理员",
    "alchemist": "炼金术士", "apprentice": "学徒", "assistant": "助手", "blacksmith": "铁匠", "board": "公告牌",
    "bulletin": "公告", "craftslady": "工匠", "dealer": "商人", "excavenger": "挖掘者", "examiner": "考官",
    "escort": "护卫", "fisherman": "渔夫", "fishing": "钓鱼", "grocery": "杂货", "gt": "领地", "gtmerchant": "领地商人",
    "gtstore": "领地商店", "gttransport": "领地传送员", "gttransporter": "领地传送员", "hairdresser": "理发师",
    "high": "高级", "inn": "旅店", "keeper": "老板", "librarian": "图书管理员", "lottery": "彩票商人",
    "manager": "主管", "merchant": "商人", "mirguide": "传奇向导", "mongchon": "盟重", "monger": "商贩",
    "officer": "军官", "peddler": "小贩", "pet": "宠物", "potion": "药剂", "priest": "祭司", "proceeder": "执行者",
    "protector": "守护者", "quest": "任务", "sailor": "水手", "scout": "斥候", "signpost": "路标", "solider": "士兵",
    "soldier": "士兵", "specialist": "专家", "stable": "马厩", "steward": "管家", "storage": "仓库管理员",
    "subjugation": "讨伐", "subjagation": "讨伐", "squad": "小队", "teleporter": "传送员", "thewatcher": "观察者",
    "trainer": "训练师", "travelling": "游历", "transport": "运输", "trust": "信任商人", "villagechief": "村长",
    "wanderer": "流浪者", "warehouse": "仓库", "weapon": "武器", "wise": "睿智", "wicked": "邪恶",
    "agility": "敏捷", "aid": "援助", "alchemy": "炼金", "algae": "海藻",
    "ancient": "远古", "angel": "天使", "animal": "野兽", "ape": "猿猴",
    "arch": "拱门", "archer": "弓箭手", "armour": "铠甲", "armor": "护甲",
    "arrow": "箭矢", "assassin": "刺客", "attack": "攻击", "awakening": "觉醒",
    "axe": "斧", "back": "后退", "bamboo": "竹", "bandit": "强盗", "barbarian": "蛮族",
    "base": "基础", "battle": "战斗", "bat": "蝙蝠", "bead": "珠子", "beast": "野兽",
    "beef": "牛肉", "belt": "腰带", "bell": "铃铛", "big": "巨型", "bichon": "比奇",
    "blade": "刀刃", "blades": "双刃", "blaster": "爆裂", "blessed": "神圣",
    "blink": "瞬移", "blood": "鲜血", "blue": "蓝色", "boar": "野猪", "book": "书籍",
    "boots": "靴子", "border": "边境", "boss": "首领", "bossy": "霸道", "bow": "弓",
    "bracelet": "手镯", "bravery": "勇气", "bronze": "青铜", "brown": "棕色",
    "bug": "虫", "bulletin": "公告", "butcher": "屠夫", "cannibal": "食人花",
    "captain": "队长", "carnage": "屠戮", "carnation": "康乃馨", "cat": "猫",
    "cave": "洞穴", "centipede": "蜈蚣", "chaos": "混沌", "charm": "魅惑", "cherry": "樱桃",
    "chieftain": "酋长", "chicken": "鸡", "chick": "小鸡", "circle": "环", "claw": "利爪",
    "clean": "清理", "cloud": "云", "club": "棍", "cold": "寒冰", "collector": "收藏家",
    "commander": "指挥官", "concentration": "集中", "contest": "争夺", "copper": "铜",
    "coral": "珊瑚", "corps": "尸花", "courage": "勇气", "craft": "制作", "crafting": "工匠",
    "crane": "仙鹤", "creature": "生物", "credit": "点券", "cripple": "残废", "cross": "十字",
    "crossbow": "弩", "crystal": "水晶", "cursed": "诅咒", "dark": "黑暗", "darkness": "黑暗",
    "dash": "冲锋", "death": "死亡", "deer": "鹿", "defence": "防御", "defensive": "防御",
    "delayed": "延迟", "demon": "恶魔", "destruction": "破坏", "devil": "恶魔",
    "diana": "黛安娜", "double": "双重", "dragon": "龙", "drama": "戏剧", "drapery": "裁缝",
    "drug": "药品", "dust": "尘埃", "eagle": "鹰", "ebony": "乌木", "electric": "雷电",
    "element": "元素", "elixir": "灵药", "elite": "精英", "emperor": "皇帝", "energy": "能量",
    "entrance": "入口", "escape": "逃脱", "explosive": "爆裂", "experience": "经验",
    "eye": "眼", "fairy": "仙女", "fatal": "致命", "fighter": "战士", "fighting": "战斗",
    "fire": "火焰", "fisher": "渔夫", "fishing": "钓鱼", "five": "五", "flame": "烈焰",
    "flaming": "烈焰", "flash": "闪电", "flower": "花", "focus": "专注", "formal": "正式",
    "fox": "狐狸", "frozen": "冰冻", "fruit": "果实", "frost": "冰霜", "frog": "青蛙",
    "fury": "狂怒", "gale": "狂风", "gem": "宝石", "general": "将军", "giant": "巨人",
    "girl": "少女", "glyph": "符文", "gold": "黄金", "golden": "黄金", "gon": "昆仑",
    "gonryun": "昆仑", "gonryunyongdrama": "昆仑龙戏", "gonryunpasackle": "昆仑八卦",
    "great": "强力", "green": "绿色", "guardian": "守护者", "guard": "卫兵", "ghoul": "食尸鬼",
    "hard": "坚硬", "health": "生命", "healing": "治愈", "heaven": "天", "helmet": "头盔",
    "hell": "地狱", "hidden": "隐藏", "holy": "神圣", "hog": "野猪", "horn": "号角",
    "horned": "角魔", "hwan": "幻魔", "ice": "寒冰", "impact": "冲击", "insect": "昆虫",
    "iron": "铁", "jade": "翡翠", "jar": "罐", "jewel": "宝石", "knowledge": "知识",
    "king": "王", "knapsack": "背包", "knight": "骑士", "large": "大型", "leather": "皮革",
    "left": "左", "leg": "腿", "light": "光明", "lightning": "雷电", "lotus": "莲花",
    "lord": "领主", "lure": "鱼饵", "magic": "魔法", "magical": "魔法", "mammoth": "猛犸",
    "man": "男子", "manectric": "电击兽", "manworm": "人虫", "marble": "大理石", "marshmallow": "棉花糖",
    "martial": "武术", "material": "材料", "meat": "肉", "medium": "中型", "mental": "精神",
    "mctorch": "魔法火炬", "medicine": "药剂", "minion": "仆从", "minotaur": "牛魔王",
    "mir": "传奇", "mirror": "镜像", "mist": "雾", "mob": "怪物", "monk": "武僧",
    "monster": "怪物", "moon": "月", "moth": "飞蛾", "mpeater": "吸魔", "mp": "魔法",
    "mud": "盟重", "mushroom": "蘑菇", "mysterious": "神秘", "mystery": "谜团",
    "naga": "娜迦", "necklace": "项链", "nephrite": "碧玉", "ninja": "忍者", "node": "节点",
    "nok": "绿", "nokyoungokhwan": "绿灵玉环", "ore": "矿石", "orb": "宝珠", "orchid": "兰花",
    "over": "上层", "palace": "宫殿", "pass": "通道", "pendant": "吊坠", "pearl": "珍珠",
    "phoenix": "凤凰", "pick": "镐", "pill": "丹药", "pirate": "海盗", "pig": "猪",
    "plague": "瘟疫", "platinum": "白金", "poison": "毒", "potion": "药水", "power": "力量",
    "premium": "尊贵", "protection": "护盾", "purified": "净化", "purity": "纯净", "purple": "紫色",
    "queen": "女王", "rage": "狂暴", "rat": "老鼠", "raider": "掠夺者", "raiders": "掠夺者",
    "rare": "稀有", "recall": "回城", "relic": "遗物", "repair": "修理", "resurrection": "复活",
    "red": "红色", "reincarnation": "转生", "ring": "戒指", "robe": "长袍", "rock": "岩石",
    "royal": "王室", "ruby": "红宝石", "sabre": "军刀", "sand": "沙", "scorpion": "蝎子",
    "scroll": "卷轴", "seeker": "探寻者", "serpent": "蛇", "shield": "盾牌", "shinsu": "神兽",
    "short": "短", "shot": "射击", "silver": "白银", "skeleton": "骷髅", "skull": "头骨",
    "slayer": "屠龙者", "slash": "斩击", "small": "小型", "smash": "重击", "snake": "蛇",
    "snow": "冰雪", "soul": "灵魂", "sorcery": "法术", "spear": "长矛", "spearman": "枪兵",
    "spider": "蜘蛛", "spirit": "灵魂", "staff": "法杖", "stamina": "体力", "statue": "雕像",
    "steel": "钢铁", "stone": "石头", "storm": "风暴", "strength": "力量", "strong": "强效",
    "summon": "召唤", "sword": "剑", "tales": "传说", "tao": "道", "taoist": "道士",
    "tarragon": "龙蒿", "teleport": "传送", "teeth": "獠牙", "thunder": "雷霆", "tiger": "猛虎",
    "titan": "泰坦", "token": "令牌", "torch": "火炬", "trap": "陷阱", "tree": "树",
    "trident": "三叉戟", "troll": "巨魔", "tucson": "土城", "turtle": "乌龟", "twin": "双生",
    "undead": "亡灵", "unstable": "不稳定", "vampire": "吸血鬼", "venom": "剧毒", "violet": "紫罗兰",
    "war": "战争", "warrior": "战士", "water": "水", "web": "蛛网", "white": "白色",
    "wicked": "邪恶", "wind": "风", "winged": "翼", "wizard": "法师", "wiz": "法师",
    "wolf": "狼", "wooma": "沃玛", "worm": "蠕虫", "wooden": "木制", "worn": "破旧",
    "wuma": "沃玛", "yellow": "黄色", "yeti": "雪人", "yeongokhwan": "灵玉环", "yeoseonru": "女仙楼",
    "yizhili": "伊芝莉", "yama": "夜叉", "yang": "杨", "zombie": "僵尸", "zuma": "祖玛",
    "xl": "特大", "hp": "生命", "mc": "魔法", "sc": "道术", "dc": "攻击", "h": "圣",
    "l": "低阶", "s": "小", "m": "男", "f": "女", "nd": "无", "of": "之", "the": "",
    "with": "与", "and": "和", "in": "在", "to": "至", "from": "来自", "one": "一",
    "two": "二", "three": "三", "four": "四", "none": "无", "nonee": "无",
}

TOKEN_MAP.update({
    "black": "黑色", "oma": "半兽人", "evil": "邪恶", "mine": "矿洞", "bone": "白骨", "maze": "迷宫",
    "temple": "寺庙", "store": "商店", "dy": "神龙", "tactical": "战术", "prajna": "潘夜", "gm": "管理员",
    "cavern": "洞窟", "valley": "山谷", "viper": "毒蛇", "stairs": "楼梯", "path": "道路", "agony": "痛苦",
    "ryun": "仑", "he": "鹤", "publication": "典籍", "eunhyung": "银形", "sanggwan": "商馆", "wine": "酒",
    "pearll": "珍珠", "cheongbo": "青宝", "egg": "蛋", "glove": "手套", "sky": "天空", "house": "房屋",
    "connection": "连接", "tomb": "石墓", "wheel": "轮", "old": "老", "village": "村庄", "lost": "失落",
    "b": "地下", "ball": "球", "liquor": "烈酒", "wonder": "奇迹", "dead": "死亡", "damian": "达米安",
    "private": "私人", "exp": "经验", "portal": "传送门", "d": "龙", "mira": "米拉", "transporter": "传送员",
    "room": "房间", "court": "庭院", "for": "", "amulet": "护身符", "brace": "护腕", "dungeon": "地下城",
    "box": "宝箱", "body": "身体", "solid": "坚固", "master": "大师", "lady": "女士", "mage": "法师",
    "mask": "面具", "chestnut": "栗子", "doom": "末日", "forest": "森林", "tongs": "铁钳", "fof": "层",
    "angled": "转角", "lobby": "大厅", "dog": "狗", "yo": "妖", "hammer": "锤", "sharp": "锋利",
    "shaman": "萨满", "tea": "茶", "maggot": "蛆虫", "heart": "心脏", "zombies": "僵尸", "taurus": "教主",
    "crafts": "工匠", "hunt": "狩猎", "test": "试炼", "hoa": "花", "tempered": "淬炼", "r": "红",
    "lunar": "月宫", "collar": "项圈", "skill": "技能", "oil": "油", "baby": "幼崽", "footballer": "球员",
    "secret": "秘密", "bless": "祝福", "hwayoung": "花影", "okhwan": "玉环", "kek": "客", "tal": "符",
    "weaver": "织工", "prison": "监狱", "meet": "会见", "burst": "爆发", "tough": "坚韧", "hero": "英雄",
    "dumpling": "饺子", "leopard": "豹", "a": "", "bangle": "手镯", "keratoid": "角质", "toxic": "剧毒",
    "guide": "向导", "pillar": "石柱", "reagent": "药材", "natural": "天然", "hall": "大厅", "passage": "通道",
    "mineral": "矿物", "request": "请求", "instructor": "导师", "help": "援助", "find": "寻找", "string": "弦",
    "dagger": "匕首", "ma": "魔", "rod": "钓竿", "fish": "鱼", "time": "时间", "kitten": "幼猫",
    "half": "半月", "field": "领域", "meteor": "流星", "hiding": "隐身", "mass": "群体", "elemental": "元素",
    "canni": "食人花", "gathering": "收集", "underpants": "内甲", "hyeoncheon": "玄天", "horo": "葫芦",
    "bomb": "炸弹", "blademan": "刀兵", "strange": "奇异", "bones": "骨堆", "chief": "首领",
    "woomyon": "沃玛", "woods": "森林", "item": "物品", "n": "北", "w": "西", "swamp": "沼泽",
    "west": "西部", "gorge": "峡谷", "east": "东部", "deliver": "递送", "friend": "朋友", "sparkling": "闪耀",
    "assa": "刺客", "god": "神", "spring": "泉", "fan": "扇", "freezing": "冰冻", "witch": "女巫",
    "survival": "生存", "sacred": "神圣", "lantern": "灯笼", "amethyst": "紫水晶", "kings": "国王",
    "ginseng": "人参", "broth": "汤", "greater": "强效", "ribbon": "丝带", "stinger": "毒刺", "shell": "甲壳",
    "thread": "丝线", "antidote": "解毒剂", "fencing": "剑术", "drake": "龙", "bolt": "雷击", "enhancer": "强化",
    "deva": "圣兽", "eater": "吞噬", "toad": "蛤蟆", "cook": "厨师", "report": "报告", "head": "头颅",
    "suit": "套装", "fine": "精制", "octagonal": "八角", "suho": "守护", "bull": "牛", "scarecrow": "稻草人",
    "crawler": "爬行者", "gang": "帮众", "ronin": "浪人", "right": "右", "hugger": "抱怪", "sabuk": "沙巴克",
    "smith": "铁匠", "carriage": "马车", "miss": "小姐", "library": "图书馆", "tavern": "酒馆", "academy": "学院",
    "castle": "城堡", "dangerous": "危险", "ruins": "遗迹", "infinite": "无尽", "bout": "战斗", "ingredients": "材料",
    "delivery": "递送", "mundane": "凡俗", "chi": "气", "protect": "护身", "hooked": "钩形", "judgement": "审判",
    "mace": "钉锤", "raw": "粗制", "scimitar": "弯刀", "kriss": "波纹剑", "wand": "魔杖", "carved": "雕纹",
    "long": "长", "fiend": "邪魔", "malefic": "邪术", "force": "力量", "dress": "礼服", "heavy": "重型",
    "studded": "镶钉", "stealth": "隐秘", "scaled": "鳞甲", "revival": "复活", "moral": "道德", "expel": "驱邪",
    "noble": "贵族", "seal": "封印", "gauntlet": "护手", "precision": "精准", "amber": "琥珀", "convex": "凸面",
    "lens": "镜片", "life": "生命", "adamantine": "金刚", "coronet": "冠", "shadow": "暗影", "chain": "锁链",
    "silk": "丝绸", "scale": "鳞片", "sun": "太阳", "soup": "汤", "bundle": "包裹", "town": "城镇",
    "durability": "持久", "disillusion": "破幻", "endurance": "耐力", "sewing": "缝纫", "goods": "货物",
    "bridle": "缰绳", "hook": "鱼钩", "float": "浮漂", "bait": "鱼饵", "reel": "鱼线轮", "gobby": "哥布林",
    "bookof": "技能书", "tool": "工具", "bar": "锭", "chip": "碎片", "letter": "信件", "baboon": "狒狒",
    "rhino": "犀牛", "baekdon": "白豚", "wimaen": "威猛", "olympic": "奥林匹克", "drop": "掉落",
    "slaying": "攻杀", "thrusting": "刺杀", "shoulder": "野蛮", "entrapment": "困魔", "lion": "狮子",
    "roar": "吼", "avalanche": "风暴", "counter": "反击", "slashing": "斩击", "immortal": "不灭", "skin": "护体",
    "shock": "电击", "bang": "爆裂", "crunch": "咆哮", "turn": "诱惑", "vampirism": "吸血", "disruptor": "破坏",
    "mirroring": "镜像", "blizzard": "暴风雪", "booster": "增幅", "strike": "打击", "thrust": "突刺",
    "poisoning": "施毒", "revelation": "启示", "repulsor": "气功", "hexagon": "六芒", "purification": "净化",
    "hallucination": "幻觉", "ultimate": "终极", "curse": "诅咒", "haste": "加速", "heavenly": "天界",
    "swift": "迅捷", "feet": "步", "hemorrhage": "流血", "cresent": "新月", "straight": "直射", "state": "状态",
    "meditation": "冥想", "step": "步", "explosion": "爆炸", "barrier": "屏障", "binding": "束缚", "snakes": "蛇群",
    "napalm": "燃烧弹", "nature": "自然", "recipe": "配方", "relics": "遗物", "wings": "翅膀", "stolen": "被盗",
    "trainee": "学徒", "rib": "肋骨", "tooth": "牙齿", "broken": "破损", "superior": "上等", "thick": "厚重",
    "ying": "鹰", "pk": "PK", "banga": "邦加", "sealed": "封印", "shower": "雨", "cry": "怒吼",
    "knife": "短刀", "bayonet": "刺刀", "tattoo": "纹身", "pink": "粉色", "rabbit": "兔子", "essence": "精华",
    "training": "训练", "clone": "分身", "spitting": "喷吐", "yob": "恶棍", "hooking": "钩击", "raking": "耙击",
    "nipper": "钳虫", "visceral": "内脏", "rot": "腐烂", "yimoogi": "蛟龙", "whimpering": "哀嚎",
    "bee": "蜜蜂", "wedge": "楔蛾", "hedge": "刺藤", "dung": "粪虫", "incarnated": "化身", "hungry": "饥饿",
    "shi": "尸", "vale": "谷", "flail": "链枷", "slave": "奴隶", "mutated": "变异", "devourer": "吞噬者",
    "keel": "龙骨", "warlock": "术士", "rouge": "盗贼", "meow": "喵", "armadillo": "犰狳", "miner": "矿工",
    "bloody": "血腥", "game": "游戏", "shop": "商城", "gate": "城门", "province": "省", "ground": "场地",
    "inner": "内城", "jin": "阵", "hill": "山", "rocks": "岩石", "insomnia": "失眠", "supplies": "补给",
    "mines": "矿洞", "danger": "危险", "finding": "寻找", "remains": "遗骸", "research": "研究", "news": "消息",
    "souls": "灵魂", "save": "拯救", "antique": "古董", "mobs": "怪物",
})

TOKEN_MAP.update({
    "shoes": "鞋", "wall": "城墙", "repulsion": "抗拒", "cl": "克莱", "jerald": "杰拉德", "bill": "比尔",
    "sir": "爵士", "jake": "杰克", "jang": "张", "lead": "首席", "dr": "博士", "pile": "堆", "mr": "先生",
    "inspector": "巡查员", "e": "东", "kr": "韩", "on": "上", "pendulum": "摆锤", "decapitator": "斩首刀",
    "hood": "兜帽", "prince": "王子", "purifier": "净化", "stealer": "夺取", "scythe": "镰刀", "conqueror": "征服者",
    "bastard": "重型", "velocity": "疾速", "magi": "贤者", "stiletto": "短剑", "zinc": "精钢", "butchering": "屠宰",
    "ripper": "撕裂者", "mad": "狂龙", "compound": "复合", "lithe": "轻灵", "dread": "恐惧", "lethal": "致命",
    "apus": "天鹰", "thirsty": "嗜血", "paralysis": "麻痹", "clear": "清心", "muscle": "力量", "recovery": "恢复",
    "hardened": "坚韧", "hexagonal": "六角", "glass": "琉璃", "boundless": "无极", "tae": "太", "guk": "极",
    "pledge": "誓约", "crimson": "赤红", "taji": "太极", "thin": "轻盈", "evade": "闪避", "strain": "坚韧",
    "rd": "赤龙", "spell": "法术", "bracer": "护腕", "trigram": "八卦", "hang": "降", "bok": "伏", "baek": "白",
    "ta": "打", "reformer": "重塑", "dual": "双生", "whisp": "鬼火", "wild": "狂野", "enchanted": "附魔",
    "elusion": "幻影", "pipe": "烟斗", "demonic": "魔神", "bells": "铃", "requiem": "镇魂", "cuspid": "尖牙",
    "anchor": "锚", "adamant": "金刚", "triangle": "三角", "kunroon": "昆仑", "tear": "泪", "probe": "探测",
    "brass": "黄铜", "tiara": "宝冠", "wisdom": "智慧", "grasp": "之握", "rotten": "腐朽", "low": "轻便",
    "pattern": "纹饰", "apple": "苹果", "spencer": "斯宾塞", "herbal": "草药", "zerk": "狂暴", "benediction": "祝福",
    "invitation": "邀请函", "random": "随机", "home": "家园", "candle": "蜡烛", "enternal": "永恒", "bengal": "孟加拉",
    "panthera": "黑豹", "saddle": "马鞍", "titanium": "钛金", "lean": "轻型", "detector": "探测器", "finder": "探鱼器",
    "walleye": "大眼鱼", "bass": "鲈鱼", "trout": "鳟鱼", "tinker": "丁鲷", "mackeral": "鲭鱼", "acc": "准确",
    "rusty": "生锈", "translucent": "半透明", "havoc": "浩劫", "mana": "魔力", "cast": "施法", "mossy": "苔痕",
    "rope": "绳索", "adamintine": "金刚", "emerald": "祖母绿", "olivine": "橄榄石", "stamp": "印章", "chest": "宝箱",
    "gamble": "赌注", "dice": "骰子", "unknown": "未知", "admission": "入场", "piece": "碎片", "pork": "猪肉",
    "venison": "鹿肉", "mutton": "羊肉", "feather": "羽毛", "timber": "木材", "leaf": "树叶", "tail": "尾巴",
    "mandible": "颚骨", "evasion": "闪避", "will": "意志", "ear": "耳朵", "hoof": "蹄子", "nuts": "坚果",
    "moss": "苔藓", "freshwater": "淡水", "clam": "蛤蜊", "mackerel": "鲭鱼", "cherries": "樱桃", "ac": "防御",
    "mac": "魔防", "speed": "速度", "fast": "快速", "move": "移动", "dolly": "多莉", "santa": "圣诞老人",
    "rudolph": "鲁道夫", "mutant": "变异", "ram": "公羊", "leaves": "叶子", "ginger": "姜", "stem": "茎",
    "sack": "袋", "olivias": "奥利维亚的", "seed": "种子", "blu": "蓝色", "herb": "草药", "skys": "天空的",
    "woomas": "沃玛的", "specimen": "标本", "history": "历史", "stiff": "坚硬", "loafer": "便鞋", "elk": "麋鹿",
    "buckle": "带扣", "beadof": "宝珠", "strap": "绑带", "eyeof": "之眼", "keen": "锐利", "crow": "乌鸦",
    "swords": "剑", "flute": "笛子", "exorcist": "驱魔", "point": "点数", "reduction": "削减", "rental": "租赁",
    "sex": "性别", "change": "变更", "extra": "额外", "physical": "物理", "scyther": "镰魔", "boxof": "宝箱",
    "might": "力量", "package": "礼包", "costume": "时装", "gymnastics": "体操", "ski": "滑雪", "kid": "少年",
    "sportsman": "运动员", "sportman": "运动员", "tognue": "舌", "invite": "邀请", "thorn": "荆棘", "bluish": "湛蓝",
    "slaughter": "屠戮", "pike": "长枪", "retsuen": "烈旋", "infernal": "炼狱", "maseok": "魔石",
    "redrecoverywater": "红色恢复水", "bluerecoverywater": "蓝色恢复水", "sentry": "哨兵", "familiar": "使魔",
    "totem": "图腾", "charmed": "魅惑", "hen": "母鸡", "sheep": "绵羊", "currish": "恶犬", "plant": "植物",
    "whoo": "呼啸", "hi": "高级", "treasure": "宝藏", "khazard": "卡扎德", "wt": "沃玛", "zt": "祖玛",
    "root": "树根", "grey": "灰色", "sobo": "索博", "cracking": "裂纹", "arming": "武装", "flying": "飞行",
    "stoning": "石化", "yin": "阴", "blest": "祝圣", "door": "门", "pilar": "石柱", "ghastly": "恐怖",
    "leecher": "吸血虫", "crazy": "疯狂", "cyano": "青色", "ghast": "恶灵", "dream": "梦魇", "slasher": "斩杀者",
    "doctor": "医生", "mub": "魔布", "harden": "硬化", "burning": "燃烧", "anc": "远古", "bringer": "使者",
    "hydra": "九头蛇", "manticore": "蝎狮", "lamia": "拉米亚", "bear": "熊", "grass": "草", "widow": "寡妇蛛",
    "stain": "斑纹", "stray": "流浪", "seedings": "幼苗", "restless": "躁动", "young": "幼年", "elder": "长老",
    "snail": "蜗牛", "tentacles": "触手", "golem": "魔像", "phantom": "幻影", "warewolf": "狼人", "axeman": "斧兵",
    "magician": "魔术师", "wraith": "怨灵", "sinseok": "神石", "ghost": "幽灵", "football": "足球",
    "lieutenant": "副官", "tower": "塔", "finial": "尖塔", "hydrax": "海德拉", "sorceror": "术士", "boulder": "巨石",
    "michelangelo": "米开朗基罗", "raphael": "拉斐尔", "donatello": "多纳泰罗", "sea": "海洋", "wa": "瓦",
    "vincent": "文森特", "challange": "挑战", "trader": "商人", "traveller": "旅行者", "satin": "萨廷", "mk": "明空",
    "investigator": "调查员", "crushed": "碎裂", "vulnerable": "虚弱", "son": "之子", "delegate": "代表",
    "wierd": "怪异", "ashes": "灰烬", "odd": "古怪", "jene": "珍妮", "abbigale": "阿比盖尔", "watcher": "观察者",
    "leader": "队长", "monument": "纪念碑", "do": "多", "mi": "米", "re": "蕾", "boot": "靴子", "mount": "坐骑",
    "script": "脚本", "nothing": "空置", "beauty": "美容", "saloon": "沙龙", "drapers": "裁缝", "kitchen": "厨房",
    "barracks": "兵营", "restaurant": "餐馆", "residence": "住宅", "eastof": "东部", "place": "场所", "southof": "南部",
    "gi": "蟠", "ryoong": "龙", "depository": "仓库", "purgatory": "炼狱", "sole": "孤魂", "side": "山腰",
    "seokcho": "石草", "armory": "军械库", "rev": "道长", "office": "居所", "laundry": "洗衣房", "lab": "工坊",
    "treepath": "林间小路", "nest": "巢穴", "mill": "磨坊", "coffin": "棺室", "stream": "溪流", "main": "主",
    "middle": "中部", "southern": "南部", "western": "西部", "northern": "北部", "eastern": "东部", "fatally": "致命",
    "poisonous": "剧毒", "lobbyof": "大厅", "forgotten": "遗忘", "city": "城市", "island": "岛", "shrine": "神殿",
    "past": "过去", "generals": "将军", "camp": "营地", "land": "大地", "molten": "熔岩", "waste": "荒芜",
    "lands": "之地", "overpass": "天桥", "haunted": "幽灵", "lair": "巢穴", "waiting": "等候", "penal": "刑罚",
    "archers": "弓箭手", "hideout": "藏身处", "arena": "竞技场", "hyun": "玄", "pharmacy": "药房", "med": "医疗",
    "smithy": "铁匠铺", "wearhouse": "仓库", "lift": "升降台", "enchancer": "强化", "tongue": "舌", "bounce": "反弹",
})

PROPER_MAP = {
    "albert": "阿尔伯特", "alex": "亚历克斯", "alfie": "阿尔菲", "alfred": "阿尔弗雷德",
    "alice": "爱丽丝", "amanda": "阿曼达", "andy": "安迪", "andrew": "安德鲁", "anne": "安妮",
    "anthony": "安东尼", "bart": "巴特", "belina": "贝琳娜", "betty": "贝蒂", "bradley": "布拉德利",
    "brian": "布莱恩", "brittney": "布兰妮", "bruce": "布鲁斯", "carl": "卡尔", "carlos": "卡洛斯",
    "carratt": "卡拉特", "chandler": "钱德勒", "charley": "查理", "charlotte": "夏洛特", "chris": "克里斯",
    "christine": "克里斯蒂娜", "cindy": "辛迪", "clair": "克莱尔", "clara": "克拉拉", "cloud": "云",
    "daniel": "丹尼尔", "david": "大卫", "dean": "迪恩", "debora": "黛博拉", "denzel": "丹泽尔",
    "derby": "德比", "deric": "德里克", "diana": "戴安娜", "dolby": "杜比", "don": "唐",
    "dustin": "达斯汀", "eddie": "埃迪", "edward": "爱德华", "edwin": "埃德温", "elijah": "以利亚",
    "emily": "艾米丽", "eric": "埃里克", "eugene": "尤金", "far": "法尔", "frank": "弗兰克",
    "graeme": "格雷姆", "gerald": "杰拉德", "gilbert": "吉尔伯特", "glenn": "格伦", "gordon": "戈登",
    "grace": "格蕾丝", "grim": "格林", "grimchamp": "格林查普", "hans": "汉斯", "harrison": "哈里森",
    "harry": "哈里", "harvey": "哈维", "heather": "海瑟", "helen": "海伦", "hubert": "休伯特",
    "huxley": "赫胥黎", "hwa": "华", "jack": "杰克", "jacob": "雅各布", "james": "詹姆斯",
    "jamie": "杰米", "janey": "珍妮", "jason": "杰森", "jane": "简", "jeffery": "杰弗里",
    "jennifer": "詹妮弗", "jerry": "杰瑞", "jessica": "杰西卡", "jim": "吉姆", "joffrey": "乔弗里",
    "john": "约翰", "jon": "乔恩", "joseph": "约瑟夫", "jude": "裘德", "julia": "朱莉娅", "julie": "朱莉",
    "june": "朱恩", "keith": "基思", "kelly": "凯莉", "ken": "肯", "kevin": "凯文", "kieron": "基隆",
    "kim": "金", "kimberly": "金伯莉", "kristin": "克里斯汀", "kurtis": "柯蒂斯", "kyle": "凯尔",
    "larry": "拉里", "laura": "劳拉", "louise": "路易丝", "luke": "卢克", "martin": "马丁",
    "martyn": "马丁", "mary": "玛丽", "maximus": "马克西姆斯", "melissa": "梅丽莎", "michael": "迈克尔",
    "michelle": "米歇尔", "milton": "米尔顿", "monica": "莫妮卡", "mogu": "莫古", "murray": "默里",
    "nicole": "妮可", "olivia": "奥利维亚", "paddy": "帕迪", "patrick": "帕特里克", "paula": "宝拉",
    "peggy": "佩吉", "penny": "佩妮", "perry": "佩里", "peter": "彼得", "rachel": "瑞秋",
    "raymond": "雷蒙德", "reece": "里斯", "richard": "理查德", "robert": "罗伯特", "roger": "罗杰",
    "robin": "罗宾", "ruben": "鲁本", "rupert": "鲁珀特", "sabrina": "萨布丽娜", "sam": "山姆",
    "samuel": "塞缪尔", "sandra": "桑德拉", "sarah": "莎拉", "sandford": "桑福德", "schwartz": "施瓦茨",
    "scott": "斯科特", "shok": "肖克", "sigmund": "西格蒙德", "soho": "苏荷", "stanislav": "斯坦尼斯拉夫",
    "stephen": "斯蒂芬", "steven": "史蒂文", "stuart": "斯图尔特", "susan": "苏珊", "terry": "特里",
    "thompson": "汤普森", "tiffany": "蒂芙尼", "tony": "托尼", "travis": "特拉维斯", "victoria": "维多利亚",
    "vivian": "薇薇安", "vicky": "维琪", "walter": "沃尔特", "wang": "王", "wayne": "韦恩",
    "whitney": "惠特尼", "yang": "杨", "yu": "玉", "joffrey": "乔弗里", "jane": "简",
}

EXACT_MAP = {
    "BichonProvince": "比奇省", "BichonWall": "比奇城", "BorderVillage": "边境村",
    "MudWall": "盟重土城", "SerpentValley": "蛇谷", "TaoVillage": "道馆村", "WoomyonWoods": "沃玛森林",
    "WoomaTemple": "沃玛寺庙", "CastleGi-Ryoong": "蟠龙城", "PrajnaIsland": "潘夜岛",
    "MongchonWall": "盟重城", "Gonryun": "昆仑", "GonryunPasackle": "昆仑八卦",
    "SpiritBlade": "灵魂之刃", "MirSword": "传奇之剑", "MirArmour": "传奇铠甲", "MirHelmet": "传奇头盔",
    "MirNecklace": "传奇项链", "MirBracelet": "传奇手镯", "MirRing": "传奇戒指", "MirBelt": "传奇腰带",
    "MirBoots": "传奇战靴", "TaoProtect": "道士护身", "RedOrchid": "红兰", "RedFlower": "红花",
    "HwanDevil": "幻魔", "Purity": "纯净", "FiveString": "五弦", "WhiteGold": "白金",
    "RedJade": "红玉", "Nephrite": "碧玉", "Bone": "白骨", "Tarragon": "龙蒿",
    "Raiders": "掠夺者", "Mystery": "神秘", "BootsOfStrength": "力量战靴", "BeltOfStrength": "力量腰带",
    "Fencing": "基本剑术", "Slaying": "攻杀剑术", "Thrusting": "刺杀剑术", "HalfMoon": "半月弯刀",
    "ShoulderDash": "野蛮冲撞", "TwinDrakeBlade": "烈火剑法", "Entrapment": "困魔咒", "FlamingSword": "烈火剑法",
    "LionRoar": "狮子吼", "CrossHalfMoon": "半月十字斩", "BladeAvalanche": "剑刃风暴", "ProtectionField": "护体神盾",
    "Rage": "狂暴", "CounterAttack": "反击", "SlashingBurst": "烈火剑法", "Fury": "怒气爆发",
    "ImmortalSkin": "金刚护体", "FireBall": "火球术", "Repulsion": "抗拒火环", "ElectricShock": "雷电术",
    "GreatFireBall": "大火球", "HellFire": "地狱火", "ThunderBolt": "雷电术", "Teleport": "地牢逃脱",
    "FireBang": "爆裂火焰", "FireWall": "火墙", "Lightning": "疾光电影", "FrostCrunch": "冰咆哮",
    "ThunderStorm": "冰咆哮", "MagicShield": "魔法盾", "TurnUndead": "诱惑之光", "Vampirism": "吸血术",
    "IceStorm": "寒冰掌", "FlameDisruptor": "灭天火", "Mirroring": "镜像术", "FlameField": "火焰领域",
    "Blizzard": "暴风雪", "MagicBooster": "魔法增幅", "MeteorStrike": "流星火雨", "IceThrust": "冰霜刺",
    "Blink": "瞬息移动", "StormEscape": "风暴逃脱", "Healing": "治愈术", "SpiritSword": "灵魂火符",
    "Poisoning": "施毒术", "SoulFireBall": "灵魂火符", "SummonSkeleton": "召唤骷髅", "Hiding": "隐身术",
    "MassHiding": "群体隐身术", "SoulShield": "神圣战甲术", "Revelation": "心灵启示", "BlessedArmour": "幽灵盾",
    "EnergyRepulsor": "气功波", "TrapHexagon": "困魔咒", "Purification": "净化术", "MassHealing": "群体治愈术",
    "Hallucination": "幻觉术", "UltimateEnchancer": "强化术", "SummonShinsu": "召唤神兽", "Reincarnation": "复活术",
    "SummonHolyDeva": "召唤圣兽", "Curse": "诅咒术", "Plague": "瘟疫术", "PoisonCloud": "毒云术",
    "EnergyShield": "能量盾", "PetEnhancer": "宠物强化", "HealingCircle": "治愈光环", "FatalSword": "绝命剑法",
    "DoubleSlash": "双斩", "Haste": "疾风步", "FlashDash": "闪现冲刺", "LightBody": "轻身术",
    "HeavenlySword": "天剑", "FireBurst": "火焰爆发", "Trap": "陷阱", "PoisonSword": "毒剑术",
    "MoonLight": "月光斩", "MPEater": "吸魔术", "SwiftFeet": "疾行术", "DarkBody": "暗影身法",
    "Hemorrhage": "血流术", "CresentSlash": "新月斩", "MoonMist": "月雾", "CatTongue": "猫舌术",
    "Focus": "集中术", "StraightShot": "精准射击", "DoubleShot": "双重射击", "ExplosiveTrap": "爆炸陷阱",
    "DelayedExplosion": "延迟爆炸", "Meditation": "冥想", "BackStep": "后跃", "ElementalShot": "元素射击",
    "Concentration": "集中", "StoneTrap": "石头陷阱", "ElementalBarrier": "元素屏障", "SummonVampire": "召唤吸血鬼",
    "VampireShot": "吸血射击", "SummonToad": "召唤蛤蟆", "PoisonShot": "毒箭", "CrippleShot": "残废射击",
    "SummonSnakes": "召唤蛇群", "NapalmShot": "凝固汽油弹", "OneWithNature": "融入自然", "BindingShot": "束缚射击",
    "MentalState": "精神状态", "Portal": "传送门", "BattleCry": "战斗怒吼", "FireBounce": "火焰反弹",
    "MeteorShower": "流星雨", "Errands": "跑腿任务", "Messenger": "信使",
    "Increase Speed for 30m.": "移动速度提高，持续 30 分钟。",
    "Premium Fishing Package.\r\nPurchased from the GameShop package consists of:\r\n:RedFishingRod (6 Months)\r\n:PremiumReel\r\n:PremiumFloat\r\n:PremiumFinder\r\n:PremiumBait x7200":
        "尊贵钓鱼礼包。\n商城购买的礼包包含：\n：红色钓竿（6 个月）\n：尊贵鱼线轮\n：尊贵浮漂\n：尊贵探鱼器\n：尊贵鱼饵 ×7200",
    "A Scroll which allow you to Summon One Guard.\r\n-Summons One Guard.\r\n-Duration 7 Days.\r\n-Command @GuardsHelp":
        "可召唤一名卫兵的卷轴。\n-召唤一名卫兵。\n-持续 7 天。\n-命令：@GuardsHelp",
    "Mysterious Stone. Which Gives you additional Experience.\r\n-Additional Experience 30%\r\n-None Stackable\r\n-Command @MysteriousStoneON\r\n-Command @MysteriousStoneOFF\r\n                  ":
        "可获得额外经验的神秘石头。\n-额外经验 +30%\n-不可叠加\n-开启命令：@MysteriousStoneON\n-关闭命令：@MysteriousStoneOFF",
    "Jack Sparrow": "杰克·斯派洛", "Male Assassin": "男刺客", "Female Assassin": "女刺客",
    "Santa Claus with Candy cane.": "手持糖果杖的圣诞老人", "Santa's best friend": "圣诞老人的挚友",
    "Spider(Scythe)": "蜘蛛镰刀时装", "Manectric Club": "雷电狼牙棒时装", "Manectric Hammer": "雷电战锤时装",
    "Spider(BarbarianScythe)": "蜘蛛蛮荒镰刀时装", "RedOverAlls(M)": "红色工装（男）",
    "Red/Yellow(F)": "红黄时装（女）", "Blue(M)": "蓝色时装（男）", "Red/White(F)": "红白时装（女）",
    "Football Red": "红色足球时装", "White Dress RedTrident(F)": "白裙红戟时装（女）",
    "Black Dress Fan": "黑裙折扇时装", "Black Sword(M)": "黑衣长剑时装（男）",
    "Gold Armour + Scythe(M)": "金甲镰刀时装（男）", "Ice King": "寒冰之王时装",
    "Football Player Black uniform": "黑色球衣时装", "Football Player Red and Blue uniform": "红蓝球衣时装",
    "Football Player White Blue stripe uniform": "白底蓝纹球衣时装", "Football Player White uniform": "白色球衣时装",
    "Football Player Pink and Blue uniform": "粉蓝球衣时装", "WhiteDress Blue/Red Trident": "白裙蓝红戟时装",
    "Angry Sheep": "愤怒绵羊时装", "Formal Male": "男士礼服",
}
# Fixed names from the official Legend of Mir terminology take precedence over
# generic CamelCase splitting. These values are display names only; lookupName
# stays unchanged so drops, scripts, and spawn settings remain valid.
OFFICIAL_ITEM_MAP = {
    "WoodenSword": "木剑",
    "JudgementMace": "裁决之杖",
    "ZumaJudgementMace": "祖玛裁决之杖",
    "DragonSlayer": "屠龙",
    "BlackDragonSlayer": "黑龙屠龙",
    "WoomasHorn": "沃玛号角",
    "RedMoonChip": "赤月碎片",
    "RedMoonSword": "赤月剑",
    "RedMoonBlades": "赤月双刃",
    "RedMoonBow": "赤月弓",
    "FrozenSabre": "凝霜",
    "SerpentSword": "银蛇",
    "MageStaff": "魔杖",
    "ParalysisRing": "麻痹戒指",
    "TeleportRing": "传送戒指",
    "ProtectionRing": "防御戒指",
    "RevivalRing": "复活戒指",
    "FlameRing": "火焰戒指",
    "PowerRing": "力量戒指",
    "CopperRing": "古铜戒指",
    "GlassRing": "玻璃戒指",
    "HornRing": "牛角戒指",
    "BlueRing": "蓝色水晶戒指",
    "BlackRing": "黑色水晶戒指",
    "GoldRing": "金戒指",
    "ExpelRing": "降妖除魔戒指",
    "CoralRing": "珊瑚戒指",
    "RubyRing": "红宝石戒指",
    "PlatinumRing": "铂金戒指",
    "DragonRing": "龙之戒指",
    "SilverBracelet": "银手镯",
    "SteelBracelet": "钢手镯",
    "LargeBracelet": "大手镯",
    "GoldBracelet": "金手镯",
    "DragonBracelet": "龙之手镯",
    "BlackIronBracelet": "黑铁手镯",
    "GoldNecklace": "金项链",
    "BlueJadeNecklace": "蓝翡翠项链",
    "BlackIronHelmet": "黑铁头盔",
    "SoulNecklace": "灵魂项链",
    "DragonNecklace": "龙之项链",
}

OFFICIAL_MONSTER_MAP = {
    "Hen": "鸡",
    "HookingCat": "多钩猫",
    "RakingCat": "钉耙猫",
    "CannibalPlant": "食人花",
    "Oma": "半兽人",
    "OmaFighter": "半兽战士",
    "OmaWarrior": "半兽勇士",
    "RedSnake": "红蛇",
    "TigerSnake": "虎蛇",
    "BoneFighter": "骷髅战士",
    "BoneWarrior": "骷髅战将",
    "BoneElite": "骷髅精灵",
    "WhiteBoar": "白野猪",
    "RedBoar": "红野猪",
    "BlackBoar": "黑野猪",
    "BlackMaggot": "黑色恶蛆",
    "WhimperingBee": "跳跳蜂",
    "Tongs": "钳虫",
    "EvilTongs": "邪恶钳虫",
    "WedgeMoth": "楔蛾",
    "SnakeScorpion": "蝎蛇",
    "GiantRat": "大老鼠",
    "ZumaGuardian": "祖玛卫士",
    "Dark": "暗黑战士",
    "WoomaSoldier": "沃玛战士",
    "WoomaFighter": "沃玛勇士",
    "WoomaWarrior": "沃玛战将",
    "WoomaGuardian": "沃玛卫士",
    "WoomaTaurus": "沃玛教主",
    "ZumaTaurus": "祖玛教主",
    "WhiteSerpent": "白蛇",
    "KingHog": "野猪王",
    "KingScorpion": "蝎子王",
    "RedMoonEvil": "赤月恶魔",
}

ITEM_SIZE_MAP = {"S": "小", "M": "中", "L": "大", "XL": "特大"}
ITEM_SIZE_BASE_MAP = {
    "HealthStone": "生命石",
    "MagicStone": "魔法石",
    "PowerStone": "力量石",
    "TaoistDrug": "道士药品",
}


def fixed_name(source: str, mapping: dict[str, str]) -> str | None:
    if source in mapping:
        return mapping[source]
    match = re.fullmatch(r"(.+?)(\d+)", source)
    if match and match.group(1) in mapping:
        return f"{mapping[match.group(1)]}{match.group(2)}"
    return None


def translate_item_special_case(source: str) -> str | None:
    match = re.fullmatch(r"(HealthStone|MagicStone|PowerStone|TaoistDrug)(?:\((S|M|L|XL)\))?", source)
    if not match:
        return None
    base = ITEM_SIZE_BASE_MAP[match.group(1)]
    size = ITEM_SIZE_MAP.get(match.group(2) or "")
    return f"{base}（{size}）" if size else base


NPC_ROLE_MAP = {
    "CraftsLady": "制作师",
    "HighPriest": "大祭司",
    "HighAssassin": "刺客导师",
    "MasterMage": "法师导师",
    "Teleport": "传送员",
    "TrustMerchant": "寄售商",
    "Warehouse": "仓库管理员",
    "TrainerTaoist": "道士导师",
    "BigTaoist": "大道士",
    "MasterMK": "武学大师",
    "Transport": "传送员",
    "Mysterious": "神秘人",
    "VillageChief": "村长",
    "StableGirl": "马厩管理员",
    "SubjugationLead": "讨伐队长",
    "SubjagationManager": "讨伐管理员",
    "PotionShop": "药店老板",
    "Grocery": "杂货商",
    "Accessory": "首饰商",
    "Book": "书店老板",
    "FishMonger": "鱼贩",
    "TheWatcher": "守望者",
    "SquadLeader": "小队长",
    "VulnerableSon": "胆怯的孩子",
    "GTMerchant": "行会领地商人",
    "GTStore": "行会领地商店",
}

NPC_PERSON_MAP = {
    "Bull": "布尔",
    "Cloud": "克劳德",
    "Cook": "库克",
    "Smith": "史密斯",
    "Yu": "余",
}

NPC_EXACT_MAP = {
    "BorderVillage_Board": "边境村公告牌",
    "BichonWall_Board": "比奇城公告牌",
    "MudWall_Board": "盟重土城公告牌",
    "Prison_Guard": "监狱守卫",
    "_Shinsu(Jude)": "神兽（裘德）",
    "Challange_OldMan": "挑战老人",
    "Sir_Mogu": "莫古爵士",
    "StrangeMan": "神秘人",
    "Merchant_Dr.Kim": "商人·金博士",
    "Merchant_Mr.Wang": "商人·王先生",
    "Merchant_Dr.Hwa": "商人·华博士",
    "General_Sir.Kevin": "将军·凯文爵士",
    "OddOldMan": "古怪老人",
    "TimeStone": "时光石",
    "MysteriousStone": "神秘石",
    "OldSkull": "古老骷髅头",
    "GuardianRock": "守护石",
    "BrokenCarriage": "损坏的马车",
    "CrushedBones": "碎骨堆",
    "SkullPile": "骷髅堆",
    "SkeletonPile": "骷髅堆",
    "CraftingVillage_Portal": "工匠村传送门",
    "GTTransporter": "行会领地传送员",
    "GT_BulletinBoard": "行会领地公告牌",
    "Gt_BulletinBoard": "行会领地公告牌",
    "GT_Peddler": "行会领地商贩",
    "GT_Steward": "行会领地管家",
    "Administrator_SabukOfficer": "沙巴克管理员",
    "GM_Teleporter": "GM 传送员",
    "Premium_Elijah": "高级通行证管理员·以利亚",
    "Proceeder": "接引人",
    "MissDo": "多小姐",
    "MissMi": "米小姐",
    "MissRe": "蕾小姐",
}

PHRASE_MAP = {
    "Instantly heals player.": "立即恢复生命值。", "Repairs equipped weapons durability to maximum.": "将已装备武器的持久恢复至最大值。",
    "Scroll which revives you from death.": "使用后可使你从死亡中复活的卷轴。", "Quest reward item.": "任务奖励物品。",
    "Mysterious scroll": "神秘卷轴", "Mysterious stone": "神秘石头", "Premium Torch purchased from the GameShop.": "从商城购买的尊贵火炬。",
    "Additional Experience acquired for a period of time.": "在一段时间内获得额外经验。", "Additional Drop Rate acquired for a period of time.": "在一段时间内获得额外掉宝率。",
    "Improvement of your": "提升你的", "for a certain period of time.": "，持续一段时间。", "None Stackable": "不可叠加",
    "Limited Item.": "限时物品。", "Potion which gives the following enhancements.": "可获得以下强化效果的药水。",
    "Physical Hunting Package consists of.": "物理狩猎礼包包含：", "Magical Hunting Package consists of.": "魔法狩猎礼包包含：",
    "Soul Hunting Package consists of.": "道术狩猎礼包包含：", "Purchased from the GameShop package consists of:": "商城购买的礼包包含：",
    "Useage Once.": "使用一次。", "Duration": "持续时间", "Duratiuon": "持续时间", "Hours": "小时", "Hour": "小时",
    "Minutes": "分钟", "Minuets": "分钟", "Days": "天", "Day": "天", "Months": "个月", "Month": "个月",
    "Experience": "经验", "Destruction Power": "攻击力", "Magical Power": "魔法力", "Soul Power": "道术力",
    "Physical Defence": "物理防御", "Magical Defence": "魔法防御", "Attack Speed": "攻击速度", "Additional Experience": "额外经验",
    "Resurrection HP": "复活生命", "Resurrection MP": "复活魔法", "PK Point": "PK 点数", "Command": "命令",
    "Allows Entry to the Premium Dungeon": "允许进入尊贵地下城", "Invite players to your GT.": "邀请玩家进入你的个人领地。",
    "In the Kunlun region, it seems to have lost its strength": "在昆仑地区似乎失去了力量",
    "Used in certain Taoist spells to give red poison (reduces defences)": "用于道士施放红毒，可降低目标防御。",
    "Required amulet for most Taoist spells.": "大多数道士技能所需的护身符。",
    "Bundle which contains total of 3000 Amulets": "内含 3000 张护身符的包裹。",
    "Required amulet for the Taoists resurrection spell.": "道士施放复活术所需的护身符。",
    "Strange scroll that can take you back to your Guild's Home": "可将你传送回行会驻地的奇异卷轴。",
    "Teleports player to their last saved safezone.": "将玩家传送至最近保存的安全区。",
    "Required for each upgrade.": "每次升级时都需要此物品。",
    "Click this egg to obtain a Creature.": "点击此蛋可获得一只灵兽。",
    "A Creature can pickup your items for you while you can continue the fight.": "灵兽会在你战斗时帮助拾取物品。",
    "With this mirror you can rename a creature.": "使用这面镜子可以为灵兽改名。",
    "Special creatures produce these rare stones every 3 hours.": "特殊灵兽每 3 小时产出一块稀有石头。",
    "Breaking this stone will grant you a random item.": "打碎石头后可随机获得一件物品。",
    "Maintain food level  for 10 hours.": "使灵兽的饱食度维持 10 小时。",
    "You can give this to your creature.": "可将此物品喂给你的灵兽。",
    "When a creature's feeding reaches 0": "当灵兽的饱食度降至 0 时使用。",
    "Open this box to receive a random wonder.": "打开宝箱可随机获得一件奇珍。",
    "Increase Experience by": "经验加成", "Increase DropRate by": "掉宝率提高", "Increase MaxHP by": "生命上限提高",
    "Increase MaxMP by": "魔法上限提高", "Increase BagWeight by": "背包负重提高", "Increase Speed for": "移动速度提高，持续",
    "Improvement of Destruction": "提升攻击力", "Improvement of Magic": "提升魔法力",
    "Improvement of Soul Magic": "提升道术力", "Improvement of your Mana": "提升魔法值",
    "Improvement of your Health": "提升生命值", "Improvement of your Attack Speed": "提升攻击速度",
    "Improvement of your Physical Defence": "提升物理防御", "Improvement of your Accuracy": "提升准确",
}

WORD_MAP = {
    "a": "", "an": "", "the": "", "to": "前往", "return": "返回", "speak": "与其交谈", "with": "与",
    "at": "，位于", "in": "在", "and": "并", "or": "或", "for": "用于", "of": "的", "on": "在",
    "from": "来自", "deliver": "交付", "find": "寻找", "hunt": "猎杀", "kill": "击杀", "search": "寻找",
    "travel": "前往", "visit": "拜访", "meet": "会见", "help": "帮助", "talk": "交谈", "bring": "带来",
    "give": "给予", "collect": "收集", "lost": "遗失的", "missing": "失踪的", "secret": "秘密的", "danger": "危险",
    "dangerous": "危险的", "test": "试炼", "skill": "技能", "request": "请求", "threat": "威胁", "threats": "威胁",
    "ingredients": "材料", "ingredient": "材料", "delivery": "递送", "deliver": "递送", "supplies": "补给",
    "information": "情报", "report": "报告", "research": "研究", "message": "消息", "news": "消息", "problem": "麻烦",
    "way": "道路", "path": "道路", "passage": "通道", "remains": "遗骸", "bones": "骨头", "soul": "灵魂",
    "book": "书", "books": "书籍", "ring": "戒指", "sword": "剑", "necklace": "项链", "materials": "材料",
    "food": "食物", "wine": "酒", "medicine": "药剂", "poison": "毒药", "friend": "朋友", "friends": "朋友",
    "farmer": "农夫", "farmers": "农夫", "lumberer": "伐木工", "carriage": "马车", "uniform": "制服",
    "order": "订单", "status": "状态", "ancient": "远古", "more": "更多", "first": "第一项", "second": "第二项",
    "last": "最后一次", "best": "最好的", "healthy": "健康的", "free": "解放", "save": "拯救", "wipe": "消灭",
    "exterminate": "歼灭", "eliminate": "消灭", "boss": "首领", "mob": "怪物", "monsters": "怪物", "monster": "怪物",
    "abnormal": "异常", "creatures": "生物", "guards": "卫兵", "guard": "卫兵", "village": "村庄", "chief": "首领",
    "chief's": "首领的", "morale": "士气", "increase": "提升", "safety": "安全", "saftey": "安全", "darkness": "黑暗",
    "shadow": "阴影", "call": "呼唤", "dead": "亡者", "death": "死亡", "words": "遗言", "letter": "信件", "letters": "信件",
    "parents": "父母", "cooking": "烹饪", "beef": "牛肉", "stew": "炖肉", "red": "红色", "white": "白色",
    "skeleton": "骷髅", "zombies": "僵尸", "zombie": "僵尸", "snake": "蛇", "snakes": "蛇", "boar": "野猪",
    "teeth": "獠牙", "skull": "头骨", "skulls": "头骨", "sister": "妹妹", "grandad": "爷爷", "grandfather": "祖父",
    "student": "学徒", "trainee": "学徒", "insomnia": "失眠", "wanted": "通缉", "force": "强制", "recruit": "招募",
    "rare": "稀有", "ore": "矿石", "flower": "花", "chestnut": "栗子", "tooth": "牙齿", "holy": "神圣",
    "sword": "剑", "cursed": "被诅咒的", "souls": "灵魂", "cave": "洞穴", "mines": "矿洞", "mine": "矿洞",
    "island": "岛屿", "forest": "森林", "woods": "森林", "valley": "山谷", "wall": "城", "tavern": "酒馆",
    "shop": "商店", "store": "商店", "town": "城镇", "towns": "城镇", "dungeon": "地下城", "entrances": "入口",
    "level": "等级", "levels": "等级", "skill": "技能", "captain": "队长", "manager": "主管", "master": "大师",
}


def split_tokens(value: str) -> list[str]:
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", value)
    value = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", value)
    return re.findall(r"[A-Za-z]+|\d+|[^A-Za-z\d\s]+", value)


def translate_name(source: str, category: str = "") -> str:
    if not source:
        return source
    if category == "item":
        special_case = translate_item_special_case(source)
        if special_case:
            return special_case
        official = fixed_name(source, OFFICIAL_ITEM_MAP)
        if official:
            return official
    if category == "monster":
        official = fixed_name(source, OFFICIAL_MONSTER_MAP)
        if official:
            return official
    if source in EXACT_MAP:
        return EXACT_MAP[source]
    if category == "monster" and source == "00":
        return "测试怪物"

    parts = split_tokens(source)
    output: list[str] = []
    for part in parts:
        if part.isdigit():
            output.append(part)
            continue
        if re.fullmatch(r"[^A-Za-z\d]+", part):
            punctuation = part.translate(str.maketrans({"(": "（", ")": "）", "[": "【", "]": "】", "_": "", "/": "·"}))
            output.append(punctuation)
            continue
        lower = part.lower()
        translated = PROPER_MAP.get(lower) or TOKEN_MAP.get(lower)
        if translated is None:
            translated = {"item": "秘制", "monster": "异种", "map": "秘境", "magic": "秘术"}.get(category, "人物")
        output.append(translated)
    result = "".join(output).replace("（）", "")
    if category == "item":
        result = result.replace("（拱门）", "（弓箭手）")
        result = result.replace("（战争）", "（战士）")
        result = result.replace("（道）", "（道士）")
    result = re.sub(r"(秘制|异种|秘境|秘术|人物)(?=\1)", "", result)
    if not result:
        result = "未命名"
    if category == "monster" and result == "异种":
        result = "神秘怪物"
    return result


def protect(text: str) -> tuple[str, dict[str, str]]:
    saved: dict[str, str] = {}

    def replace(match: re.Match[str]) -> str:
        key = f"__保留{len(saved)}__"
        saved[key] = match.group(0)
        return key

    return re.sub(r"<[^>]+>|\{[^}]+\}|@\w+|\$\w+|#[A-Za-z_]+|\b(?:DCTorch|MCTorch|SCTorch|MAXHP|EXP|HP|MP|PK)\b", replace, text), saved


def restore(text: str, saved: dict[str, str]) -> str:
    for key, value in saved.items():
        text = text.replace(key, value)
    return text


def translate_sentence(source: str, replacements: dict[str, str]) -> str:
    if not source:
        return source
    if source in EXACT_MAP:
        return EXACT_MAP[source]
    text, saved = protect(source)
    combined_replacements = {
        key: value for key, value in {**EXACT_MAP, **replacements}.items()
        if re.search(r"[A-Za-z]", key)
    }
    for old, new in sorted(combined_replacements.items(), key=lambda pair: len(pair[0]), reverse=True):
        if old not in text:
            continue
        text = re.sub(rf"(?<![A-Za-z]){re.escape(old)}(?![A-Za-z])", lambda _: new, text)
    for old, new in sorted(PHRASE_MAP.items(), key=lambda pair: len(pair[0]), reverse=True):
        text = text.replace(old, new)
    text = re.sub(r"\b1st\b", "第一次", text, flags=re.IGNORECASE)
    text = re.sub(r"\b2nd\b", "第二次", text, flags=re.IGNORECASE)
    text = re.sub(r"\b3rd\b", "第三次", text, flags=re.IGNORECASE)
    text = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", text)
    text = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", text)
    text = re.sub(r"\bDuration\b", "持续时间", text, flags=re.IGNORECASE)
    text = re.sub(r"\b(?:Hours?|Days?|Months?|Minutes?)\b", lambda match: {"hour": "小时", "hours": "小时", "day": "天", "days": "天", "month": "个月", "months": "个月", "minute": "分钟", "minutes": "分钟"}[match.group(0).lower()], text, flags=re.IGNORECASE)
    def translate_word(match: re.Match[str]) -> str:
        word = match.group(0).lower()
        possessive = word.endswith("'s")
        if possessive:
            word = word[:-2]
        translated = WORD_MAP.get(word, TOKEN_MAP.get(word, PROPER_MAP.get(word, "相关")))
        return translated + ("的" if possessive else "")

    text = re.sub(r"\b[A-Za-z][A-Za-z']*\b", translate_word, text)
    text = re.sub(r"[^\S\r\n]+", " ", text)
    text = re.sub(r" *\r?\n *", "\n", text).strip()
    return restore(text, saved)


def translate_npc(source: str) -> str:
    if source in NPC_EXACT_MAP:
        return NPC_EXACT_MAP[source]
    if source in {"Signpost", "Pillar", "Stairs", "Monument", "Bones", "Ashes", "SkullPile", "SkeletonPile", "CrushedBones"}:
        return translate_name(source)
    parts = source.split("_")
    role = NPC_ROLE_MAP.get(parts[0], translate_name(parts[0]))
    if len(parts) == 1:
        return role
    suffix = "·".join(NPC_PERSON_MAP.get(part, translate_name(part)) for part in parts[1:] if part)
    return f"{role}·{suffix}"


def fill_name_corrections(document: dict, reference_path: Path, kinds: set[str]) -> int:
    supported_kinds = {"item", "monster", "npc", "map", "magic", "quest"}
    unknown_kinds = kinds - supported_kinds
    if unknown_kinds:
        raise ValueError(f"Unsupported correction kind(s): {', '.join(sorted(unknown_kinds))}")

    reference = json.loads(reference_path.read_text(encoding="utf-8-sig"))
    reference_by_key = {
        entry["key"]: entry
        for entry in reference["entries"]
        if entry["kind"] in kinds and entry["field"] == "name"
    }
    changed = 0
    for entry in document["entries"]:
        entry["translation"] = ""
        if entry["kind"] not in kinds or entry["field"] != "name":
            continue
        reference_entry = reference_by_key.get(entry["key"])
        if reference_entry is None:
            continue
        current_lookup = entry.get("lookupName")
        reference_lookup = reference_entry.get("lookupName")
        if current_lookup and reference_lookup and current_lookup != reference_lookup:
            raise ValueError(
                f'{entry["key"]}: lookupName mismatch '
                f'({current_lookup!r} != {reference_lookup!r})'
            )
        desired = reference_entry.get("translation", "")
        if desired and desired != entry["source"]:
            entry["translation"] = desired
            changed += 1
    kind_label = ",".join(sorted(kinds))
    document["translationGeneratedBy"] = (
        f"fill_database_translations.py --name-corrections-reference ({kind_label})"
    )
    return changed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--npc-corrections-reference", type=Path)
    parser.add_argument("--name-corrections-reference", type=Path)
    parser.add_argument("--name-correction-kinds", default="item,monster")
    args = parser.parse_args()
    output = args.output or args.input

    document = json.loads(args.input.read_text(encoding="utf-8-sig"))
    if args.npc_corrections_reference and args.name_corrections_reference:
        parser.error("use only one corrections reference option")
    if args.npc_corrections_reference:
        changed = fill_name_corrections(document, args.npc_corrections_reference, {"npc"})
        output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Prepared {changed} NPC name correction entries: {output}")
        return 0
    if args.name_corrections_reference:
        kinds = {
            kind.strip()
            for kind in args.name_correction_kinds.split(",")
            if kind.strip()
        }
        if not kinds:
            parser.error("--name-correction-kinds cannot be empty")
        changed = fill_name_corrections(document, args.name_corrections_reference, kinds)
        output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Prepared {changed} name correction entries ({','.join(sorted(kinds))}): {output}")
        return 0

    entries = document["entries"]
    by_source: dict[str, str] = {}

    for entry in entries:
        entry.pop("hasTranslation", None)
        entry.pop("isUnsafeNameChange", None)
        if entry["kind"] in {"item", "monster"} and entry["field"] == "name":
            entry.setdefault("lookupName", entry["source"])

    for entry in entries:
        if entry["field"] != "name" and not (entry["kind"] == "map" and entry["field"] == "title"):
            continue
        if entry["kind"] == "npc":
            translation = translate_npc(entry["source"])
        else:
            translation = translate_name(entry["source"], entry["kind"])
        entry["translation"] = translation
        if entry["kind"] != "quest":
            by_source.setdefault(entry["source"], translation)

    for entry in entries:
        if entry["field"] == "toolTip":
            index = int(entry["index"])
            item_name = next((item.get("translation") for item in entries if item["kind"] == "item" and item["field"] == "name" and int(item["index"]) == index), "该物品")
            if 973 <= index <= 1041:
                entry["translation"] = f"{item_name}的技能说明。"
            elif 1084 <= index <= 1110 or 1350 <= index <= 1360:
                entry["translation"] = translate_name(entry["source"], "item")
            elif entry["source"].strip().lower() == "wont":
                entry["translation"] = "暂无说明"
            else:
                translation = translate_sentence(entry["source"], by_source)
                entry["translation"] = f"{item_name}的物品说明。" if "相关" in translation else translation
        elif entry["kind"] == "quest":
            entry["translation"] = translate_sentence(entry["source"], by_source).replace("相关", "")

    document["translationGeneratedBy"] = "fill_database_translations.py"
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    translated = sum(1 for entry in entries if entry.get("translation") and entry["translation"] != entry["source"])
    print(f"Filled {translated}/{len(entries)} translation entries: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
