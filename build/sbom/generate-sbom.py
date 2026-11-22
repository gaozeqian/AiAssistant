#!/usr/bin/env python3
"""Offline CycloneDX 1.5 SBOM generator for AiAssistant.

Reads the NuGet restore graph (`obj/project.assets.json`) instead of shelling out to an external
CycloneDX tool, so SBOM generation works in air-gapped and low-privilege environments.

Usage:
    python build/sbom/generate-sbom.py                       # artifacts/build/sbom/bom.json
    python build/sbom/generate-sbom.py --verify              # fail on malformed output
    python build/sbom/generate-sbom.py --check-manifest      # every CPM package documented
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

NAMESPACE = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
ARTIFACTS_ROOT = Path(os.environ.get("AIASSISTANT_ARTIFACTS_ROOT", "artifacts"))
DEFAULT_OUTPUT = ARTIFACTS_ROOT / "build" / "sbom" / "bom.json"


def read_version(repo: Path) -> str:
    props = (repo / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props)
    return match.group(1).strip() if match else "0.0.0"


def collect_assets(repo: Path):
    components: dict[tuple[str, str], str | None] = {}
    dependencies: dict[str, set[str]] = {}
    direct: set[str] = set()

    for assets_path in repo.glob("**/obj/project.assets.json"):
        if ARTIFACTS_ROOT.name in assets_path.parts:
            continue
        try:
            data = json.loads(assets_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"warning: skipping {assets_path}: {exc}", file=sys.stderr)
            continue

        for key, library in data.get("libraries", {}).items():
            if library.get("type") != "package" or "/" not in key:
                continue
            name, _, version = key.partition("/")
            components[(name, version)] = library.get("sha512")

        for target in data.get("targets", {}).values():
            for key, entry in target.items():
                if entry.get("type") != "package" or "/" not in key:
                    continue
                name, _, _ = key.partition("/")
                for dependency in (entry.get("dependencies") or {}):
                    dependencies.setdefault(name, set()).add(dependency)

        for group in data.get("projectFileDependencyGroups", {}).values():
            for dependency in group:
                direct.add(dependency.split(" ", 1)[0])

    return components, dependencies, direct


def sha512_hex(base64_digest: str | None) -> str | None:
    if not base64_digest:
        return None
    try:
        return base64.b64decode(base64_digest).hex()
    except (ValueError, TypeError):
        return None


def build_bom(repo: Path, version: str):
    components, dependencies, direct = collect_assets(repo)
    if not components:
        raise SystemExit("No NuGet components found. Run `dotnet restore AiAssistant.sln` first.")

    refs: dict[tuple[str, str], str] = {}
    for name, component_version in components:
        refs[(name, component_version)] = f"pkg:nuget/{name}@{component_version}"

    by_name: dict[str, list[str]] = {}
    for (name, _), ref in refs.items():
        by_name.setdefault(name, []).append(ref)

    component_entries = []
    for (name, component_version) in sorted(components):
        entry = {
            "type": "library",
            "bom-ref": refs[(name, component_version)],
            "name": name,
            "version": component_version,
            "purl": refs[(name, component_version)],
            "scope": "required",
        }
        digest = sha512_hex(components[(name, component_version)])
        if digest:
            entry["hashes"] = [{"alg": "SHA-512", "content": digest}]
        component_entries.append(entry)

    dependency_entries = []
    for (name, component_version) in sorted(components):
        ref = refs[(name, component_version)]
        depends_on = sorted(
            {candidate for dependency in dependencies.get(name, set()) for candidate in by_name.get(dependency, [])}
        )
        dependency_entries.append({"ref": ref, "dependsOn": depends_on})

    root_ref = f"pkg:generic/AiAssistant@{version}"
    root_depends = sorted(
        {candidate for name in direct for candidate in by_name.get(name, [])}
    )
    dependency_entries.insert(0, {"ref": root_ref, "dependsOn": root_depends})

    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    timestamp = (
        datetime.fromtimestamp(int(epoch), tz=timezone.utc)
        if epoch and epoch.isdigit()
        else datetime.now(tz=timezone.utc)
    ).strftime("%Y-%m-%dT%H:%M:%SZ")

    serial_seed = "|".join(sorted(refs.values())) + f"|{version}"
    serial = f"urn:uuid:{uuid.uuid5(NAMESPACE, serial_seed)}"

    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": serial,
        "version": 1,
        "metadata": {
            "timestamp": timestamp,
            "tools": [{"vendor": "AiAssistant", "name": "offline-sbom", "version": "1.0.0"}],
            "component": {
                "type": "application",
                "bom-ref": root_ref,
                "name": "AiAssistant",
                "version": version,
            },
            "properties": [
                {"name": "aiassistant:targetFramework", "value": "net10.0"},
                {"name": "aiassistant:packageCount", "value": str(len(component_entries))},
            ],
        },
        "components": component_entries,
        "dependencies": dependency_entries,
    }


def verify(bom: dict) -> list[str]:
    errors: list[str] = []
    components = bom.get("components", [])
    if len(components) < 20:
        errors.append(f"expected at least 20 components, found {len(components)}")

    refs = set()
    for component in components:
        ref = component.get("bom-ref")
        if not ref or not component.get("name") or not component.get("version"):
            errors.append(f"component missing name/version/bom-ref: {component}")
            continue
        if ref in refs:
            errors.append(f"duplicate bom-ref: {ref}")
        refs.add(ref)
        if not str(component.get("purl", "")).startswith("pkg:nuget/"):
            errors.append(f"invalid purl for {ref}: {component.get('purl')}")

    root = bom.get("metadata", {}).get("component", {}).get("bom-ref")
    refs.add(root)
    for dependency in bom.get("dependencies", []):
        if dependency.get("ref") not in refs:
            errors.append(f"dependency ref not declared: {dependency.get('ref')}")
        for child in dependency.get("dependsOn", []):
            if child not in refs:
                errors.append(f"dependsOn ref not declared: {child}")

    return errors


def check_manifest(repo: Path) -> list[str]:
    props = (repo / "Directory.Packages.props").read_text(encoding="utf-8")
    manifest = (repo / "docs/compliance/DEPENDENCY_MANIFEST.md").read_text(encoding="utf-8")
    declared = re.findall(r'PackageVersion Include="([^"]+)"', props)
    missing = [name for name in declared if f"`{name}`" not in manifest]
    return missing


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT))
    parser.add_argument("--version", default="")
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--check-manifest", action="store_true")
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    version = args.version or read_version(repo)

    if args.check_manifest:
        missing = check_manifest(repo)
        if missing:
            print("Dependency manifest is missing packages: " + ", ".join(missing), file=sys.stderr)
            return 1
        print("Dependency manifest covers every centrally managed package.")

    bom = build_bom(repo, version)
    output = Path(args.output)
    if not output.is_absolute():
        output = repo / output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(bom, ensure_ascii=False, indent=2, sort_keys=False) + "\n", encoding="utf-8")

    print(f"SBOM written to {output}")
    print(f"components={len(bom['components'])} dependencies={len(bom['dependencies'])} serial={bom['serialNumber']}")

    if args.verify:
        errors = verify(bom)
        if errors:
            for error in errors:
                print(f"SBOM verification error: {error}", file=sys.stderr)
            return 1
        print("SBOM verification passed.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
