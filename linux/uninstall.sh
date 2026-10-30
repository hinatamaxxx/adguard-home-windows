#!/usr/bin/env bash
# AdGuard Home のトレイアプリ（Linux 版）をアンインストールする。普段のユーザーで実行する。
# AdGuard Home 本体（/opt/AdGuardHome）は残す。本体も消すときは、公式の手順で:
#   curl -fsSL https://raw.githubusercontent.com/AdguardTeam/AdGuardHome/master/scripts/install.sh | sh -s -- -u
#   --keep-settings  設定とログイン情報を残す
#   --no-system      sudo を使う作業（自動更新のタイマーと polkit の規則の削除）をしない
set -euo pipefail

APP_ID=adguard-home-tray
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
keep=0; system=1
for arg in "$@"; do
  case "$arg" in
    --keep-settings) keep=1 ;;
    --no-system) system=0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

# 動いているトレイを終わらせる
if [ -x "$DATA_HOME/$APP_ID/adguard-home-tray" ]; then
  python3 - "$DATA_HOME/$APP_ID" <<'PY' || true
import os, sys
sys.path.insert(0, sys.argv[1])
from PyQt6.QtNetwork import QLocalSocket
s = QLocalSocket()
s.connectToServer(f"adguard-home-tray-{os.getuid()}")
if s.waitForConnected(500):
    s.write(b"quit\n"); s.waitForBytesWritten(1000)
PY
fi

if [ "$keep" -eq 0 ]; then
  python3 -c "import keyring; keyring.delete_password('AdGuardHomeTray', 'login')" 2>/dev/null || true
  rm -rf "${CONFIG_HOME:?}/$APP_ID" "${XDG_STATE_HOME:-$HOME/.local/state}/$APP_ID" "${XDG_CACHE_HOME:-$HOME/.cache}/$APP_ID"
fi
rm -f "$CONFIG_HOME/autostart/$APP_ID.desktop" "$DATA_HOME/applications/$APP_ID.desktop" \
      "$DATA_HOME/icons/hicolor/scalable/apps/$APP_ID.svg" "$HOME/.local/bin/$APP_ID"
rm -rf "${DATA_HOME:?}/$APP_ID"

if [ "$system" -eq 1 ] && { [ -e /etc/systemd/system/adguard-home-update.timer ] || [ -e /etc/polkit-1/rules.d/50-adguard-home-tray.rules ]; }; then
  sudo systemctl disable --now adguard-home-update.timer 2>/dev/null || true
  sudo rm -f /etc/systemd/system/adguard-home-update.service /etc/systemd/system/adguard-home-update.timer \
             /etc/polkit-1/rules.d/50-adguard-home-tray.rules
  sudo systemctl daemon-reload
fi
echo "アンインストールしました（AdGuard Home 本体は残しています）。"
