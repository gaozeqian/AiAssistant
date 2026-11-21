#!/usr/bin/env bash
# Creates a self-signed code-signing identity in a temporary keychain for local/macOS-runner builds.
# Usage: create-self-signed-identity.sh <output-cert.pem> [identity-name] [keychain-path] [password]
set -euo pipefail

OUTPUT_CERT="${1:?output cert path required}"
IDENTITY_NAME="${2:-AiAssistant Self-Signed}"
KEYCHAIN="${3:-$HOME/Library/Keychains/aiassistant-sign.keychain-db}"
PASSWORD="${4:-aiassistant-sign}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "error: this helper requires macOS (security/codesign)" >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -days 365 \
  -subj "/CN=$IDENTITY_NAME" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" >/dev/null 2>&1

openssl pkcs12 -export -out "$WORK/identity.p12" \
  -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -passout "pass:$PASSWORD" >/dev/null 2>&1

security create-keychain -p "$PASSWORD" "$KEYCHAIN" 2>/dev/null || true
security unlock-keychain -p "$PASSWORD" "$KEYCHAIN"
security import "$WORK/identity.p12" -k "$KEYCHAIN" -P "$PASSWORD" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple: -s -k "$PASSWORD" "$KEYCHAIN" >/dev/null

# Self-signed identities are usable by codesign even before explicit trust; users must trust the
# exported certificate before Gatekeeper will accept the app.
security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"') >/dev/null

cp "$WORK/cert.pem" "$OUTPUT_CERT"
security find-identity -v -p codesigning "$KEYCHAIN" || true
echo "identity=$IDENTITY_NAME"
echo "cert=$OUTPUT_CERT"
