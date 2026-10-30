#!/usr/bin/env bash
# AdGuard Home のトレイアプリ（Linux 版）をインストールする。管理者ではなく、普段のユーザーで実行する。
#   ~/.local/share/adguard-home-tray/      アプリ本体
#   ~/.local/bin/adguard-home-tray         起動コマンド
#   ~/.local/share/applications/           アプリケーションメニューの「AdGuard Home」
#   ~/.config/autostart/                   ログイン時の自動起動
# sudo を使うもの（--update のときは行わない）:
#   必要なパッケージ（apt: python3-pyqt6 ほか）、AdGuard Home の自動更新のタイマー（/etc/systemd/system）、
#   「今すぐ確認して更新」を確認なしで行うための polkit の規則（/etc/polkit-1/rules.d）
#
# 使い方: bash install.sh [--update] [--no-system] [--no-autostart] [--no-start] [--with-adguard-home]
#   --update             このアプリだけを入れ替える（自己更新で使う。sudo を使わない）
#   --no-system          sudo を使う作業（パッケージ・タイマー・polkit）をしない
#   --no-autostart       ログイン時の自動起動を設定しない
#   --no-start           インストール後にトレイを起動しない
#   --with-adguard-home  AdGuard Home が入っていなければ、公式の install.sh で /opt/AdGuardHome に入れる
set -euo pipefail

APP_ID=adguard-home-tray
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
DEST="$DATA_HOME/$APP_ID"
BIN="$HOME/.local/bin"

update=0; system=1; autostart=1; start=1; with_agh=0
for arg in "$@"; do
  case "$arg" in
    --update) update=1; system=0; autostart=0; start=0 ;;
    --no-system) system=0 ;;
    --no-autostart) autostart=0 ;;
    --no-start) start=0 ;;
    --with-adguard-home) with_agh=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [ "$(id -u)" -eq 0 ]; then
  echo "普段のユーザーで実行してください（sudo を付けない）。必要なところだけ sudo を使います。" >&2
  exit 1
fi

say() { printf '\033[1m==>\033[0m %s\n' "$*"; }
# /opt/AdGuardHome は root だけが読める（0700）ので、systemd の登録で確かめる
agh_installed() { [ "$(systemctl show -p LoadState --value AdGuardHome.service 2>/dev/null)" = loaded ]; }

# 1) 必要なパッケージ
if [ "$system" -eq 1 ]; then
  if ! python3 -c 'import PyQt6.QtWidgets' 2>/dev/null || ! python3 -c 'import PyQt6.QtWebEngineWidgets' 2>/dev/null \
     || ! python3 -c 'import keyring' 2>/dev/null; then
    if command -v apt-get >/dev/null; then
      say "必要なパッケージを入れます（python3-pyqt6, python3-pyqt6.qtwebengine, python3-keyring）"
      sudo apt-get install -y python3-pyqt6 python3-pyqt6.qtwebengine python3-keyring
    else
      echo "PyQt6（と Qt WebEngine、keyring）を入れてから、もう一度実行してください。" >&2
      exit 1
    fi
  fi
fi
if ! python3 -c 'import PyQt6.QtWidgets' 2>/dev/null; then
  echo "PyQt6 が見つかりません（Ubuntu / Kubuntu: sudo apt install python3-pyqt6）。" >&2
  exit 1
fi

# 2) AdGuard Home 本体（頼まれたときだけ。公式の install.sh で /opt/AdGuardHome に入れ、systemd に登録する）
if [ "$with_agh" -eq 1 ] && ! agh_installed; then
  say "AdGuard Home を公式の install.sh で入れます"
  curl -fsSL https://raw.githubusercontent.com/AdguardTeam/AdGuardHome/master/scripts/install.sh | sh -s -- -v
fi

# 3) アプリ本体（入れ替えは新しいフォルダーを用意してから差し替える）
say "アプリを $DEST に入れます"
mkdir -p "$DATA_HOME" "$BIN"
rm -rf "$DEST.new"
mkdir -p "$DEST.new"
cp -r "$SRC/adguard-home-tray" "$SRC/adguard_home_tray" "$SRC/scripts" "$SRC/systemd" "$SRC/icons" \
      "$SRC/install.sh" "$SRC/uninstall.sh" "$DEST.new/"
find "$DEST.new" -name '__pycache__' -prune -exec rm -rf {} +
chmod +x "$DEST.new/adguard-home-tray" "$DEST.new"/scripts/*.py "$DEST.new/install.sh" "$DEST.new/uninstall.sh"
rm -rf "$DEST.old"
[ -d "$DEST" ] && mv "$DEST" "$DEST.old"
mv "$DEST.new" "$DEST"
rm -rf "$DEST.old"
ln -sfn "$DEST/adguard-home-tray" "$BIN/$APP_ID"

# 4) アイコンとアプリケーションメニュー
mkdir -p "$DATA_HOME/icons/hicolor/scalable/apps" "$DATA_HOME/applications"
cp "$DEST/icons/$APP_ID.svg" "$DATA_HOME/icons/hicolor/scalable/apps/$APP_ID.svg"
python3 - "$DEST" <<'PY'
import sys
sys.path.insert(0, sys.argv[1])
from adguard_home_tray import system
import os
path = os.path.join(os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share"), "applications", "adguard-home-tray.desktop")
with open(path, "w", encoding="utf-8") as f:
    f.write(system.desktop_entry(False))
if os.path.exists(system.AUTOSTART_FILE):   # 既存の自動起動は、新しい起動コマンドに書き直す（オフのままなら残す）
    if system.autostart_enabled():
        system.set_autostart(True)
PY
command -v update-desktop-database >/dev/null && update-desktop-database -q "$DATA_HOME/applications" || true
command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 >/dev/null 2>&1 || true

# 5) ログイン時の自動起動
if [ "$autostart" -eq 1 ]; then
  python3 -c "import sys; sys.path.insert(0, '$DEST'); from adguard_home_tray import system; system.set_autostart(True)"
fi

# 6) AdGuard Home の自動更新（root のタイマー）と、「今すぐ確認して更新」用の polkit の規則
if [ "$system" -eq 1 ]; then
  say "AdGuard Home の自動更新（毎日）を設定します"
  sudo install -m 0644 "$DEST/systemd/adguard-home-update.service" "$DEST/systemd/adguard-home-update.timer" /etc/systemd/system/
  sudo install -d -m 0755 /etc/polkit-1/rules.d
  sudo install -m 0644 "$DEST/systemd/50-adguard-home-tray.rules" /etc/polkit-1/rules.d/
  sudo systemctl daemon-reload
  sudo systemctl enable --now adguard-home-update.timer
fi

case ":$PATH:" in *":$BIN:"*) ;; *) echo "メモ: $BIN が PATH にありません（次のログインから入ります）。";; esac

# 7) 起動（動いていれば新しい版に入れ替える）
if [ "$start" -eq 1 ] && { [ -n "${WAYLAND_DISPLAY:-}" ] || [ -n "${DISPLAY:-}" ]; }; then
  nohup "$BIN/$APP_ID" --replace >/dev/null 2>&1 &
fi

say "完了しました。システムトレイの AdGuard Home のアイコンから操作できます。"
if [ "$update" -eq 0 ] && ! agh_installed; then
  echo "AdGuard Home 本体はまだ入っていません。入れるには: bash install.sh --with-adguard-home"
fi
