import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "sync-release-metadata.py"


class ReleaseSyncTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="gxmcp-release-sync-"))
        (self.root / "config").mkdir()
        (self.root / "docs" / "generated").mkdir(parents=True)
        (self.root / "src" / "nexus-ide").mkdir(parents=True)
        self._write_json(
            "config/gx-versions.json",
            {
                "primaryMajor": "18",
                "supportedMajors": [
                    {"major": "17", "displayName": "GeneXus 17", "defaultInstallPath": r"C:\GX17"},
                    {"major": "18", "displayName": "GeneXus 18", "defaultInstallPath": r"C:\GX18"},
                ],
            },
        )
        self._write_json("package.json", {"name": "genexus-mcp", "version": "3.0.0", "description": "old"})
        self._write_json("server.json", {"name": "fixture", "version": "2.0.0", "description": "old", "packages": [{"version": "2.0.0"}]})
        self._write_json("config.sample.json", {"GeneXus": {"InstallationPath": r"C:\Old"}})
        block = "\n".join(
            [
                "<!-- BEGIN GENERATED: gx-compatibility -->",
                "old",
                "<!-- END GENERATED: gx-compatibility -->",
            ]
        )
        (self.root / "README.md").write_text("before\n" + block + "\nafter\n", encoding="utf-8")
        (self.root / "AGENTS.md").write_text("before\n" + block + "\nafter\n", encoding="utf-8")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def _write_json(self, relative, document):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(document), encoding="utf-8")

    def _run(self, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(self.root), "--version", "3.0.1", *args],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_write_is_idempotent_and_check_detects_drift(self):
        first = self._run("--write")
        self.assertEqual(0, first.returncode, first.stderr)
        snapshot = {
            path.relative_to(self.root): path.read_bytes()
            for path in self.root.rglob("*")
            if path.is_file()
        }

        second = self._run("--write")
        self.assertEqual(0, second.returncode, second.stderr)
        current = {
            path.relative_to(self.root): path.read_bytes()
            for path in self.root.rglob("*")
            if path.is_file()
        }
        self.assertEqual(snapshot, current)
        self.assertEqual(0, self._run("--check").returncode)

        server = json.loads((self.root / "server.json").read_text(encoding="utf-8"))
        self.assertEqual("3.0.1", server["version"])
        self.assertEqual("3.0.1", server["packages"][0]["version"])
        self.assertEqual(r"C:\GX18", json.loads((self.root / "config.sample.json").read_text(encoding="utf-8"))["GeneXus"]["InstallationPath"])
        self.assertIn(r"| 18 | GeneXus 18 | `C:\GX18` |", (self.root / "docs" / "generated" / "supported-versions.md").read_text(encoding="utf-8"))

        (self.root / "README.md").write_text("ambiguous\n", encoding="utf-8")
        broken = self._run("--write")
        self.assertEqual(2, broken.returncode)
        self.assertIn("exactly one generated", broken.stderr)


if __name__ == "__main__":
    unittest.main()
