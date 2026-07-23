from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


SCHEMA_VERSION = 1
SCRIPT_DIRS = ("NPCs", "SystemScripts")
QUEST_VISIBLE_HEADERS = {
    "[@DESCRIPTION]": "questDescription",
    "[@TASKDESCRIPTION]": "questTaskDescription",
    "[@RETURNDESCRIPTION]": "questReturnDescription",
    "[@COMPLETION]": "questCompletionDescription",
}
LATIN_RE = re.compile(r"[A-Za-z]")
HAN_RE = re.compile(r"[\u3400-\u9fff]")
VISIBLE_ABBREVIATION_RE = re.compile(r"\b(?:PK|HP|MP|EXP|GM|NPC|DC|MC|SC)\b", re.IGNORECASE)
PROTECTED_RE = re.compile(
    r"https?://[^\s)>）]+|<\$[^>]+>|\{\$[^}]+\}|\$[A-Za-z_][\w]*|@[A-Za-z_][\w&+().-]*|"
    r"(?<=/)[A-Za-z_@][\w&+().-]*(?=>)|"
    r"(?<=[/{])(?:Aqua|Black|Blue|Brown|Coral|Crimson|Cyan|DarkBlue|DarkGray|DarkGreen|"
    r"DarkMagenta|DarkOrange|DarkRed|DarkViolet|DeepSkyBlue|DodgerBlue|Gold|Gray|Green|"
    r"GreenYellow|Khai|Khaki|LimeGreen|LightBlue|LightGray|LightGreen|LightPink|LightSalmon|LightSeaGreen|"
    r"LightSkyBlue|LightSteelBlue|Magenta|Orange|Pink|Purple|Red|Silver|Snow|SteelBlue|"
    r"RoyalBlue|SeaGreen|Violet|White|Yellow)(?=[}>])|\b\d+(?=o'clock)|"
    r"\b\d+[A-Za-z]+\d*\b(?!')|\b\d+(?:\.\d+)?\b",
    re.IGNORECASE,
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def read_text(path: Path) -> tuple[str, str]:
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        return data.decode("utf-8-sig"), "utf-8-sig"
    try:
        return data.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        return data.decode("cp1252"), "cp1252"


def file_record(path: Path, envir: Path) -> dict:
    data = path.read_bytes()
    _, encoding = read_text(path)
    return {
        "path": path.relative_to(envir).as_posix(),
        "sha256": sha256_bytes(data),
        "encoding": encoding,
    }


def is_candidate(line: str) -> bool:
    stripped = line.strip()
    if not stripped or stripped.startswith((";", "//", "@")):
        return False
    visible_text = PROTECTED_RE.sub("", stripped)
    visible_text = VISIBLE_ABBREVIATION_RE.sub("", visible_text)
    return bool(LATIN_RE.search(visible_text))


def make_entry(path: Path, envir: Path, line_number: int, kind: str, source: str) -> dict:
    relative = path.relative_to(envir).as_posix()
    digest = hashlib.sha1(f"{relative}\0{line_number}\0{source}".encode("utf-8")).hexdigest()[:16]
    return {
        "key": f"{relative}:{line_number}:{digest}",
        "path": relative,
        "line": line_number,
        "kind": kind,
        "source": source,
        "translation": "",
        "protectedTokens": PROTECTED_RE.findall(source),
    }


def extract_npc_entries(path: Path, envir: Path) -> list[dict]:
    text, _ = read_text(path)
    lines = text.splitlines()
    entries: list[dict] = []
    say_mode = False
    speech_mode = False

    for index, line in enumerate(lines, start=1):
        stripped = line.strip()
        upper = stripped.upper()

        if upper == "[SPEECH]":
            say_mode = False
            speech_mode = True
            continue

        if stripped.startswith("[") and stripped.endswith("]"):
            say_mode = False
            speech_mode = False
            continue

        if stripped.startswith("#"):
            directive = upper[1:].split(maxsplit=1)[0]
            say_mode = directive in {"SAY", "ELSESAY"}
            speech_mode = False
            continue

        if speech_mode:
            match = re.match(r"^(\s*\d+\s+)(.*)$", line)
            if match and is_candidate(match.group(2)):
                entries.append(make_entry(path, envir, index, "npcSpeech", line))
            continue

        if say_mode and is_candidate(line):
            entries.append(make_entry(path, envir, index, "npcDialogue", line))

    return entries


def extract_quest_entries(path: Path, envir: Path) -> list[dict]:
    text, _ = read_text(path)
    lines = text.splitlines()
    entries: list[dict] = []
    kind: str | None = None

    for index, line in enumerate(lines, start=1):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            kind = QUEST_VISIBLE_HEADERS.get(stripped.upper())
            continue
        if kind and is_candidate(line):
            entries.append(make_entry(path, envir, index, kind, line))

    return entries


def collect(envir: Path) -> tuple[list[dict], list[dict]]:
    files: list[dict] = []
    entries: list[dict] = []

    for directory in SCRIPT_DIRS:
        root = envir / directory
        if not root.exists():
            continue
        for path in sorted(root.rglob("*.txt")):
            files.append(file_record(path, envir))
            entries.extend(extract_npc_entries(path, envir))

    quest_root = envir / "Quests"
    if quest_root.exists():
        for path in sorted(quest_root.rglob("*.txt")):
            files.append(file_record(path, envir))
            entries.extend(extract_quest_entries(path, envir))

    return files, entries


def load_document(path: Path) -> dict:
    document = json.loads(path.read_text(encoding="utf-8-sig"))
    if document.get("schemaVersion") != SCHEMA_VERSION:
        raise ValueError(
            f"Unsupported schema version {document.get('schemaVersion')}; expected {SCHEMA_VERSION}."
        )
    return document


def export_document(envir: Path, output: Path) -> int:
    files, entries = collect(envir)
    document = {
        "schemaVersion": SCHEMA_VERSION,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "envirPath": str(envir.resolve()),
        "files": files,
        "entries": entries,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Exported {len(entries)} visible text entries from {len(files)} files to {output.resolve()}")
    print_counts(entries)
    return 0


def print_counts(entries: list[dict]) -> None:
    counts = Counter(entry["kind"] for entry in entries)
    for kind in sorted(counts):
        print(f"{kind}: {counts[kind]}")


def report_document(input_path: Path) -> int:
    document = load_document(input_path)
    entries = document["entries"]
    translated = [entry for entry in entries if has_translation(entry)]
    print(f"Entries: {len(entries)}")
    print(f"Translated: {len(translated)}")
    print(f"Remaining: {len(entries) - len(translated)}")
    print_counts(translated)
    return 0


def has_translation(entry: dict) -> bool:
    translation = entry.get("translation")
    return bool(translation and translation.strip() and translation != entry["source"])


def validate_document(envir: Path, document: dict, post_apply: bool = False) -> list[str]:
    errors: list[str] = []
    files = {record["path"]: record for record in document["files"]}
    line_cache: dict[str, list[str]] = {}

    for relative, record in files.items():
        path = envir / Path(relative)
        if not path.exists():
            errors.append(f"Missing source file: {relative}")
            continue
        if not post_apply and sha256_bytes(path.read_bytes()) != record["sha256"]:
            errors.append(f"Source file changed since export: {relative}")
        text, _ = read_text(path)
        line_cache[relative] = text.splitlines()

    keys: set[str] = set()
    for entry in document["entries"]:
        key = entry.get("key", "")
        if key in keys:
            errors.append(f"Duplicate entry key: {key}")
        keys.add(key)

        relative = entry["path"]
        lines = line_cache.get(relative)
        line_number = int(entry["line"])
        if lines is None or line_number < 1 or line_number > len(lines):
            errors.append(f"{key}: line no longer exists")
            continue

        expected = entry.get("translation") if post_apply and has_translation(entry) else entry["source"]
        if lines[line_number - 1] != expected:
            errors.append(f"{key}: source line no longer matches")

        if not has_translation(entry):
            continue
        translation = entry["translation"]
        if "\n" in translation or "\r" in translation or "\0" in translation:
            errors.append(f"{key}: translation must contain exactly one text line")
        source_tokens = entry.get("protectedTokens", PROTECTED_RE.findall(entry["source"]))
        translated_tokens = PROTECTED_RE.findall(translation)
        if Counter(source_tokens) != Counter(translated_tokens):
            errors.append(
                f"{key}: protected tokens changed; expected {source_tokens}, got {translated_tokens}"
            )

    return errors


def print_errors(errors: list[str]) -> None:
    for error in errors[:100]:
        print(f"Validation error: {error}", file=sys.stderr)
    if len(errors) > 100:
        print(f"Validation error: {len(errors) - 100} more error(s) omitted.", file=sys.stderr)


def validate_command(envir: Path, input_path: Path) -> int:
    document = load_document(input_path)
    errors = validate_document(envir, document)
    if errors:
        print_errors(errors)
        return 2
    print("Validation passed.")
    return 0


def create_backup(envir: Path, document: dict) -> Path:
    backup_dir = envir.parent / "ScriptLocalizationBackups"
    backup_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    backup_path = backup_dir / f"EnvirScripts.{timestamp}.zip"
    with zipfile.ZipFile(backup_path, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for record in document["files"]:
            relative = record["path"]
            archive.write(envir / Path(relative), arcname=relative)
        archive.writestr("localization-manifest.json", json.dumps(document["files"], indent=2))
    return backup_path


def write_lines(path: Path, lines: list[str], encoding: str, original: bytes) -> None:
    newline = "\r\n" if b"\r\n" in original else "\n"
    ended_with_newline = original.endswith((b"\n", b"\r"))
    text = newline.join(lines) + (newline if ended_with_newline else "")
    if encoding == "utf-8-sig":
        path.write_bytes(b"\xef\xbb\xbf" + text.encode("utf-8"))
    else:
        path.write_bytes(text.encode(encoding))


def apply_command(envir: Path, input_path: Path, write: bool) -> int:
    document = load_document(input_path)
    errors = validate_document(envir, document)
    if errors:
        print_errors(errors)
        print("No script files were changed.", file=sys.stderr)
        return 2

    changes = [entry for entry in document["entries"] if has_translation(entry)]
    print(f"Validated {len(changes)} translation change(s).")
    if not write:
        print("Dry run complete. Add --write to create a ZIP backup and update the scripts.")
        return 0

    backup_path = create_backup(envir, document)
    by_file: dict[str, list[dict]] = {}
    for entry in changes:
        by_file.setdefault(entry["path"], []).append(entry)

    changed_files = 0
    for relative, file_entries in by_file.items():
        path = envir / Path(relative)
        original = path.read_bytes()
        text, encoding = read_text(path)
        lines = text.splitlines()
        for entry in file_entries:
            lines[int(entry["line"]) - 1] = entry["translation"]
        write_lines(path, lines, encoding, original)
        changed_files += 1

    verify_errors = validate_document(envir, document, post_apply=True)
    if verify_errors:
        print_errors(verify_errors)
        print(f"Post-write verification failed. Restore from {backup_path}", file=sys.stderr)
        return 3

    print(f"Applied {len(changes)} translation(s) to {changed_files} file(s).")
    print(f"Backup: {backup_path.resolve()}")
    print("Post-write verification passed.")
    return 0


def verify_command(envir: Path, input_path: Path) -> int:
    document = load_document(input_path)
    errors = validate_document(envir, document, post_apply=True)
    if errors:
        print_errors(errors)
        return 2
    translated = sum(1 for entry in document["entries"] if has_translation(entry))
    print(f"Verified {translated} stored translation(s).")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Crystal NPC and quest script localization tool")
    subparsers = parser.add_subparsers(dest="command", required=True)

    export_parser = subparsers.add_parser("export")
    export_parser.add_argument("--envir", type=Path, required=True)
    export_parser.add_argument("--output", type=Path, required=True)

    report_parser = subparsers.add_parser("report")
    report_parser.add_argument("--input", type=Path, required=True)

    validate_parser = subparsers.add_parser("validate")
    validate_parser.add_argument("--envir", type=Path, required=True)
    validate_parser.add_argument("--input", type=Path, required=True)

    apply_parser = subparsers.add_parser("apply")
    apply_parser.add_argument("--envir", type=Path, required=True)
    apply_parser.add_argument("--input", type=Path, required=True)
    apply_parser.add_argument("--write", action="store_true")

    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--envir", type=Path, required=True)
    verify_parser.add_argument("--input", type=Path, required=True)

    args = parser.parse_args()
    if args.command == "export":
        return export_document(args.envir.resolve(), args.output)
    if args.command == "report":
        return report_document(args.input)
    if args.command == "validate":
        return validate_command(args.envir.resolve(), args.input)
    if args.command == "apply":
        return apply_command(args.envir.resolve(), args.input, args.write)
    if args.command == "verify":
        return verify_command(args.envir.resolve(), args.input)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
