@echo off
chcp 65001 >nul
rem AdGuard Home Tray のセットアップ画面を開く。初回は先にトレイアプリをビルドする。
cd /d "%~dp0"
if not exist "build\AdGuardHomeTray.exe" (
  echo トレイアプリをビルドしています...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 (
    echo ビルドに失敗しました。
    pause
    exit /b 1
  )
)
start "" "%~dp0build\AdGuardHomeTray.exe" --setup
