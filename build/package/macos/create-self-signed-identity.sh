#!/usr/bin/env bash
# 在临时钥匙串里创建自签名代码签名身份，供本地或 macOS runner 演练签名与公证。
set -euo pipefail

usage() {
  cat <<'EOF'
用法: bash build/package/macos/create-self-signed-identity.sh <输出证书路径> [标识名] [钥匙串路径] [密码]

  <输出证书路径>   必填。导出的公钥证书（.cer/.pem），随安装包分发。
  [标识名]         默认 "AiAssistant Self-Signed"。
  [钥匙串路径]     默认 ~/Library/Keychains/aiassistant-sign.keychain-db。
  [密码]           默认 "aiassistant-sign"。

选项:
  -h, --help       显示本帮助

说明: 自签名身份可被 codesign 直接使用，但用户需先信任导出的证书，Gatekeeper 才会放行。
      仅用于演练；正式发布使用真实 Developer ID。本脚本只能在 macOS 上运行。

退出码: 0 成功 / 1 环境不满足 / 2 用法错误
EOF
}

for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
  esac
done

if [[ $# -lt 1 || -z "${1:-}" ]]; then
  usage >&2
  exit 2
fi

OUTPUT_CERT="$1"
IDENTITY_NAME="${2:-AiAssistant Self-Signed}"
KEYCHAIN="${3:-$HOME/Library/Keychains/aiassistant-sign.keychain-db}"
PASSWORD="${4:-aiassistant-sign}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "error: this helper requires macOS (security/codesign)" >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# macOS security import 只接受传统 PKCS#12（SHA-1 MAC + 3DES/RC2 PBE）；
# OpenSSL 3 默认的 AES/PBKDF2/SHA-256 会报 "MAC verification failed"。
# 按能力探测追加兼容参数，兼容 OpenSSL 3 与 LibreSSL/macOS 自带 openssl。
PKCS12_HELP="$(openssl pkcs12 -help 2>&1 || true)"
PKCS12_OPTS=()
if printf '%s\n' "$PKCS12_HELP" | grep -q -- '-legacy'; then
  PKCS12_OPTS+=(-legacy)
fi
if printf '%s\n' "$PKCS12_HELP" | grep -q -- '-certpbe'; then
  PKCS12_OPTS+=(-certpbe PBE-SHA1-3DES)
fi
if printf '%s\n' "$PKCS12_HELP" | grep -q -- '-keypbe'; then
  PKCS12_OPTS+=(-keypbe PBE-SHA1-3DES)
fi
if printf '%s\n' "$PKCS12_HELP" | grep -q -- '-macalg'; then
  PKCS12_OPTS+=(-macalg SHA1)
fi
echo "openssl: $(openssl version 2>&1)"

openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -days 365 \
  -subj "/CN=$IDENTITY_NAME" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" >/dev/null 2>&1

openssl pkcs12 -export -out "$WORK/identity.p12" \
  ${PKCS12_OPTS[@]+"${PKCS12_OPTS[@]}"} \
  -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -passout "pass:$PASSWORD"

security create-keychain -p "$PASSWORD" "$KEYCHAIN" 2>/dev/null || true
security unlock-keychain -p "$PASSWORD" "$KEYCHAIN"
security import "$WORK/identity.p12" -k "$KEYCHAIN" -P "$PASSWORD" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple: -s -k "$PASSWORD" "$KEYCHAIN" >/dev/null

# 自签名身份无需显式信任即可被 codesign 使用；用户需先信任导出的证书，Gatekeeper 才会放行应用。
# 逐个收集现有用户钥匙串：路径可能含空格，用数组保留参数边界，不依赖 shell 分词。
existing_keychains=()
while IFS= read -r kc; do
  kc="${kc#"${kc%%[![:space:]]*}"}"
  kc="${kc%"${kc##*[![:space:]]}"}"
  if [[ -n "$kc" ]]; then
    existing_keychains+=("$kc")
  fi
done <<< "$(security list-keychains -d user | tr -d '"')"
security list-keychains -d user -s "$KEYCHAIN" ${existing_keychains[@]+"${existing_keychains[@]}"} >/dev/null

mkdir -p "$(dirname "$OUTPUT_CERT")"
cp "$WORK/cert.pem" "$OUTPUT_CERT"
security find-identity -v -p codesigning "$KEYCHAIN" || true
echo "identity=$IDENTITY_NAME"
echo "cert=$OUTPUT_CERT"
