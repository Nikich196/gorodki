#!/usr/bin/env python3
"""Проверка текстов приложения по-русски (PLAN.md, §6.10) — шаг ios-core.

1. Дробные числа через String(format:) в App/ и Widgets/ запрещены: "%.2f" всегда ставит точку («1.23 км»), а игра
   пишет «1,23 км». Числа для игрока — через NumberText (GameCore).
2. У каждой русской формы множественного числа в каталогах строк (*.xcstrings) есть все четыре формы русского:
   one (1, 21 петля), few (2, 22 петли), many (5, 11 петель), other (1,5 петли). Без одной из них iOS молча возьмёт
   запасную форму, и где-то выйдет «5 петли».

Запуск: python3 ios/scripts/check-localization.py [папка ios]. Код выхода 1 — есть нарушения, они перечислены.
"""

import json
import re
import sys
from pathlib import Path

SOURCE_DIRS = ("App", "Widgets")
RUSSIAN_PLURAL_FORMS = ("one", "few", "many", "other")

# String(format: "…") — строка формата сразу первым аргументом, в том числе на следующей строке кода.
FORMAT_CALL = re.compile(r'String\s*\(\s*format:\s*"((?:[^"\\\n]|\\.)*)"')
# Дробное число в строке формата: %f, %.2f, %5.1f, %e, %g… (%% — знак процента, его убирают до поиска).
FLOAT_SPECIFIER = re.compile(r"%[-+ #0']*\d*(?:\.\d+)?l?[fFeEgGaA]")


def float_formats(ios_root: Path) -> list[str]:
    """Вызовы String(format:) с дробными числами: «файл:строка: формат»."""
    problems = []
    for directory in SOURCE_DIRS:
        for path in sorted((ios_root / directory).rglob("*.swift")):
            text = path.read_text(encoding="utf-8")
            for match in FORMAT_CALL.finditer(text):
                if FLOAT_SPECIFIER.search(match.group(1).replace("%%", "")):
                    line = text.count("\n", 0, match.start()) + 1
                    relative = path.relative_to(ios_root).as_posix()
                    problems.append(
                        f'{relative}:{line}: String(format: "{match.group(1)}") ставит точку в дробях — '
                        "нужен NumberText (GameCore)"
                    )
    return problems


def incomplete_plurals(ios_root: Path) -> list[str]:
    """Русские формы множественного числа без одной из one/few/many/other: «файл: ключ — чего нет»."""
    problems = []
    for directory in SOURCE_DIRS:
        for path in sorted((ios_root / directory).rglob("*.xcstrings")):
            relative = path.relative_to(ios_root).as_posix()
            try:
                catalog = json.loads(path.read_text(encoding="utf-8"))
            except json.JSONDecodeError as error:
                problems.append(f"{relative}: не JSON ({error})")
                continue
            for key, entry in sorted(catalog.get("strings", {}).items()):
                russian = entry.get("localizations", {}).get("ru")
                if russian is None:
                    continue
                for where, forms in plural_variations(russian, key):
                    missing = [form for form in RUSSIAN_PLURAL_FORMS if form not in forms]
                    if missing:
                        problems.append(f"{relative}: «{where}» — нет форм {', '.join(missing)}")
    return problems


def plural_variations(node, where):
    """Все варианты «plural» внутри локализации: и у самой строки, и в подстановках (substitutions), и внутри
    вариантов по устройству."""
    if isinstance(node, dict):
        for name, child in node.items():
            if name == "variations" and isinstance(child, dict) and isinstance(child.get("plural"), dict):
                yield where, child["plural"]
            label = f"{where} → {name}" if name not in ("variations", "localizations", "substitutions") else where
            yield from plural_variations(child, label)


def main(argv: list[str]) -> int:
    ios_root = Path(argv[1]) if len(argv) > 1 else Path(__file__).resolve().parent.parent
    problems = float_formats(ios_root) + incomplete_plurals(ios_root)
    for problem in problems:
        print(problem)
    if problems:
        print(f"Нарушений: {len(problems)}. Правила — в начале ios/scripts/check-localization.py.")
        return 1
    print("Локализация в порядке: дробей через String(format:) нет, русские формы множественного числа полные.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
