#!/usr/bin/env bash
# Copies the product legal documents and third-party notices into a package directory.
# Fails when any required file is missing so a release can never ship without them.
#
# Usage: bash build/package/legal-files.sh <destination-directory>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DEST="${1:?usage: legal-files.sh <destination-directory>}"

mkdir -p "$DEST"

for name in LICENSE EULA.md THIRD-PARTY-NOTICES.md; do
  if [[ ! -f "$REPO_ROOT/$name" ]]; then
    echo "missing legal file: $REPO_ROOT/$name" >&2
    exit 1
  fi
  cp "$REPO_ROOT/$name" "$DEST/$name"
done

NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
ORT_VERSION="$(sed -n 's:.*Include="Microsoft.ML.OnnxRuntime.Managed" Version="\([^"]*\)".*:\1:p' "$REPO_ROOT/Directory.Packages.props" | head -1)"
if [[ -z "$ORT_VERSION" ]]; then
  echo "unable to read Microsoft.ML.OnnxRuntime.Managed version from Directory.Packages.props" >&2
  exit 1
fi

ORT_NOTICES="$NUGET_ROOT/microsoft.ml.onnxruntime.managed/$ORT_VERSION/ThirdPartyNotices.txt"
if [[ ! -f "$ORT_NOTICES" ]]; then
  echo "missing ONNX Runtime notices: $ORT_NOTICES (run 'dotnet restore' first)" >&2
  exit 1
fi
cp "$ORT_NOTICES" "$DEST/THIRD-PARTY-NOTICES-ONNXRUNTIME.txt"

echo "legal files -> $DEST (LICENSE, EULA.md, THIRD-PARTY-NOTICES.md, THIRD-PARTY-NOTICES-ONNXRUNTIME.txt)"
