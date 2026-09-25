#!/usr/bin/env python3
"""Проверка текстов приложения по-русски (PLAN.md, §6.10) — шаг ios-core.

1. Дробные числа через String(format:) в App/ и Widgets/ запрещены: "%.2f" всегда ставит точку («1.23 км»), а игра
   пишет «1,23 км». Числа для игрока — через NumberText (GameCore). Строку формата не из литерала (переменная, вызов)
   не проверить — она тоже нарушение.
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

# String(format: …) — строка формата первым аргументом, в том числе на следующей строке кода.
FORMAT_CALL = re.compile(r"String\s*\(\s*format:\s*")
# Начало строкового литерала Swift: обычный "…", многострочный """…""" и «сырые» #"…"#, ##"""…"""##.
LITERAL_OPENER = re.compile(r'(#*)("""|")')
# Дробное число в строке формата (%% — знак процента, его убирают до поиска).
FLOAT_SPECIFIER = re.compile(
    r"%(?:\d+\$)?"  # номер аргумента: %1$.2f — так пишут в переводах
    r"[-+ #0']*"  # флаги
    r"(?:\d+|\*(?:\d+\$)?)?"  # ширина: %5.1f, %*f
    r"(?:\.(?:\d+|\*(?:\d+\$)?))?"  # точность: %.2f, %.*f
    r"(?:ll|l|L)?[fFeEgGaA]"  # длина и вид: %f, %.3lf, %.2Lf, %e, %g
)


def float_formats(ios_root: Path) -> list[str]:
    """Вызовы String(format:) с дробными числами или со строкой формата не из литерала: «файл:строка: что не так»."""
    problems = []
    for directory in SOURCE_DIRS:
        for path in sorted((ios_root / directory).rglob("*.swift")):
            text = path.read_text(encoding="utf-8")
            relative = path.relative_to(ios_root).as_posix()
            for match in FORMAT_CALL.finditer(text):
                # «String(format:)» без аргумента — имя инициализатора в комментарии, а не вызов.
                if text.startswith(")", match.end()):
                    continue
                line = text.count("\n", 0, match.start()) + 1
                literal = format_literal(text, match.end())
                if literal is None:
                    # Строку формата из переменной или вызова не проверить, а дробь в ней снова дала бы «1.23 км».
                    problems.append(
                        f"{relative}:{line}: String(format:) со строкой формата не из литерала — её не проверить; "
                        "числа для игрока — через NumberText (GameCore)"
                    )
                elif FLOAT_SPECIFIER.search(literal.replace("%%", "")):
                    shown = literal.replace("\n", "\\n")
                    problems.append(
                        f'{relative}:{line}: String(format: "{shown}") ставит точку в дробях — '
                        "нужен NumberText (GameCore)"
                    )
    return problems


def format_literal(text: str, start: int) -> str | None:
    """Содержимое строкового литерала Swift, который начинается с text[start], или None, если там не литерал."""
    opener = LITERAL_OPENER.match(text, start)
    if opener is None:
        return None
    hashes, quotes = opener.groups()
    closer = quotes + hashes
    # Экранирование в «сырой» строке — «\#», в обычной — «\»: экранированная кавычка строку не закрывает.
    escape = "\\" + hashes
    position = opener.end()
    while position < len(text):
        if text.startswith(escape, position):
            position += len(escape) + 1
        elif text.startswith(closer, position):
            return text[opener.end() : position]
        elif quotes == '"' and text[position] == "\n":
            return None  # однострочный литерал не переносится: это не литерал
        else:
            position += 1
    return None


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
