"""Тесты export-screenshots.py: запуск — python3 -m unittest discover -s ios/scripts -p 'test_*.py'.

Сам xcresulttool есть только на Mac, поэтому здесь — разбор его выгрузки (папка с manifest.json), которую делает шаг
ios-snapshots.yml.
"""

import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "export-screenshots.py"
spec = importlib.util.spec_from_file_location("export_screenshots", SCRIPT)
export = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export)

UUID = "5B7C0D1E-2F30-4A5B-8C9D-0E1F2A3B4C5D"


class ExportScreenshotsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.exported = Path(self.temp.name) / "exported"
        self.exported.mkdir()
        self.out = Path(self.temp.name) / "screenshots"

    def tearDown(self):
        self.temp.cleanup()

    def attachment(self, file_name, suggested, failure=False):
        (self.exported / file_name).write_bytes(b"\x89PNG")
        return {
            "exportedFileName": file_name,
            "suggestedHumanReadableName": suggested,
            "isAssociatedWithFailure": failure,
        }

    def manifest(self, *tests):
        (self.exported / "manifest.json").write_text(json.dumps(list(tests)), encoding="utf-8")

    def collect(self):
        with redirect_stdout(io.StringIO()):
            return [path.name for path in export.collect(self.exported, self.out)]

    def test_names_come_from_attachments_in_order(self):
        self.manifest(
            {
                "testIdentifier": "ScreenSnapshotTests/test02Lab()",
                "attachments": [self.attachment("B.png", f"03-lab_0_{UUID}.png")],
            },
            {
                "testIdentifier": "ScreenSnapshotTests/test01RootAndInstallCheck()",
                "attachments": [
                    self.attachment("C.png", f"01-root_0_{UUID}.png"),
                    self.attachment("A.png", f"02-install-check_1_{UUID}.png"),
                ],
            },
        )
        self.assertEqual(self.collect(), ["01-root.png", "02-install-check.png", "03-lab.png"])

    def test_failure_screenshots_are_marked(self):
        self.manifest({"attachments": [self.attachment("F.png", f"Screenshot at failure_0_{UUID}.png", True)]})
        self.assertEqual(self.collect(), ["failure-Screenshot-at-failure.png"])

    def test_same_names_do_not_overwrite(self):
        self.manifest(
            {"attachments": [self.attachment("A.png", f"05-lab-map_0_{UUID}.png")]},
            {"attachments": [self.attachment("B.png", f"05-lab-map_0_{UUID}.png")]},
        )
        self.assertEqual(self.collect(), ["05-lab-map-2.png", "05-lab-map.png"])

    def test_non_images_are_skipped(self):
        (self.exported / "log.txt").write_text("журнал", encoding="utf-8")
        self.manifest(
            {
                "attachments": [
                    {"exportedFileName": "log.txt", "suggestedHumanReadableName": "log.txt"},
                    {"exportedFileName": "missing.png", "suggestedHumanReadableName": "missing.png"},
                    self.attachment("A.png", f"01-root_0_{UUID}.png"),
                ]
            }
        )
        self.assertEqual(self.collect(), ["01-root.png"])

    def test_without_manifest_images_keep_export_names(self):
        (self.exported / "X.png").write_bytes(b"\x89PNG")
        self.assertEqual(self.collect(), ["X.png"])

    def test_unknown_manifest_format_falls_back_to_files(self):
        (self.exported / "manifest.json").write_text('{"attachments": {}}', encoding="utf-8")
        (self.exported / "Y.png").write_bytes(b"\x89PNG")
        self.assertEqual(self.collect(), ["Y.png"])

    def test_no_images_is_an_error(self):
        self.manifest({"attachments": []})
        with redirect_stdout(io.StringIO()) as output:
            code = export.main(["--exported", str(self.exported), str(self.out)])
        self.assertEqual(code, 1)
        self.assertIn("::error::", output.getvalue())

    def test_missing_result_bundle_is_an_error(self):
        with redirect_stdout(io.StringIO()) as output:
            code = export.main([str(Path(self.temp.name) / "none.xcresult"), str(self.out)])
        self.assertEqual(code, 1)
        self.assertIn("UI-тесты не запустились", output.getvalue())


if __name__ == "__main__":
    unittest.main()
