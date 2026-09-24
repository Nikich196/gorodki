#!/usr/bin/env python3
"""Версии Swift-пакетов приложения закреплены точно — шаг ios-build.

Пакеты ios/Packages/* объявляют зависимости диапазоном («from: 1.0.0»), а их Package.resolved действует, только когда
пакет собирают сам по себе (swift test в ios-core). Проект приложения генерируется XcodeGen каждый раз заново, своего
Package.resolved у него нет — без закрепления Xcode брал бы новейшие версии, и приложение собиралось бы не с тем, что
проверено тестами пакетов. Поэтому каждая внешняя зависимость повторена в ios/project.yml (packages:) с exactVersion,
а эта проверка следит, чтобы закрепления совпадали с Package.resolved пакетов:
1. у каждой внешней зависимости в project.yml есть exactVersion;
2. пакеты не расходятся между собой: одна зависимость — одна версия во всех Package.resolved;
3. каждая зависимость из Package.resolved закреплена в project.yml той же версией, и лишних закреплений нет.

Запуск: python3 ios/scripts/check-spm-pins.py [папка ios]. Код выхода 1 — есть расхождения, они перечислены.
PyYAML не нужен (в контейнере Swift и на раннере его может не быть): секция packages: в project.yml простая,
её разбирает project_packages ниже.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

TOP_LEVEL_KEY = re.compile(r"^[A-Za-z_][\w-]*:")
PACKAGE_KEY = re.compile(r"^  ([\w.-]+):\s*(?:#.*)?$")
PACKAGE_FIELD = re.compile(r"^    (\w+):\s*(.*?)\s*(?:#.*)?$")


def identity(url: str) -> str:
    """Как SwiftPM называет пакет по адресу: последняя часть пути без .git, строчными буквами."""
    name = url.rstrip("/").rsplit("/", 1)[-1]
    if name.lower().endswith(".git"):
        name = name[: -len(".git")]
    return name.lower()


def project_packages(project_yml: str) -> dict[str, dict[str, str]]:
    """Секция packages: из project.yml — {имя: {поле: значение}}; кавычки вокруг значений снимаются."""
    packages: dict[str, dict[str, str]] = {}
    inside = False
    current = None
    for line in project_yml.splitlines():
        if TOP_LEVEL_KEY.match(line):
            inside = line.startswith("packages:")
            current = None
            continue
        if not inside or not line.strip() or line.lstrip().startswith("#"):
            continue
        if match := PACKAGE_KEY.match(line):
            current = packages.setdefault(match.group(1), {})
        elif current is not None and (match := PACKAGE_FIELD.match(line)):
            current[match.group(1)] = match.group(2).strip("\"'")
    return packages


def resolved_pins(ios_root: Path) -> tuple[dict[str, dict[str, str]], list[str]]:
    """Закрепления всех Package.resolved пакетов: {identity: {version: файл}} и ошибки чтения."""
    pins: dict[str, dict[str, str]] = {}
    problems = []
    for path in sorted((ios_root / "Packages").glob("*/Package.resolved")):
        relative = path.relative_to(ios_root).as_posix()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as error:
            problems.append(f"{relative}: не читается ({error})")
            continue
        for pin in data.get("pins", []):
            version = (pin.get("state") or {}).get("version")
            if not version:
                problems.append(f"{relative}: {pin.get('identity')} закреплён не версией, а веткой или коммитом")
                continue
            pins.setdefault(pin["identity"], {}).setdefault(version, relative)
    return pins, problems


def check(ios_root: Path) -> list[str]:
    packages = project_packages((ios_root / "project.yml").read_text(encoding="utf-8"))
    remote = {name: fields for name, fields in packages.items() if "url" in fields}
    pinned: dict[str, tuple[str, str]] = {}
    problems = []
    for name, fields in sorted(remote.items()):
        if "exactVersion" not in fields:
            others = ", ".join(key for key in fields if key != "url") or "ничего"
            problems.append(f"project.yml: {name} — нужен exactVersion, а указано: {others}")
            continue
        pinned[identity(fields["url"])] = (name, fields["exactVersion"])

    pins, read_problems = resolved_pins(ios_root)
    problems += read_problems
    for package, versions in sorted(pins.items()):
        if len(versions) > 1:
            listed = "; ".join(f"{version} — {path}" for version, path in sorted(versions.items()))
            problems.append(f"{package}: пакеты закрепили разные версии ({listed}) — обнови Package.resolved")
            continue
        version, path = next(iter(versions.items()))
        if package not in pinned:
            problems.append(f"project.yml: нет закрепления {package} {version} (из {path})")
        elif pinned[package][1] != version:
            name, exact = pinned[package]
            problems.append(f"project.yml: {name} exactVersion {exact}, а в {path} — {version}")
    for package, (name, exact) in sorted(pinned.items()):
        if package not in pins:
            problems.append(f"project.yml: {name} {exact} закреплён, но ни одному пакету не нужен — убрать")
    return problems


def main() -> int:
    ios_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    problems = check(ios_root)
    if problems:
        print("Версии пакетов приложения расходятся с Package.resolved пакетов (ios/scripts/check-spm-pins.py):")
        for problem in problems:
            print(f"  {problem}")
        return 1
    print("Версии пакетов приложения совпадают с Package.resolved пакетов.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
