"""Verify/stage VigilWatt source and strict adaptation patches; never run upstream code."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1] / "Dependencies" / "VigilWatt"
MANIFEST = "source-manifest.json"
REPOSITORY = "https://github.com/evren-oct/vigil-watt"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_relative(value):
    path = PurePosixPath(value)
    if path.is_absolute() or any(p in ("..", ".") for p in path.parts) or "\\" in value or ":" in value:
        raise ValueError("unsafe_relative_path")
    return Path(*path.parts)


def read_bytes(root, relative):
    path = root / safe_relative(relative)
    if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
        raise ValueError("linked_source_path")
    return path.read_bytes()


def source_text(data):
    # Preserve an original BOM as patch context. Adapted files deliberately use LF.
    return data.decode("utf-8").replace("\r\n", "\n").splitlines(keepends=True)


def apply_patch(source, patch):
    """Exact unified patch application: no fuzzy matching or dropped hunks."""
    original = source_text(source)
    lines = patch.decode("utf-8").splitlines(keepends=True)
    if len(lines) < 3 or not lines[0].startswith("--- a/") or not lines[1].startswith("+++ b/"):
        raise ValueError("invalid_patch_header")
    output, cursor, index = [], 0, 2
    while index < len(lines):
        match = re.fullmatch(r"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@\n?", lines[index])
        if not match:
            raise ValueError("invalid_patch_hunk")
        start = int(match[1]) - (1 if int(match[1]) else 0)
        old_count = int(match[2]) if match[2] is not None else 1
        new_count = int(match[4]) if match[4] is not None else 1
        if start < cursor or start > len(original):
            raise ValueError("patch_position_conflict")
        output.extend(original[cursor:start])
        cursor = start
        consumed = produced = 0
        index += 1
        while index < len(lines) and not lines[index].startswith("@@ "):
            line = lines[index]
            if not line or line[0] not in " +-":
                raise ValueError("unsupported_patch_line")
            kind, text = line[0], line[1:]
            if kind in " -":
                if cursor >= len(original) or original[cursor] != text:
                    raise ValueError("patch_context_conflict")
                cursor += 1
                consumed += 1
            if kind in " +":
                output.append(text)
                produced += 1
            index += 1
        if consumed != old_count or produced != new_count:
            raise ValueError("patch_hunk_count_conflict")
    output.extend(original[cursor:])
    return "".join(output).encode("utf-8")


def verify_baseline(root):
    manifest = json.loads(read_bytes(root, MANIFEST))
    if manifest["repository"] != REPOSITORY or not re.fullmatch(r"[0-9a-f]{40}", manifest["commit"]):
        raise ValueError("invalid_source_manifest")
    actual = {p.relative_to(root / "upstream").as_posix()
              for p in (root / "upstream").rglob("*") if p.is_file()}
    if actual != set(manifest["files"]):
        raise ValueError("baseline_inventory_changed")
    for relative, expected in manifest["files"].items():
        if digest(read_bytes(root / "upstream", relative)) != expected:
            raise ValueError("baseline_hash_changed: " + relative)
    for relative, adapted in manifest["adapted"].items():
        patch = read_bytes(root, adapted["patch"])
        current = read_bytes(root / "Adapted", relative)
        if digest(patch) != adapted["patchSha256"] or digest(current) != adapted["sha256"]:
            raise ValueError("adaptation_hash_changed: " + relative)
        rebuilt = apply_patch(read_bytes(root / "upstream", adapted["source"]), patch)
        if rebuilt != current:
            raise ValueError("adaptation_patch_mismatch: " + relative)
    actual_adapted = {p.relative_to(root / "Adapted").as_posix()
                      for p in (root / "Adapted").rglob("*.cs")}
    if actual_adapted != set(manifest["adapted"]):
        raise ValueError("adaptation_inventory_changed")
    generated = root / "admitted-plugins.json"
    if generated.exists():
        registry = root.parents[1] / "PeripheralHost" / "PluginRegistry.cs"
        data = json.loads(generated.read_bytes())
        actual_types = re.findall(r"new (VigilWatt\.Core\.Plugins\.[\w.]+)\(\)", registry.read_text(encoding="utf-8"))
        if (data["registrySha256"] != digest(registry.read_bytes()) or data["commit"] != manifest["commit"]
                or [p["type"] for p in data["plugins"]] != actual_types):
            raise ValueError("admitted_registry_metadata_stale")
    return manifest


def fetch_sources(commit, manifest):
    url = "https://codeload.github.com/evren-oct/vigil-watt/zip/" + commit
    with urllib.request.urlopen(url, timeout=60) as response:
        data = response.read(64 * 1024 * 1024 + 1)
    if len(data) > 64 * 1024 * 1024:
        raise ValueError("archive_too_large")
    selected = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) > 10000 or sum(e.file_size for e in entries) > 256 * 1024 * 1024:
            raise ValueError("archive_expansion_too_large")
        prefix = "vigil-watt-" + commit + "/"
        for entry in entries:
            if entry.is_dir():
                continue
            if not entry.filename.startswith(prefix):
                raise ValueError("unexpected_archive_prefix")
            relative = entry.filename[len(prefix):]
            safe_relative(relative)
            if (relative.startswith("src/VigilWatt.Core/") or relative.startswith("docs/hardware/")
                    or relative in manifest["files"]):
                if relative in selected:
                    raise ValueError("duplicate_archive_entry")
                selected[relative] = archive.read(entry)
    # A removed selected file is an explicit review event, never silently dropped.
    if not set(manifest["files"]).issubset(selected):
        raise ValueError("selected_upstream_file_missing")
    return selected


def stage_update(root, stage, commit, manifest):
    selected = fetch_sources(commit, manifest)
    updated = json.loads(json.dumps(manifest))
    updated["commit"] = commit
    updated["files"] = {p: digest(data) for p, data in sorted(selected.items())}
    added = sorted(set(selected) - set(manifest["files"]))
    changed = sorted(p for p in selected if p in manifest["files"]
                     and digest(selected[p]) != manifest["files"][p])
    print(f"Upstream comparison {manifest['commit']} -> {commit}: {len(added)} added, {len(changed)} changed, 0 removed")
    for label, paths in (("added", added), ("changed", changed)):
        for relative in paths[:40]:
            print(f"  {label}: {relative}")
        if len(paths) > 40:
            print(f"  {label}: ... {len(paths) - 40} more paths (inspect staged source before acceptance)")
    for relative, data in selected.items():
        target = stage / "upstream" / safe_relative(relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    for relative, adapted in updated["adapted"].items():
        # Fail before replacing anything if even one safety/identity patch conflicts.
        data = apply_patch(selected[adapted["source"]], read_bytes(root, adapted["patch"]))
        target = stage / "Adapted" / safe_relative(relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        adapted["sha256"] = digest(data)
    (stage / MANIFEST).write_text(json.dumps(updated, indent=2) + "\n", encoding="utf-8")
    return updated


def required_follow_up(previous, candidate):
    if previous == candidate:
        return
    print("Required integration review after changing the upstream commit:")
    for item in (
        "Review actual admitted PluginRegistry and every adaptation diff; regenerate Dependencies/VigilWatt/admitted-plugins.json and COMPATIBILITY.md.",
        "Update source revision in Media/Peripherals/PeripheralDiagnostics.cs, docs/外设电量.md, and Dependencies/VigilWatt/README.md.",
        "Update the VigilWatt entry and notice in licenses/MIT.txt and keep upstream/LICENSE and upstream/CONTRIBUTORS.md byte-for-byte in Dependencies/VigilWatt/upstream/.",
        "Rebuild host and run --self-test, lifecycle_test.py, synchronization checks, and integrated package acceptance.",
    ):
        print("  - " + item)


def install_stage(root, stage):
    # Same-volume renames; roll back all completed replacements on any failure.
    replaced = []
    try:
        for name in ("upstream", "Adapted", MANIFEST):
            old, new, backup = root / name, stage / name, stage / (name + ".baseline")
            old.rename(backup)
            try:
                new.rename(old)
            except BaseException:
                backup.rename(old)
                raise
            replaced.append((old, backup))
    except BaseException:
        for old, backup in reversed(replaced):
            retired = stage / (old.name + ".rejected")
            old.rename(retired)
            backup.rename(old)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--ref")
    args = parser.parse_args()
    if not args.check and not args.ref:
        parser.error("specify --check or --ref <40sha>")
    if args.ref and not re.fullmatch(r"[0-9a-fA-F]{40}", args.ref):
        parser.error("--ref requires a full 40-character commit SHA")
    manifest = verify_baseline(ROOT)
    if args.ref:
        with tempfile.TemporaryDirectory(prefix=".sync-", dir=ROOT) as directory:
            stage = Path(directory)
            stage_update(ROOT, stage, args.ref.lower(), manifest)
            required_follow_up(manifest["commit"], args.ref.lower())
            if not args.check:
                install_stage(ROOT, stage)
    print("VigilWatt source hashes and adaptation patches verified" + ("; candidate staged successfully" if args.ref else ""))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        raise SystemExit("VigilWatt sync failed: " + str(error))
