#!/usr/bin/env python3
"""Generate an offline CycloneDX 1.6 SBOM for AiAssistant.

Reads the NuGet restore graph from the unified artifacts layout
(``artifacts/obj/<Project>/project.assets.json``) so the generator works in
air-gapped and low-privilege environments. Also records the Swift macOS helper
and the bundled local model assets (ML-BOM) as first-class components.

Usage:
    python3 build/sbom/generate-sbom.py                       # artifacts/dist/<version>/AiAssistant-<version>-sbom.cdx.json
    python3 build/sbom/generate-sbom.py --verify              # + structural/license checks
    python3 build/sbom/generate-sbom.py --check-manifest      # + CPM manifest coverage
    python3 build/sbom/generate-sbom.py --dry-run             # print summary, write nothing
    python3 build/sbom/generate-sbom.py --help

Exit codes: 0 success / 1 verification failure / 2 usage error.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

NAMESPACE = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
ALLOWED_LICENSES = frozenset({"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "MS-PL"})

# Some packages declare license by file/URL instead of an SPDX expression.
# Each override must be justified here and re-reviewed when the package version changes.
LICENSE_OVERRIDES: dict[str, tuple[str, str]] = {
    "avalonia.angle.windows.natives": ("BSD-3-Clause", "ANGLE LICENSE file, 3-clause redistribution terms"),
    "htmlagilitypack": ("MIT", "nuspec licenseUrl = github.com/zzzprojects/html-agility-pack/LICENSE (MIT License)"),
    "microsoft.ml.onnxruntime.managed": ("MIT", "LICENSE.txt starts with 'MIT License'"),
    "microsoft.testing.extensions.codecoverage": ("MIT", "Microsoft .NET Library terms (MIT-equivalent)"),
    "mono.cecil": ("MIT", "nuspec licenseUrl = opensource.org/licenses/mit-license.php"),
    "netarchtest.rules": ("MIT", "dependency-manifest.md records MIT; nuspec has no expression"),
    "whisper.net": ("MIT", "LICENSE starts with 'MIT License'"),
}

# Bundled models are declared in docs/release/compliance.md (single source of truth).
# The last field is the path inside artifacts/cache/models/bundle used for hashing.
MODEL_ASSETS: tuple[tuple[str, str, str, str, str], ...] = (
    ("sherpa-kws-wenetspeech-3.3M", "2024-01-01", "Apache-2.0", "Sherpa ONNX KWS wake-word model", "sherpa/kws/encoder.onnx"),
    ("sherpa-paraformer-zh-small-int8", "2024-03-09", "Apache-2.0", "Sherpa ONNX Paraformer ASR model", "sherpa/model.int8.onnx"),
    ("sherpa-melo-tts-zh-en", "2024", "MIT", "MeloTTS Chinese/English VITS model", "sherpa/tts/model.onnx"),
    ("silero-vad-v5", "5", "MIT", "Silero voice activity detection model", "sherpa/silero_vad.onnx"),
    ("rapidocr-pp-ocrv4-det", "3.10.0", "Apache-2.0", "PP-OCRv4 text detection model (ONNX conversion by RapidAI)", "rapidocr/ch_PP-OCRv4_det_infer.onnx"),
    ("rapidocr-pp-ocrv4-rec", "3.10.0", "Apache-2.0", "PP-OCRv4 Chinese recognition model and ppocr_keys_v1.txt dictionary (ONNX conversion by RapidAI)", "rapidocr/ch_PP-OCRv4_rec_infer.onnx"),
)


def find_repo_root(explicit: str | None) -> Path:
    """Resolve repository root via git, with a file-relative fallback for archives."""
    if explicit:
        return Path(explicit).resolve()
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            check=True,
        )
        return Path(result.stdout.strip()).resolve()
    except (OSError, subprocess.CalledProcessError):
        return Path(__file__).resolve().parents[2]


def artifacts_root(repo: Path) -> Path:
    override = os.environ.get("AIASSISTANT_ARTIFACTS_ROOT")
    return (repo / override).resolve() if override else repo / "artifacts"


def read_version(repo: Path) -> str:
    props = (repo / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props)
    return match.group(1).strip() if match else "0.0.0"


def parse_license(nuspec: Path, package_name: str) -> str | None:
    """Return an SPDX license expression from a nuspec, with reviewed overrides."""
    key = package_name.lower()
    if key in LICENSE_OVERRIDES:
        return LICENSE_OVERRIDES[key][0]
    if not nuspec.exists():
        return None
    text = nuspec.read_text(encoding="utf-8-sig", errors="replace")
    match = re.search(r"<license\b[^>]*>(.*?)</license>", text, re.S)
    if match:
        value = match.group(1).strip()
        if value in ALLOWED_LICENSES or " OR " in value or " AND " in value:
            return value
        return None
    match = re.search(r"<licenseUrl>(.*?)</licenseUrl>", text, re.S)
    if match:
        url = match.group(1).strip().lower()
        if "mit-license" in url:
            return "MIT"
        if "apache.org/licenses/license-2.0" in url:
            return "Apache-2.0"
    return None


def nuget_root() -> Path:
    return Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))


def collect_assets(repo: Path, art_root: Path):
    """Read only the unified artifacts/obj restore graph."""
    components: dict[tuple[str, str], str | None] = {}
    dependencies: dict[str, set[str]] = {}
    direct: set[str] = set()
    assets_files = sorted(art_root.glob("obj/**/project.assets.json"))
    if not assets_files:
        raise SystemExit(
            f"No project.assets.json found under {art_root / 'obj'}. "
            "Run `dotnet restore AiAssistant.sln` first."
        )

    for assets_path in assets_files:
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


def file_digest(path: Path) -> str | None:
    if not path.is_file():
        return None
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_bom(repo: Path, art_root: Path, version: str):
    components, dependencies, direct = collect_assets(repo, art_root)
    if not components:
        raise SystemExit("No NuGet components found. Run `dotnet restore AiAssistant.sln` first.")

    cache = nuget_root()
    refs: dict[tuple[str, str], str] = {}
    for name, component_version in components:
        refs[(name, component_version)] = f"pkg:nuget/{name}@{component_version}"

    by_name: dict[str, list[str]] = {}
    for (name, _), ref in refs.items():
        by_name.setdefault(name, []).append(ref)

    component_entries = []
    missing_licenses: list[str] = []
    for name, component_version in sorted(components):
        ref = refs[(name, component_version)]
        nuspec = cache / name.lower() / component_version / f"{name.lower()}.nuspec"
        license_id = parse_license(nuspec, name)
        if license_id is None:
            missing_licenses.append(f"{name}@{component_version}")
        entry = {
            "type": "library",
            "bom-ref": ref,
            "name": name,
            "version": component_version,
            "purl": ref,
            "scope": "required",
        }
        if license_id:
            entry["licenses"] = [{"expression": license_id}]
        digest = components[(name, component_version)]
        if digest:
            try:
                entry["hashes"] = [{"alg": "SHA-512", "content": base64.b64decode(digest).hex()}]
            except (ValueError, TypeError):
                pass
        component_entries.append(entry)

    # Swift macOS helper: first-class native component with a content hash.
    helper_dir = repo / "native" / "AiAssistant.MacHelper"
    helper_hash = file_digest(helper_dir / "Package.swift")
    helper_ref = f"pkg:generic/aiassistant/AiAssistant.MacHelper@{version}"
    helper_entry = {
        "type": "application",
        "bom-ref": helper_ref,
        "name": "AiAssistant.MacHelper",
        "version": version,
        "scope": "required",
        "purl": helper_ref,
        "licenses": [{"expression": "MIT"}],
        "properties": [{"name": "aiassistant:language", "value": "Swift 6"}],
    }
    if helper_hash:
        helper_entry["hashes"] = [{"alg": "SHA-256", "content": helper_hash}]
    component_entries.append(helper_entry)

    # ML-BOM: bundled local models. Hashes are filled in when the staged bundle exists.
    bundle_root = art_root / "cache" / "models" / "bundle"
    for model_name, model_version, license_id, description, relative_path in MODEL_ASSETS:
        model_ref = f"pkg:generic/aiassistant/{model_name}@{model_version}"
        entry = {
            "type": "machine-learning-model",
            "bom-ref": model_ref,
            "name": model_name,
            "version": model_version,
            "purl": model_ref,
            "scope": "optional",
            "description": description,
            "licenses": [{"expression": license_id}],
            "properties": [{"name": "aiassistant:distribution", "value": "bundled"}],
        }
        staged = file_digest(bundle_root / relative_path)
        if staged:
            entry["hashes"] = [{"alg": "SHA-256", "content": staged}]
        component_entries.append(entry)

    dependency_entries = []
    for (name, component_version) in sorted(components):
        ref = refs[(name, component_version)]
        depends_on = sorted(
            {candidate for dep in dependencies.get(name, set()) for candidate in by_name.get(dep, [])}
        )
        dependency_entries.append({"ref": ref, "dependsOn": depends_on})
    root_ref = f"pkg:generic/AiAssistant@{version}"
    root_depends = sorted(
        {candidate for name in direct for candidate in by_name.get(name, [])}
    ) + [helper_ref] + [f"pkg:generic/aiassistant/{m[0]}@{m[1]}" for m in MODEL_ASSETS]
    dependency_entries.insert(0, {"ref": root_ref, "dependsOn": sorted(set(root_depends))})

    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    timestamp = (
        datetime.fromtimestamp(int(epoch), tz=timezone.utc)
        if epoch and epoch.isdigit()
        else datetime.now(tz=timezone.utc)
    ).strftime("%Y-%m-%dT%H:%M:%SZ")
    serial_seed = "|".join(sorted(refs.values())) + f"|{version}|madr-4.0"
    serial = f"urn:uuid:{uuid.uuid5(NAMESPACE, serial_seed)}"

    bom = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "serialNumber": serial,
        "version": 1,
        "metadata": {
            "timestamp": timestamp,
            "tools": [{"vendor": "AiAssistant", "name": "offline-sbom", "version": "2.0.0"}],
            "component": {
                "type": "application",
                "bom-ref": root_ref,
                "name": "AiAssistant",
                "version": version,
            },
            "properties": [
                {"name": "aiassistant:targetFramework", "value": "net10.0"},
                {"name": "aiassistant:packageCount", "value": str(len(components))},
                {"name": "aiassistant:modelCount", "value": str(len(MODEL_ASSETS))},
            ],
        },
        "components": component_entries,
        "dependencies": dependency_entries,
    }
    return bom, missing_licenses


def verify(bom: dict, missing_licenses: list[str]) -> list[str]:
    errors: list[str] = []
    components = bom.get("components", [])
    if len(components) < 20:
        errors.append(f"expected at least 20 components, found {len(components)}")
    if bom.get("specVersion") != "1.6":
        errors.append(f"expected CycloneDX 1.6, found {bom.get('specVersion')}")
    if missing_licenses:
        errors.append("license whitelist violation: " + ", ".join(sorted(missing_licenses)))

    refs = set()
    for component in components:
        ref = component.get("bom-ref")
        if not ref or not component.get("name") or not component.get("version"):
            errors.append(f"component missing name/version/bom-ref: {component}")
            continue
        if ref in refs:
            errors.append(f"duplicate bom-ref: {ref}")
        refs.add(ref)
        if not str(component.get("purl", "")).startswith(("pkg:nuget/", "pkg:generic/")):
            errors.append(f"invalid purl for {ref}: {component.get('purl')}")
        for license_entry in component.get("licenses", []):
            expression = license_entry.get("expression", "")
            if expression not in ALLOWED_LICENSES:
                errors.append(f"license not whitelisted for {ref}: {expression}")

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
    manifest = (repo / "docs/release/compliance.md").read_text(encoding="utf-8")
    declared = re.findall(r'PackageVersion Include="([^"]+)"', props)
    return [name for name in declared if f"`{name}`" not in manifest]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", default=None, help="repository root (default: git rev-parse --show-toplevel)")
    parser.add_argument("--output", default=None, help="output path (default: artifacts/dist/<version>/AiAssistant-<version>-sbom.cdx.json)")
    parser.add_argument("--version", default="", help="product version (default: VersionPrefix)")
    parser.add_argument("--verify", action="store_true", help="verify structure and license whitelist")
    parser.add_argument("--check-manifest", action="store_true", help="verify CPM packages are documented")
    parser.add_argument("--dry-run", action="store_true", help="do not write the output file")
    args = parser.parse_args()

    repo = find_repo_root(args.repo)
    art_root = artifacts_root(repo)
    version = args.version or read_version(repo)
    output = (
        Path(args.output)
        if args.output
        else art_root / "dist" / version / f"AiAssistant-{version}-sbom.cdx.json"
    )
    if not output.is_absolute():
        output = repo / output

    if args.check_manifest:
        missing = check_manifest(repo)
        if missing:
            print("Dependency manifest is missing packages: " + ", ".join(missing), file=sys.stderr)
            return 1
        print("Dependency manifest covers every centrally managed package.")

    bom, missing_licenses = build_bom(repo, art_root, version)
    if not args.dry_run:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(bom, ensure_ascii=False, indent=2, sort_keys=False) + "\n", encoding="utf-8"
        )
        print(f"SBOM written to {output}")
    else:
        print("[dry-run] output not written")

    kinds: dict[str, int] = {}
    for component in bom["components"]:
        kinds[component["type"]] = kinds.get(component["type"], 0) + 1
    print(
        "components="
        + str(len(bom["components"]))
        + " ("
        + ", ".join(f"{k}={v}" for k, v in sorted(kinds.items()))
        + ") dependencies="
        + str(len(bom["dependencies"]))
        + " serial="
        + bom["serialNumber"]
    )

    if args.verify:
        errors = verify(bom, missing_licenses)
        if errors:
            for error in errors:
                print(f"SBOM verification error: {error}", file=sys.stderr)
            return 1
        print("SBOM verification passed (CycloneDX 1.6, license whitelist 100%).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
