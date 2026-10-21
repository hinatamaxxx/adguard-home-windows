# 1.8.7 以前の自動更新（manage.ps1）が呼ぶ入口。リリースの ZIP の直下に install.ps1 として置く（リポジトリでは使わない）。
# install.ps1 は scripts に移ったので、同じ引数で scripts\install.ps1 を実行する。
param(
    [string]$AdGuardExe,
    [string]$InstallDir,
    [string]$TrayDir,
    [switch]$SelfUpdate
)
& (Join-Path $PSScriptRoot 'scripts\install.ps1') @PSBoundParameters
exit $LASTEXITCODE
