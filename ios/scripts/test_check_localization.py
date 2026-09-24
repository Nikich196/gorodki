"""Тесты check-localization.py: запуск — python3 -m unittest discover -s ios/scripts -p 'test_*.py'."""

import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "check-localization.py"
spec = importlib.util.spec_from_file_location("check_localization", SCRIPT)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


def plural(**forms):
    return {"variations": {"plural": {name: {"stringUnit": {"state": "translated", "value": value}}
                                      for name, value in forms.items()}}}


FULL = plural(one="%lld петля", few="%lld петли", many="%lld петель", other="%lld петли")


class CheckLocalizationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "App").mkdir()
        (self.root / "Widgets").mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def swift(self, name, text, directory="App"):
        (self.root / directory / name).write_text(text, encoding="utf-8")

    def catalog(self, strings, directory="App"):
        data = {"sourceLanguage": "ru", "strings": strings, "version": "1.0"}
        (self.root / directory / "Localizable.xcstrings").write_text(json.dumps(data, ensure_ascii=False),
                                                                     encoding="utf-8")

    def run_check(self):
        output = io.StringIO()
        with redirect_stdout(output):
            code = check.main(["check-localization.py", str(self.root)])
        return code, output.getvalue()

    # Дроби через String(format:)

    def test_float_format_fails_with_file_and_line(self):
        self.swift("Run.swift", 'let a = 1\nlet title = "Забег · \\(String(format: "%.2f", km)) км"\n')
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("App/Run.swift:2:", output)

    def test_float_format_in_widgets_and_on_next_line_fails(self):
        self.swift("Widget.swift", 'let s = String(\n    format: "медиана %.1f с · 99 %% — %.1f с", a, b)\n',
                   directory="Widgets")
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("Widgets/Widget.swift:1:", output)

    def test_every_float_specifier_fails(self):
        for specifier in ("%f", "%.0f", "%5.1f", "%.3lf", "%e", "%g"):
            with self.subTest(specifier=specifier):
                self.swift("Run.swift", f'let s = String(format: "{specifier} км", x)\n')
                code, _ = self.run_check()
                self.assertEqual(code, 1)

    def test_integers_percent_sign_and_other_code_pass(self):
        self.swift("Ok.swift", 'let a = String(format: "%ld из %d · 99 %%", n, m)\n'
                               'let b = NumberText.kilometers(fromMeters: m, fractionDigits: 2)\n'
                               'let c = "%.2f"  // не String(format:)\n')
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    def test_packages_are_not_checked(self):
        (self.root / "Packages").mkdir()
        (self.root / "Packages" / "Core.swift").write_text('String(format: "%.2f", x)\n', encoding="utf-8")
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    # Русские формы множественного числа

    def test_full_russian_plural_passes(self):
        self.catalog({"%lld петель": {"localizations": {"ru": FULL}}})
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    def test_plural_without_many_fails(self):
        self.catalog({"%lld петель": {"localizations": {"ru": plural(one="%lld петля", few="%lld петли",
                                                                       other="%lld петли")}}})
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("«%lld петель» — нет форм many", output)

    def test_english_style_plural_fails(self):
        self.catalog({"%lld петель": {"localizations": {"ru": plural(one="%lld петля", other="%lld петель")}}})
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("нет форм few, many", output)

    def test_plural_inside_substitution_and_device_variation_is_checked(self):
        substitution = {"stringUnit": {"state": "translated", "value": "Пройдено %#@loops@"},
                        "substitutions": {"loops": {"argNum": 1, "formatSpecifier": "lld",
                                                    **plural(one="%arg петля", few="%arg петли",
                                                             many="%arg петель")}}}
        device = {"variations": {"device": {"iphone": plural(one="a", many="b", other="c")}}}
        self.catalog({"Пройдено": {"localizations": {"ru": substitution}},
                      "Экран": {"localizations": {"ru": device}}})
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("«Пройдено → loops» — нет форм other", output)
        self.assertIn("«Экран → device → iphone» — нет форм few", output)

    def test_other_languages_are_not_russian_rules(self):
        self.catalog({"%lld loops": {"localizations": {"en": plural(one="%lld loop", other="%lld loops")}}})
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    def test_broken_catalog_fails(self):
        (self.root / "App" / "Localizable.xcstrings").write_text("{", encoding="utf-8")
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("не JSON", output)

    # Настоящий репозиторий

    def test_repository_passes(self):
        output = io.StringIO()
        with redirect_stdout(output):
            code = check.main(["check-localization.py"])
        self.assertEqual(code, 0, output.getvalue())


if __name__ == "__main__":
    unittest.main()
