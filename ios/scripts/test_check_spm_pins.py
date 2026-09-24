"""Тесты check-spm-pins.py: запуск — python3 -m unittest discover -s ios/scripts -p 'test_*.py'."""

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "check-spm-pins.py"
spec = importlib.util.spec_from_file_location("check_spm_pins", SCRIPT)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

PROJECT = """\
name: Gorodki

packages:
  GameCore:
    path: Packages/GameCore
  # Закрепления внешних зависимостей
  GRDB:
    url: https://github.com/groue/GRDB.swift.git
    exactVersion: 7.11.1
  swift-openapi-runtime:
    url: "https://github.com/apple/swift-openapi-runtime"
    exactVersion: "1.12.1"  # комментарий после значения

targets:
  Gorodki:
    dependencies:
      - package: GameCore
"""


def pin(identity, version, location=None):
    return {
        "identity": identity,
        "kind": "remoteSourceControl",
        "location": location or f"https://github.com/example/{identity}",
        "state": {"revision": "0" * 40, "version": version},
    }


class CheckSpmPinsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.project(PROJECT)

    def tearDown(self):
        self.temp.cleanup()

    def project(self, text):
        (self.root / "project.yml").write_text(text, encoding="utf-8")

    def resolved(self, package, *pins):
        directory = self.root / "Packages" / package
        directory.mkdir(parents=True, exist_ok=True)
        data = {"originHash": "x", "pins": list(pins), "version": 3}
        (directory / "Package.resolved").write_text(json.dumps(data), encoding="utf-8")

    def test_matching_pins_pass(self):
        self.resolved("GorodkiPersistence", pin("grdb.swift", "7.11.1"), pin("swift-openapi-runtime", "1.12.1"))
        self.resolved("GorodkiSync", pin("swift-openapi-runtime", "1.12.1"))
        self.assertEqual(check.check(self.root), [])

    def test_identity_follows_swiftpm(self):
        self.assertEqual(check.identity("https://github.com/groue/GRDB.swift.git"), "grdb.swift")
        self.assertEqual(check.identity("https://github.com/mattpolzin/OpenAPIKit/"), "openapikit")

    def test_parser_reads_only_packages_section(self):
        packages = check.project_packages(PROJECT)
        self.assertEqual(set(packages), {"GameCore", "GRDB", "swift-openapi-runtime"})
        self.assertEqual(packages["swift-openapi-runtime"]["exactVersion"], "1.12.1")
        self.assertEqual(packages["swift-openapi-runtime"]["url"], "https://github.com/apple/swift-openapi-runtime")

    def test_other_version_in_resolved_fails(self):
        self.resolved("GorodkiPersistence", pin("grdb.swift", "7.12.0"), pin("swift-openapi-runtime", "1.12.1"))
        problems = check.check(self.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("GRDB exactVersion 7.11.1", problems[0])
        self.assertIn("7.12.0", problems[0])

    def test_unpinned_dependency_fails(self):
        self.resolved(
            "GorodkiPersistence",
            pin("grdb.swift", "7.11.1"),
            pin("swift-openapi-runtime", "1.12.1"),
            pin("yams", "6.2.2"),
        )
        problems = check.check(self.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("нет закрепления yams 6.2.2", problems[0])

    def test_packages_disagreeing_fail(self):
        self.resolved("GorodkiPersistence", pin("grdb.swift", "7.11.1"), pin("swift-openapi-runtime", "1.12.1"))
        self.resolved("GorodkiSync", pin("swift-openapi-runtime", "1.11.0"))
        problems = check.check(self.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("разные версии", problems[0])

    def test_range_instead_of_exact_version_fails(self):
        self.project(PROJECT.replace("exactVersion: 7.11.1", "from: 7.11.0"))
        self.resolved("GorodkiPersistence", pin("grdb.swift", "7.11.1"), pin("swift-openapi-runtime", "1.12.1"))
        problems = check.check(self.root)
        self.assertIn("project.yml: GRDB — нужен exactVersion, а указано: from", problems)

    def test_stale_pin_fails(self):
        self.resolved("GorodkiSync", pin("swift-openapi-runtime", "1.12.1"))
        problems = check.check(self.root)
        self.assertEqual(len(problems), 1)
        self.assertIn("GRDB 7.11.1 закреплён, но ни одному пакету не нужен", problems[0])

    def test_branch_pin_fails(self):
        branch = pin("grdb.swift", "7.11.1")
        branch["state"] = {"branch": "master", "revision": "0" * 40}
        self.resolved("GorodkiPersistence", branch, pin("swift-openapi-runtime", "1.12.1"))
        problems = check.check(self.root)
        self.assertTrue(any("веткой или коммитом" in problem for problem in problems), problems)

    def test_repository_pins_match(self):
        """Настоящие ios/project.yml и Package.resolved — то же, что проверяет ios-build."""
        ios_root = Path(__file__).resolve().parent.parent
        self.assertEqual(check.check(ios_root), [])


if __name__ == "__main__":
    unittest.main()
