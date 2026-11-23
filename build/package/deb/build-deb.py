#!/usr/bin/env python3
"""Build a Debian .deb from a staged root without dpkg-deb.

Layout expected:
  <stage>/DEBIAN/control
  <stage>/usr/...
The output is a standard ar archive with debian-binary, control.tar.gz and data.tar.gz.
"""

from __future__ import annotations

import argparse
import gzip
import io
import os
import subprocess
import sys
import tarfile
import time
from pathlib import Path


def add_tree(tar: tarfile.TarFile, root: str, arc_prefix: str, skip_debian: bool) -> None:
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        filenames.sort()
        if skip_debian and os.path.abspath(dirpath) == os.path.abspath(os.path.join(root, "DEBIAN")):
            dirnames[:] = []
            continue
        for name in dirnames + filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if skip_debian and (rel == "DEBIAN" or rel.startswith("DEBIAN/")):
                continue
            info = tar.gettarinfo(full, arcname=f"{arc_prefix}{rel}")
            info.uid = 0
            info.gid = 0
            info.uname = "root"
            info.gname = "root"
            if info.isdir():
                info.mode = 0o755
            elif rel in ("usr/bin/aiassistant", "usr/lib/aiassistant/AiAssistant.Host"):
                info.mode = 0o755
            else:
                info.mode = 0o644
            if info.isfile():
                with open(full, "rb") as handle:
                    tar.addfile(info, handle)
            else:
                tar.addfile(info)


def gzip_tar(build) -> bytes:
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT) as tar:
        build(tar)
    compressed = io.BytesIO()
    with gzip.GzipFile(fileobj=compressed, mode="wb", mtime=0) as gz:
        gz.write(raw.getvalue())
    return compressed.getvalue()


def ar_member(name: str, data: bytes) -> bytes:
    header = (
        f"{name:<16}"
        f"{int(time.time()):<12}"
        f"{0:<6}"
        f"{0:<6}"
        f"{0o100644:<8o}"
        f"{len(data):<10}"
        "`\n"
    ).encode("ascii")
    padding = b"\n" if len(data) % 2 else b""
    return header + data + padding


def repo_root() -> Path:
    """Resolve the repository root via git; fall back to the package directory layout."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
        )
        return Path(result.stdout.strip()).resolve()
    except (OSError, subprocess.CalledProcessError):
        return Path(__file__).resolve().parents[3]


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Build a Debian .deb from a staged root without dpkg-deb.",
        epilog="Exit codes: 0 success / 1 build failure / 2 usage error.",
    )
    parser.add_argument("stage", help="staged root containing DEBIAN/control")
    parser.add_argument("output", help="output .deb path")
    parser.add_argument("--repo", default=None, help="repository root (default: git rev-parse)")
    parser.add_argument("--dry-run", action="store_true", help="validate inputs without writing the .deb")
    args = parser.parse_args()

    root = Path(args.repo).resolve() if args.repo else repo_root()
    stage = Path(args.stage)
    if not stage.is_absolute():
        stage = (Path.cwd() / stage).resolve() if Path.cwd() != root else (root / stage).resolve()
    output = Path(args.output)
    if not output.is_absolute():
        output = (root / output).resolve()
    control_dir = os.path.join(stage, "DEBIAN")
    if not os.path.isfile(os.path.join(control_dir, "control")):
        print(f"missing control file: {control_dir}/control", file=sys.stderr)
        return 1

    if args.dry_run:
        print(f"[dry-run] would write deb: {output} from {stage}")
        return 0

    try:
        control = gzip_tar(lambda tar: add_tree(tar, control_dir, "./", skip_debian=False))
        data = gzip_tar(lambda tar: add_tree(tar, stage, "./", skip_debian=True))
        with open(output, "wb") as handle:
            handle.write(b"!<arch>\n")
            handle.write(ar_member("debian-binary", b"2.0\n"))
            handle.write(ar_member("control.tar.gz", control))
            handle.write(ar_member("data.tar.gz", data))
    except OSError as exc:
        print(f"deb build failed: {exc}", file=sys.stderr)
        return 1
    print(f"deb: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
