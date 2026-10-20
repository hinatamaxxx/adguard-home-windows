# AdGuard Home + トレイアプリのアンインストーラー。
# 既定では設定とデータもすべて削除する。確認画面の「設定とデータを残す」にチェックを入れたとき
# （または -KeepSettings）だけ、AdGuard Home の設定・データとトレイのログイン情報などを残す。
# 削除するものの一覧は README を参照。
#   -Quiet        確認画面を出さずに実行（既定どおりすべて削除。-KeepSettings と併用可）
#   -KeepSettings 設定とデータを残す
param([switch]$KeepSettings, [switch]$Quiet)

$ErrorActionPreference = 'Continue'
# 表示の言語（Windows の表示言語が日本語なら日本語、それ以外は英語）
$ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja'
function T([string]$jaText, [string]$enText) { if ($ja) { $jaText } else { $enText } }

# 管理者でなければ、同じ引数で昇格して起動し直す
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    if ($KeepSettings) { $argList += ' -KeepSettings' }
    if ($Quiet) { $argList += ' -Quiet' }
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $argList } catch { }
    exit
}

$dataDir0 = Join-Path $env:ProgramData 'AdGuardHome'
$trayExe = $null
try { $trayExe = Join-Path (([IO.File]::ReadAllText((Join-Path $dataDir0 'settings.json')) | ConvertFrom-Json).TrayDir) 'AdGuardHomeTray.exe' } catch { }
if (-not $trayExe -or -not (Test-Path $trayExe)) { $trayExe = Join-Path $PSScriptRoot 'AdGuardHomeTray.exe' }

if (-not $Quiet -and (Test-Path $trayExe)) {
    # 確認画面はトレイアプリが出す（高 DPI・Windows のライト/ダーク対応）。0=すべて削除 10=残す 1=キャンセル
    $dlgArgs = @('--uninstall-dialog'); if ($KeepSettings) { $dlgArgs += '--keep' }
    $code = (Start-Process $trayExe -ArgumentList $dlgArgs -Wait -PassThru).ExitCode
    if ($code -eq 10) { $KeepSettings = $true } elseif ($code -eq 0) { $KeepSettings = $false } else { exit 1 }
    $Quiet = $true
}

if (-not $Quiet) {
    # トレイアプリがない場合の予備の確認画面
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object Windows.Forms.Form
    $form.Text = 'AdGuard Home のアンインストール'
    $form.Font = [Drawing.SystemFonts]::MessageBoxFont
    $form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'; $form.AutoSize = $true; $form.AutoSizeMode = 'GrowAndShrink'
    $form.Padding = New-Object Windows.Forms.Padding 14
    $panel = New-Object Windows.Forms.FlowLayoutPanel
    $panel.FlowDirection = 'TopDown'; $panel.AutoSize = $true; $panel.WrapContents = $false
    $label = New-Object Windows.Forms.Label
    $label.AutoSize = $true; $label.MaximumSize = New-Object Drawing.Size 440, 0
    $label.Text = "AdGuard Home とトレイアプリをこのPCから削除します。`r`n`r`n" +
                  "既定では、AdGuard Home の設定（フィルター・ログイン情報など）やクエリログ、" +
                  "トレイに保存したログイン情報、管理画面ウィンドウのデータもすべて削除します。"
    $keep = New-Object Windows.Forms.CheckBox
    $keep.AutoSize = $true; $keep.Checked = [bool]$KeepSettings
    $keep.Text = '設定とデータを残す（再インストールしたときに引き継ぐ）'
    $keep.Margin = New-Object Windows.Forms.Padding 0, 12, 0, 12
    $buttons = New-Object Windows.Forms.FlowLayoutPanel
    $buttons.FlowDirection = 'RightToLeft'; $buttons.AutoSize = $true; $buttons.Dock = 'Fill'
    $cancel = New-Object Windows.Forms.Button; $cancel.Text = 'キャンセル'; $cancel.AutoSize = $true; $cancel.DialogResult = 'Cancel'
    $ok = New-Object Windows.Forms.Button; $ok.Text = 'アンインストール'; $ok.AutoSize = $true; $ok.DialogResult = 'OK'
    $buttons.Controls.AddRange(@($cancel, $ok))
    $panel.Controls.AddRange(@($label, $keep, $buttons))
    $form.Controls.Add($panel)
    $form.AcceptButton = $ok; $form.CancelButton = $cancel
    if ($form.ShowDialog() -ne 'OK') { exit 1 }
    $KeepSettings = $keep.Checked
}

$dataDir = Join-Path $env:ProgramData 'AdGuardHome'
$props = $null
try { $props = [IO.File]::ReadAllText((Join-Path $dataDir 'settings.json')) | ConvertFrom-Json } catch { }
$dir = if ($props.InstallDir) { $props.InstallDir } else { Join-Path $env:ProgramFiles 'AdGuardHome' }
$trayDir = if ($props.TrayDir) { $props.TrayDir } else { Join-Path $env:ProgramFiles 'AdGuardHome Tray' }

Get-Process AdGuardHomeTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1   # 管理画面ウィンドウ（WebView2）のプロセスが終わるのを待つ
if (Get-Service AdGuardHome -ErrorAction SilentlyContinue) {
    Push-Location $dir
    & (Join-Path $dir 'AdGuardHome.exe') -s uninstall
    Pop-Location
}
foreach ($t in 'AdGuardHome AutoUpdate', 'AdGuardHome UpdateNow') { Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AdGuard Home.lnk') -Force -ErrorAction SilentlyContinue
# 「インストールされているアプリ」の登録（このアプリが書く唯一のレジストリ）
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdGuardHomeTray' -Recurse -Force -ErrorAction SilentlyContinue
# 自動起動のショートカット。StartupApproved\StartupFolder の値は Windows が管理するので消さない
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'AdGuard Home.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue

$credDir = Join-Path $env:APPDATA 'AdGuardHomeTray'
$webDir = Join-Path $env:LOCALAPPDATA 'AdGuardHomeTray'   # 管理画面ウィンドウ（WebView2）のデータ・ウィンドウ位置
if ($KeepSettings) {
    Remove-Item (Join-Path $dir 'AdGuardHome.exe') -Force -ErrorAction SilentlyContinue
    Write-Host ((T "設定とデータは残しました: " "Settings and data were kept: ") + $dir)
    if (Test-Path $credDir) { Write-Host ((T "トレイのログイン情報は残しました: " "The tray login info was kept: ") + $credDir) }
    if (Test-Path $webDir) { Write-Host ((T "管理画面ウィンドウのデータは残しました: " "The dashboard window data was kept: ") + $webDir) }
} else {
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $credDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $webDir -Recurse -Force -ErrorAction SilentlyContinue
}
# 実行中のスクリプト自身を含むため、トレイフォルダーは最後に削除
Remove-Item $trayDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host (T 'アンインストールが完了しました。' 'Uninstall complete.')
