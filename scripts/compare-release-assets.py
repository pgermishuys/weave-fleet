#!/usr/bin/env python3
"""Compares a release dry run's assets with a real release's, without publishing anything.

Usage: compare-release-assets.py <assets-dir> <version> <previous-tag> [--report <file.md>]

<assets-dir> is what the Release workflow's publish job assembled (the files it would upload); <version> is the
version stamped on them (0.72.0); <previous-tag> is a published release to compare with (v0.72.0). It reads the
previous release through `gh` (GH_TOKEN, GH_REPO) and downloads its CLI archives, .deb, Mac .zip and Windows
installers into a temporary folder.

Fails (exit 1) when an asset is missing or extra once versions are lined up, or when an installer or archive is under
80% or over 125% of the previous size. Lists every file added to or removed from each package (the CLI archives, the
.deb, the Mac .zip and the Windows installers' app archive), with versions and the client's content hashes lined up;
those differences are printed for a person to judge, they don't fail the check.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path

SIZE_LOW, SIZE_HIGH = 0.80, 1.25
# Assets whose size is checked: the packages people download. Small text files (feeds, checksums, scripts) aren't.
SIZED = re.compile(r"\.(tar\.gz|zip|deb|AppImage|dmg|exe)$")
# Vite's content hashes (index-B2x9_kQe.js) and electron-builder's per-build ids change every build.
HASHED = re.compile(r"-[A-Za-z0-9_-]{8}(?=\.(?:js|css|woff2?|svg|png|webp|wasm|map)$)")


def gh_json(*args: str):
    return json.loads(subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout)


def normalise(name: str, version: str) -> str:
    return name.replace(f"v{version}", "v<version>").replace(version, "<version>")


def listing(path: Path, work: Path) -> list[str] | None:
    """The files inside a package, or None for a kind it can't read."""
    name = path.name
    if name.endswith(".tar.gz"):
        with tarfile.open(path) as archive:
            return [m.name for m in archive.getmembers() if m.isfile() or m.issym()]
    if name.endswith(".zip"):
        with zipfile.ZipFile(path) as archive:
            return [n for n in archive.namelist() if not n.endswith("/")]
    if name.endswith(".deb") and shutil.which("dpkg-deb"):
        out = subprocess.run(["dpkg-deb", "-c", str(path)], check=True, capture_output=True, text=True).stdout
        return [line.split(None, 5)[5].split(" -> ")[0] for line in out.splitlines() if not line.startswith("d")]
    if name.endswith("-setup.exe") and shutil.which("7z"):
        # NSIS installer: the app is a .7z inside $PLUGINSDIR.
        unpack = work / f"nsis-{path.stem}"
        subprocess.run(["7z", "x", "-y", f"-o{unpack}", str(path)], check=True, capture_output=True)
        inner = next(unpack.rglob("app-*.7z"), None)
        if inner is None:
            return None
        out = subprocess.run(["7z", "l", "-slt", str(inner)], check=True, capture_output=True, text=True).stdout
        files, current, is_dir = [], None, False
        for line in out.splitlines() + [""]:
            if line.startswith("Path = "):
                current = line[7:]
            elif line.startswith("Attributes = "):
                is_dir = line[13:].startswith("D")
            elif not line and current:
                if not is_dir and current != str(inner):
                    files.append(current.replace("\\", "/"))
                current, is_dir = None, False
        return files
    return None


def tidy(entries: list[str], version: str) -> set[str]:
    return {HASHED.sub("-<hash>", normalise(e, version)) for e in entries}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("assets_dir", type=Path)
    parser.add_argument("version")
    parser.add_argument("previous_tag")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()

    version, prev_tag = args.version, args.previous_tag
    prev_version = prev_tag.removeprefix("v")
    repo = os.environ["GH_REPO"]
    lines: list[str] = []
    failures: list[str] = []

    def say(text: str = "") -> None:
        print(text)
        lines.append(text)

    release = gh_json("api", f"repos/{repo}/releases/tags/{prev_tag}")
    prev_assets = {a["name"]: a for a in release["assets"]}
    new_assets = {p.name: p for p in args.assets_dir.iterdir() if p.is_file()}

    prev_by_key = {normalise(n, prev_version): n for n in prev_assets}
    new_by_key = {normalise(n, version): n for n in new_assets}

    say(f"## Dry run {version} compared with {prev_tag}")
    say()
    missing = sorted(set(prev_by_key) - set(new_by_key))
    extra = sorted(set(new_by_key) - set(prev_by_key))
    for key in missing:
        failures.append(f"missing asset: {key} ({prev_tag} has {prev_by_key[key]})")
    for key in extra:
        failures.append(f"asset {prev_tag} doesn't have: {new_by_key[key]}")
    say(f"{len(new_assets)} assets, {prev_tag} has {len(prev_assets)}; "
        f"{len(missing)} missing, {len(extra)} extra once versions are lined up.")
    say()
    say(f"| Asset | {prev_tag} | Dry run | Ratio |")
    say("|---|---:|---:|---:|")
    for key in sorted(set(prev_by_key) & set(new_by_key)):
        if not SIZED.search(key):
            continue
        old = prev_assets[prev_by_key[key]]["size"]
        new = new_assets[new_by_key[key]].stat().st_size
        ratio = new / old if old else float("inf")
        flag = "" if SIZE_LOW <= ratio <= SIZE_HIGH else " **out of range**"
        if flag:
            failures.append(f"{new_by_key[key]} is {new} bytes, {ratio:.2f}x {prev_tag}'s {old}")
        say(f"| {new_by_key[key]} | {old / 1048576:.1f} MB | {new / 1048576:.1f} MB | {ratio:.3f}{flag} |")
    say()

    with tempfile.TemporaryDirectory(prefix="release-compare-") as tmp:
        work = Path(tmp)
        for key in sorted(set(prev_by_key) & set(new_by_key)):
            if not re.search(r"(\.tar\.gz|\.zip|\.deb|-setup\.exe)$", key) or "<version>" not in key:
                continue  # the unversioned copies are byte-for-byte the versioned ones
            prev_name, new_name = prev_by_key[key], new_by_key[key]
            prev_path = work / "previous" / prev_name
            prev_path.parent.mkdir(exist_ok=True)
            subprocess.run(["gh", "release", "download", prev_tag, "--repo", repo, "--pattern", prev_name,
                            "--dir", str(prev_path.parent), "--clobber"], check=True, capture_output=True)
            old_list = listing(prev_path, work)
            new_list = listing(new_assets[new_name], work)
            if old_list is None or new_list is None:
                say(f"### {new_name}: contents not compared (no tool for this kind here)")
                continue
            old_set, new_set = tidy(old_list, prev_version), tidy(new_list, version)
            added, removed = sorted(new_set - old_set), sorted(old_set - new_set)
            say(f"### {new_name}: {len(new_set)} files, {prev_tag} {len(old_set)}; "
                f"{len(added)} added, {len(removed)} removed")
            for entry in added[:40]:
                say(f"    + {entry}")
            for entry in removed[:40]:
                say(f"    - {entry}")
            if len(added) > 40 or len(removed) > 40:
                say("    … (cut at 40 each)")
            prev_path.unlink()

    say()
    if failures:
        say("### Failed")
        for failure in failures:
            say(f"- {failure}")
    else:
        say("Every asset is there, every package within size range.")
    if args.report:
        args.report.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
