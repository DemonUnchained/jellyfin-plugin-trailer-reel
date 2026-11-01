#!/usr/bin/env python3
"""Native Windows/Linux validation and packaging. Does not publish or deploy."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import venv
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/Jellyfin.Plugin.TrailerReel/Jellyfin.Plugin.TrailerReel.csproj"
TEST = ROOT / "tests/Jellyfin.Plugin.TrailerReel.Tests/Jellyfin.Plugin.TrailerReel.Tests.csproj"
COMPAT = ROOT / "tests/Jellyfin.Plugin.TrailerReel.Compatibility/Jellyfin.Plugin.TrailerReel.Compatibility.csproj"
LOCAL = ROOT / ".codex-local"
YTDLP_VERSION = "2026.08.19"


def run(arguments, label, report, env=None):
    print(f"\n{label}", flush=True)
    start = time.monotonic()
    result = subprocess.run([str(arg) for arg in arguments], cwd=ROOT, env=env)
    report["steps"].append({"name": label, "exit_code": result.returncode, "seconds": round(time.monotonic() - start, 2)})
    if result.returncode:
        raise RuntimeError(f"{label} failed (exit {result.returncode}).")


def find_dotnet():
    local_sdk = LOCAL / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    if local_sdk.is_file():
        return str(local_sdk)
    command = shutil.which("dotnet")
    if command:
        return command
    raise RuntimeError(".NET SDK not found. Install the SDK version in global.json on PATH or under .codex-local/dotnet, then rerun the build.")


def symlink_probe():
    LOCAL.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="symlink-", dir=LOCAL) as temp:
        target = Path(temp) / "target.txt"
        target.write_text("fixture", encoding="utf-8")
        try:
            (Path(temp) / "alias.txt").symlink_to("target.txt")
        except OSError as error:
            raise RuntimeError("Symbolic links are required by the existing indexing tests. On Windows, enable Developer Mode in Settings, then rerun. An existing WSL checkout is another option; do not remove the tests.") from error


def test_python(report, env):
    directory = LOCAL / "test-venv"
    executable = directory / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    if not executable.is_file():
        venv.EnvBuilder(with_pip=True).create(directory)
    probe = subprocess.run([str(executable), "-c", "from yt_dlp.version import __version__; print(__version__)"], capture_output=True, text=True)
    if probe.returncode or probe.stdout.strip() != YTDLP_VERSION:
        run([executable, "-m", "pip", "install", "--disable-pip-version-check", "--no-deps", f"yt-dlp=={YTDLP_VERSION}"], "Install pinned selector-test dependency", report, env)
    return executable


def zip_files(path, files):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for source, name in files:
            archive.write(source, name)
    with zipfile.ZipFile(path) as archive:
        failed = archive.testzip()
        if failed:
            raise RuntimeError(f"ZIP CRC check failed: {failed}")


def source_files():
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode("utf-8").split("\0")
    tracked = {name for name in tracked if name}
    manifest_path = ROOT / "docs/development/HANDOFF_FILES.json"
    handoff = set(json.loads(manifest_path.read_text(encoding="utf-8"))["overlay_files"]) if manifest_path.exists() else set()
    untracked = set(filter(None, subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode("utf-8").split("\0")))
    unexpected = untracked - handoff
    if unexpected:
        raise RuntimeError("Source packaging found untracked files outside the handoff overlay. Stage the intended source files explicitly, or use --no-package for validation. Files: " + ", ".join(sorted(unexpected)))
    result = []
    for name in sorted(tracked | handoff):
        path = ROOT / name
        if not path.exists():
            continue  # A tracked deletion must remain deleted in the source ZIP.
        if path.is_symlink() or not path.is_file():
            raise RuntimeError(f"Review this source entry before packaging: {name}")
        if not path.resolve().is_relative_to(ROOT.resolve()):
            raise RuntimeError(f"Source path escaped repository: {name}")
        result.append((path, f"jellyfin-plugin-trailer-reel/{name}"))
    return result


def package(version, report):
    meta_path = PROJECT.parent / "meta.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    if meta["version"] != version:
        raise RuntimeError("Project version and meta.json version differ.")
    output = PROJECT.parent / "bin/Release/net10.0"
    runtime = [(output / "Jellyfin.Plugin.TrailerReel.dll", "Jellyfin.Plugin.TrailerReel.dll"),
               (output / "Jellyfin.Plugin.TrailerReel.pdb", "Jellyfin.Plugin.TrailerReel.pdb"),
               (meta_path, "meta.json")]
    source = source_files()  # Validate the complete input before writing packages.
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="package-", dir=LOCAL) as temp:
        stage = Path(temp)
        catalog = stage / f"trailer-reel_{version}.zip"
        manual = stage / f"trailer-reel_{version}-manual.zip"
        sources = stage / f"jellyfin-trailer-reel-source_{version}.zip"
        zip_files(catalog, runtime)
        prefix = f"TrailerReel_{version}/"
        manual_files = [(path, prefix + name) for path, name in runtime]
        manual_files += [(ROOT / name, prefix + name) for name in ("README.md", "INSTALL-UNRAID.md", "LICENSE")]
        for directory in ("tools", "deploy"):
            manual_files += [(path, prefix + path.relative_to(ROOT).as_posix()) for path in sorted((ROOT / directory).rglob("*")) if path.is_file() and "__pycache__" not in path.parts]
        zip_files(manual, manual_files)
        zip_files(sources, source)
        with zipfile.ZipFile(catalog) as archive:
            if set(archive.namelist()) != {"Jellyfin.Plugin.TrailerReel.dll", "Jellyfin.Plugin.TrailerReel.pdb", "meta.json"}:
                raise RuntimeError("Catalog ZIP must contain exactly three root runtime files.")
        packages = []
        for path in (catalog, manual, sources):
            packages.append({"name": path.name, "size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        checksums = stage / f"SHA256SUMS-{version}.txt"
        checksums.write_text("".join(f"{entry['sha256']}  {entry['name']}\n" for entry in packages), encoding="utf-8")
        for path in (catalog, manual, sources, checksums):
            os.replace(path, dist / path.name)
        report["packages"] = packages
    print(f"\nValidated packages: {dist}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-package", action="store_true")
    parser.add_argument("--compatibility-version", default="12.4.0")
    args = parser.parse_args()
    if sys.version_info < (3, 10):
        parser.error("Python 3.10+ is required.")
    LOCAL.mkdir(exist_ok=True)
    report = {"started_utc": datetime.now(timezone.utc).isoformat(), "success": False, "steps": [], "compatibility_version": args.compatibility_version}
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    try:
        symlink_probe()
        dotnet = find_dotnet()
        # A project-local SDK must resolve its own host/runtime without global installation.
        env["DOTNET_ROOT"] = str(Path(dotnet).resolve().parent)
        env["PATH"] = str(Path(dotnet).resolve().parent) + os.pathsep + env.get("PATH", "")
        report["commit"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        version = ET.parse(PROJECT).findtext("./PropertyGroup/Version")
        report["plugin_version"] = version
        run([dotnet, "build", TEST, "-c", "Release"], "Build production plugin and regression executable", report, env)
        run([dotnet, TEST.parent / "bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.Tests.dll"], "Run .NET regressions", report, env)
        python = test_python(report, env)
        run([python, TEST.parent / "format_selection_test.py"], "Run real yt-dlp selectors offline", report, env)
        run([dotnet, "msbuild", COMPAT, "-restore", "-target:Build", "-property:Configuration=Release", f"-property:JellyfinVersion={args.compatibility_version}"], "Build isolated Jellyfin compatibility host", report, env)
        tested_dll = COMPAT.parent / "bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.dll"
        production_dll = PROJECT.parent / "bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.dll"
        if tested_dll.read_bytes() != production_dll.read_bytes():
            raise RuntimeError("Compatibility host contains a different plugin DLL than the production build.")
        report["tested_dll_sha256"] = hashlib.sha256(tested_dll.read_bytes()).hexdigest()
        host_version = args.compatibility_version + ".0" if args.compatibility_version.count(".") == 2 else args.compatibility_version
        run([dotnet, COMPAT.parent / "bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.Compatibility.dll", host_version], "Run plugin binary compatibility checks", report, env)
        if not args.no_package:
            package(version, report)
        report["success"] = True
        print("\nAll requested validation passed. No upload or server deployment was performed.", flush=True)
        return 0
    except (OSError, RuntimeError, subprocess.SubprocessError, ValueError) as error:
        report["error"] = str(error)
        print(f"\nERROR: {error}", file=sys.stderr)
        return 1
    finally:
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        (LOCAL / "last-build.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
