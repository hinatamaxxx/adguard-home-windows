#!/usr/bin/env bash
# コマンド 1 行での導入用（Linux）:
#   curl -fsSL https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/linux/get.sh | bash
# 最新リリースの adguard-home-linux.tar.gz を SHA-256 で照合し、install.sh を実行する。
# 引数は install.sh に渡す（例: ... | bash -s -- --with-adguard-home）。
set -euo pipefail
REPO=hinatamaxxx/adguard-home-windows
PKG=adguard-home-linux.tar.gz
BASE="https://github.com/$REPO/releases/latest/download"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
echo "==> $PKG をダウンロードしています"
curl -fsSL -o "$tmp/$PKG" "$BASE/$PKG"
curl -fsSL -o "$tmp/$PKG.sha256" "$BASE/$PKG.sha256"
expected="$(cut -d' ' -f1 "$tmp/$PKG.sha256")"
actual="$(sha256sum "$tmp/$PKG" | cut -d' ' -f1)"
if [ "$expected" != "$actual" ]; then
  echo "SHA-256 が一致しません（$actual）。中止しました。" >&2
  exit 1
fi
tar -xzf "$tmp/$PKG" -C "$tmp"
bash "$tmp/adguard-home-linux/install.sh" "$@"
