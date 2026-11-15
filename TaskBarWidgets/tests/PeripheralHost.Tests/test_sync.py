"""Offline synchronization regressions; fixtures stay under this test directory."""
import difflib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("vigil_sync", HERE.parents[1] / "scripts" / "sync_vigil_watt.py")
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class SyncTests(unittest.TestCase):
    def setUp(self):
        fixtures = HERE / "obj"
        fixtures.mkdir(exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="sync-test-", dir=fixtures)
        self.root = Path(self.directory.name)
        self.addCleanup(self.directory.cleanup)
        self.source = "src/VigilWatt.Core/Plugins/Fixture.cs"
        original, adapted = b"before\nkeep\n", b"after\nkeep\n"
        patch = "".join(difflib.unified_diff(original.decode().splitlines(keepends=True),
                                           adapted.decode().splitlines(keepends=True),
                                           fromfile="a/" + self.source, tofile="b/" + self.source)).encode()
        values = {"upstream/" + self.source: original, "Adapted/Plugins/Fixture.cs": adapted,
                  "patches/Plugins/Fixture.cs.patch": patch}
        for relative, data in values.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.manifest = {"repository": sync.REPOSITORY, "commit": "a" * 40,
                         "files": {self.source: sync.digest(original)},
                         "adapted": {"Plugins/Fixture.cs": {"source": self.source,
                             "sha256": sync.digest(adapted), "patch": "patches/Plugins/Fixture.cs.patch",
                             "patchSha256": sync.digest(patch)}}}
        (self.root / sync.MANIFEST).write_text(json.dumps(self.manifest), encoding="utf-8")

    def test_real_import_and_every_patch_replay(self):
        manifest = sync.verify_baseline(sync.ROOT)
        self.assertEqual(manifest["commit"], "4df830043296760f5d5c273f4cdc0ea9456c4bc7")
        self.assertEqual(len(manifest["files"]), 307)
        self.assertGreaterEqual(len(manifest["adapted"]), 15)

    def test_original_hash_tampering_fails(self):
        (self.root / "upstream" / self.source).write_bytes(b"tampered\n")
        with self.assertRaisesRegex(ValueError, "baseline_hash_changed"):
            sync.verify_baseline(self.root)

    def test_uninventoried_original_fails(self):
        (self.root / "upstream" / "extra.cs").write_bytes(b"extra\n")
        with self.assertRaisesRegex(ValueError, "baseline_inventory_changed"):
            sync.verify_baseline(self.root)

    def test_adaptation_or_patch_tampering_fails(self):
        (self.root / "Adapted/Plugins/Fixture.cs").write_bytes(b"unchecked adaptation\n")
        with self.assertRaisesRegex(ValueError, "adaptation_hash_changed"):
            sync.verify_baseline(self.root)

    def test_patch_context_conflict_does_not_touch_baseline(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(sync, "fetch_sources", return_value={self.source: b"incompatible\nkeep\n"}):
            with self.assertRaisesRegex(ValueError, "patch_context_conflict"):
                sync.stage_update(self.root, stage, "b" * 40, self.manifest)
        self.assertEqual((self.root / "upstream" / self.source).read_bytes(), b"before\nkeep\n")
        self.assertEqual(sync.verify_baseline(self.root)["commit"], "a" * 40)

    def test_staged_update_preserves_adaptation(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(sync, "fetch_sources", return_value={self.source: b"before\nkeep\n"}):
            candidate = sync.stage_update(self.root, stage, "b" * 40, self.manifest)
        self.assertEqual(candidate["commit"], "b" * 40)
        self.assertEqual((stage / "Adapted/Plugins/Fixture.cs").read_bytes(), b"after\nkeep\n")
        self.assertEqual(sync.verify_baseline(self.root)["commit"], "a" * 40)

    def test_replacement_failure_rolls_back_all_paths(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(sync, "fetch_sources", return_value={self.source: b"before\nkeep\n"}):
            sync.stage_update(self.root, stage, "b" * 40, self.manifest)
        rename = Path.rename

        def fail_adapted_install(path, target):
            if path == stage / "Adapted":
                raise OSError("synthetic_rename_failure")
            return rename(path, target)

        with mock.patch.object(Path, "rename", fail_adapted_install):
            with self.assertRaisesRegex(OSError, "synthetic_rename_failure"):
                sync.install_stage(self.root, stage)
        self.assertEqual(sync.verify_baseline(self.root)["commit"], "a" * 40)

    def test_unsafe_paths_are_rejected(self):
        for path in ("../outside", "/absolute", "C:/absolute", "folder\\file"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                sync.safe_relative(path)


if __name__ == "__main__":
    unittest.main(verbosity=2)
