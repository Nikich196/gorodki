#!/usr/bin/env python3
"""Снимки экранов из пакета результатов UI-тестов — в PNG с понятными именами (шаг ios-snapshots.yml).

`xcrun xcresulttool export attachments` (Xcode 16+) выгружает вложения под именами-UUID и пишет manifest.json: какой
файл из какого теста и как вложение называлось в тесте. Здесь картинки копируются под именами вложений («01-root.png»),
чтобы в артефакте запуска их можно было открыть по порядку. Снимки, которые XCTest сделал сам при падении теста, —
с приставкой «failure-».

Запуск: python3 ios/scripts/export-screenshots.py <пакет .xcresult> <папка для картинок>
        python3 ios/scripts/export-screenshots.py --exported <папка выгрузки xcresulttool> <папка для картинок>
Код выхода 1 — ни одной картинки: тесты не дошли до снимков или сменился формат выгрузки (manifest.json — в журнал).
Если manifest.json нет или он не разбирается, картинки копируются под именами выгрузки — снимки важнее имён.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".heic"}
# xcresulttool добавляет к имени вложения номер и UUID: «01-root_0_<UUID>.png» → «01-root».
EXPORT_SUFFIX = re.compile(r"_\d+_[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")
UNSAFE = re.compile(r"[^A-Za-z0-9._-]+")


def readable_name(attachment: dict, exported: Path) -> str:
    """Имя файла без расширения: имя вложения из теста без добавок xcresulttool, только безопасные символы."""
    suggested = attachment.get("suggestedHumanReadableName") or attachment.get("name") or exported.name
    stem = EXPORT_SUFFIX.sub("", Path(str(suggested)).stem)
    stem = UNSAFE.sub("-", stem).strip("-") or exported.stem
    return f"failure-{stem}" if attachment.get("isAssociatedWithFailure") else stem


def manifest_images(exported_dir: Path) -> list[tuple[Path, str]] | None:
    """(файл, имя) каждой картинки по manifest.json; None — манифеста нет или формат незнакомый."""
    try:
        manifest = json.loads((exported_dir / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(manifest, list):
        return None
    images = []
    for test in manifest:
        for attachment in test.get("attachments", []) if isinstance(test, dict) else []:
            if not isinstance(attachment, dict):
                continue
            exported = exported_dir / str(attachment.get("exportedFileName", ""))
            if exported.is_file() and exported.suffix.lower() in IMAGE_SUFFIXES:
                images.append((exported, readable_name(attachment, exported)))
    return images


def collect(exported_dir: Path, out_dir: Path) -> list[Path]:
    """Скопировать картинки выгрузки в out_dir под понятными именами; вернуть скопированные файлы по порядку."""
    images = manifest_images(exported_dir)
    if images is None:
        print("::warning::manifest.json выгрузки не разобран — картинки идут под именами выгрузки")
        images = [
            (path, path.stem)
            for path in sorted(exported_dir.iterdir())
            if path.is_file() and path.suffix.lower() in IMAGE_SUFFIXES
        ]
    out_dir.mkdir(parents=True, exist_ok=True)
    copied = []
    taken: set[str] = set()
    for source, stem in images:
        name, number = stem, 2
        while name.lower() in taken:
            name, number = f"{stem}-{number}", number + 1
        taken.add(name.lower())
        target = out_dir / f"{name}{source.suffix.lower()}"
        shutil.copyfile(source, target)
        copied.append(target)
    return sorted(copied)


def export_attachments(xcresult: Path, exported_dir: Path) -> None:
    subprocess.run(
        ["xcrun", "xcresulttool", "export", "attachments", "--path", str(xcresult), "--output-path", str(exported_dir)],
        check=True,
    )


def main(arguments: list[str]) -> int:
    if len(arguments) == 3 and arguments[0] == "--exported":
        exported_dir, out_dir = Path(arguments[1]), Path(arguments[2])
        copied = collect(exported_dir, out_dir)
    elif len(arguments) == 2:
        xcresult, out_dir = Path(arguments[0]), Path(arguments[1])
        if not xcresult.exists():
            print(f"::error::Пакета результатов нет: {xcresult} — UI-тесты не запустились")
            return 1
        with tempfile.TemporaryDirectory() as temp:
            exported_dir = Path(temp)
            export_attachments(xcresult, exported_dir)
            copied = collect(exported_dir, out_dir)
            if not copied and (exported_dir / "manifest.json").is_file():
                print((exported_dir / "manifest.json").read_text(encoding="utf-8"))
    else:
        print(__doc__)
        return 2

    if not copied:
        print("::error::Снимков экранов нет: тесты до них не дошли или xcresulttool выгрузил что-то другое")
        return 1
    print(f"Снимков экранов: {len(copied)}")
    for path in copied:
        print(f"  {path.name}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as file:
            file.write(f"### Снимки экранов: {len(copied)}\n\nАртефакт запуска, файлы:\n\n")
            file.writelines(f"- `{path.name}`\n" for path in copied)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
