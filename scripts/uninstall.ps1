# AdGuard Home + トレイアプリのアンインストーラー。
# 既定では設定とデータもすべて削除する。確認画面の「設定とデータを残す」にチェックを入れたとき
# （または -KeepSettings）だけ、AdGuard Home の設定・データとトレイのログイン情報などを残す。
# 削除するものの一覧は README を参照。
#   -Quiet        確認画面を出さずに実行（既定どおりすべて削除。-KeepSettings と併用可）
#   -KeepSettings 設定とデータを残す
param([switch]$KeepSettings, [switch]$Quiet)

$ErrorActionPreference = 'Continue'
# 表示の言語（Windows の表示言語が日本語なら日本語、それ以外は英語）
# Windows の表示言語（PowerShell 自体の設定に左右されないよう、OS に直接たずねる）
try { $ja = (((Add-Type -MemberDefinition '[DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();' -Name UiLanguage -Namespace AdGuardHomeSetup -PassThru)::GetUserDefaultUILanguage()) -band 0x3FF) -eq 0x11 } catch { $ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja' }
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

# 進み具合。画面（トレイアプリ）があれば progress.log に書き、画面が読んで表示する
$uiDir = $null; $uiProc = $null; $progressFile = $null
function Step([string]$jaText, [string]$enText) {
    $line = T $jaText $enText
    Write-Host $line
    if ($progressFile) { try { [IO.File]::AppendAllText($progressFile, $line + "`r`n", (New-Object Text.UTF8Encoding $false)) } catch { } }
}

if (-not $Quiet -and (Test-Path $trayExe)) {
    # 画面（確認 → 進み具合 → 完了）はトレイアプリが出す（高 DPI・Windows のライト/ダーク対応）。
    # トレイのフォルダーは途中で消すので、exe を一時フォルダーにコピーしてから起動する
    $uiDir = Join-Path $env:TEMP ('adguard-home-windows-uninstall-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $uiDir | Out-Null
    $uiExe = Join-Path $uiDir 'AdGuardHomeTray.exe'
    Copy-Item $trayExe $uiExe -Force
    $uiArgs = @('--uninstall-ui', "`"$uiDir`""); if ($KeepSettings) { $uiArgs += '--keep' }
    $uiProc = Start-Process $uiExe -ArgumentList $uiArgs -PassThru
    # 選んだ結果（choice.txt: all / keep / cancel）を待つ
    $choiceFile = Join-Path $uiDir 'choice.txt'
    $choice = $null
    while (-not $choice) {
        if (Test-Path $choiceFile) { $choice = ([IO.File]::ReadAllText($choiceFile)).Trim() }
        elseif ($uiProc.HasExited) { $choice = 'cancel' }
        else { Start-Sleep -Milliseconds 200 }
    }
    if ($choice -eq 'cancel') {
        $uiProc.WaitForExit(5000) | Out-Null
        Remove-Item $uiDir -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
    $KeepSettings = ($choice -eq 'keep')
    $progressFile = Join-Path $uiDir 'progress.log'
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

try {
Step 'トレイアプリを終了しています' 'Closing the tray app'
# 画面（一時フォルダーにコピーした同じ exe）は終了しない
Get-Process AdGuardHomeTray -ErrorAction SilentlyContinue | Where-Object { -not $uiProc -or $_.Id -ne $uiProc.Id } | Stop-Process -Force
Start-Sleep -Seconds 1   # 管理画面ウィンドウ（WebView2）のプロセスが終わるのを待つ
Step 'AdGuard Home のサービスを停止・削除しています' 'Stopping and removing the AdGuard Home service'
if (Get-Service AdGuardHome -ErrorAction SilentlyContinue) {
    Push-Location $dir
    & (Join-Path $dir 'AdGuardHome.exe') -s uninstall 2>&1 | Out-Null
    Pop-Location
}
Step '自動更新のタスクを削除しています' 'Removing the automatic update tasks'
foreach ($t in 'AdGuardHome AutoUpdate', 'AdGuardHome UpdateNow') { Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue }
Step 'スタートメニュー・「インストールされているアプリ」・自動起動の登録を削除しています' 'Removing the Start menu, Installed apps, and startup entries'
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AdGuard Home.lnk') -Force -ErrorAction SilentlyContinue
# 「インストールされているアプリ」の登録（このアプリが書く唯一のレジストリ）
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdGuardHomeTray' -Recurse -Force -ErrorAction SilentlyContinue
# 自動起動のショートカット。StartupApproved\StartupFolder の値は Windows が管理するので消さない
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'AdGuard Home.lnk') -Force -ErrorAction SilentlyContinue

$credDir = Join-Path $env:APPDATA 'AdGuardHomeTray'
$webDir = Join-Path $env:LOCALAPPDATA 'AdGuardHomeTray'   # 管理画面ウィンドウ（WebView2）のデータ・ウィンドウ位置
if ($KeepSettings) {
    Step 'プログラムを削除しています（設定とデータは残します）' 'Removing the program files (keeping settings and data)'
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir 'AdGuardHome.exe') -Force -ErrorAction SilentlyContinue
    Write-Host ((T "設定とデータは残しました: " "Settings and data were kept: ") + $dir)
    if (Test-Path $credDir) { Write-Host ((T "トレイのログイン情報は残しました: " "The tray login info was kept: ") + $credDir) }
    if (Test-Path $webDir) { Write-Host ((T "管理画面ウィンドウのデータは残しました: " "The dashboard window data was kept: ") + $webDir) }
} else {
    Step 'プログラムと、設定・データを削除しています' 'Removing the program files, settings, and data'
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $credDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $webDir -Recurse -Force -ErrorAction SilentlyContinue
}
# 実行中のスクリプト自身を含むため、トレイフォルダーは最後に削除
Remove-Item $trayDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host (T 'アンインストールが完了しました。' 'Uninstall complete.')
if ($progressFile) { [IO.File]::AppendAllText($progressFile, "#done`r`n") }
} catch {
    Write-Host $_
    if ($progressFile) { [IO.File]::AppendAllText($progressFile, "#error " + ($_.Exception.Message -replace '\s+', ' ') + "`r`n", (New-Object Text.UTF8Encoding $false)) }
}
# 画面が閉じられたら、一時フォルダーを片付ける
if ($uiProc) {
    $uiProc.WaitForExit() | Out-Null
    Start-Sleep -Milliseconds 300
    Remove-Item $uiDir -Recurse -Force -ErrorAction SilentlyContinue
}
