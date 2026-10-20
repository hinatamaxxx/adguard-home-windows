# AdGuard Home + トレイアプリのインストーラー（管理者権限で実行）。
#   - AdGuard Home を Windows サービスとして登録（コンソール画面なし・PC 起動時に自動開始）
#   - トレイアプリと管理スクリプトを別フォルダーに配置（本体フォルダーは AdGuard Home が管理者専用にするため）
#   - 情報更新・自動更新タスク（起動時・ログオン時・毎日、SYSTEM 権限）を登録
#   - スタートメニューにショートカットを作成
# トレイアプリのログオン時自動起動（スタートアップフォルダーのショートカット）は、トレイのメニューから利用者本人が設定する。
param(
    [string]$AdGuardExe,     # 検証済みの AdGuardHome.exe。未指定なら公式サイトから取得
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'AdGuardHome'),
    [string]$TrayDir = (Join-Path $env:ProgramFiles 'AdGuardHome Tray')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Start-Transcript -Path (Join-Path $root 'install.log') -Force | Out-Null

# トレイと manage.ps1 の共有設定（manage.ps1 と同じ形式）。レジストリには保存しない
$dataDir = Join-Path $env:ProgramData 'AdGuardHome'
$settingsFile = Join-Path $dataDir 'settings.json'
$legacyKey = 'HKLM:\SOFTWARE\AdGuardHomeTray'

function Get-Settings {
    $h = @{}
    if (Test-Path $settingsFile) {
        $obj = [IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json
        foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = $p.Value }
    }
    return $h
}

function Set-Setting([string]$name, $value) {
    $h = Get-Settings
    $h[$name] = $value
    $tmp = "$settingsFile.tmp"
    [IO.File]::WriteAllText($tmp, ($h | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    Move-Item $tmp $settingsFile -Force
}

try {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '管理者権限で実行してください。'
    }

    $logDir = Join-Path $dataDir 'logs'
    New-Item -ItemType Directory -Force $InstallDir, $TrayDir, $logDir | Out-Null

    # ProgramData\AdGuardHome は SYSTEM・管理者が書き、ユーザーは読み取りのみ（既定の継承では Users に書き込みが付くため外す）
    & icacls.exe $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
    if ($LASTEXITCODE) { throw "フォルダーの権限設定に失敗しました ($LASTEXITCODE)" }
    & icacls.exe $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls.exe "$dataDir\*" /reset /T /C /Q | Out-Null

    # インストール済みのトレイだけを止める（セットアップ画面も同じ名前の exe なので、名前だけで止めない）
    $trayFull = [IO.Path]::GetFullPath($TrayDir).TrimEnd('\') + '\'
    Get-Process AdGuardHomeTray -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($trayFull, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force

    $exe = Join-Path $InstallDir 'AdGuardHome.exe'
    $agLog = Join-Path $logDir 'AdGuardHome.log'

    # 本体（未導入時のみ。導入後の更新は AdGuard Home 自身のアップデーターに任せる）
    if (-not (Test-Path $exe)) {
        if (-not $AdGuardExe) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $tmp = Join-Path $env:TEMP ('agh-' + [guid]::NewGuid())
            New-Item -ItemType Directory $tmp | Out-Null
            $zip = Join-Path $tmp 'AdGuardHome_windows_amd64.zip'
            Invoke-WebRequest 'https://static.adtidy.org/adguardhome/release/AdGuardHome_windows_amd64.zip' -OutFile $zip -UseBasicParsing
            Expand-Archive $zip (Join-Path $tmp 'x')
            $AdGuardExe = Join-Path $tmp 'x\AdGuardHome\AdGuardHome.exe'
        }
        $sig = Get-AuthenticodeSignature $AdGuardExe
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Adguard Software Limited') {
            throw "AdGuardHome.exe の署名を確認できません: $($sig.Status)"
        }
        Copy-Item $AdGuardExe $InstallDir -Force
    }

    # サービス登録。ログを ProgramData に出すよう引数を揃える（既存の登録が異なる場合は作り直す）
    $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AdGuardHome'
    $imagePath = (Get-ItemProperty $svcKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    if ($imagePath -and $imagePath -notlike "*$agLog*") {
        Push-Location $InstallDir
        try { & $exe -s uninstall } finally { Pop-Location }
        Start-Sleep 2
        $imagePath = $null
    }
    if (-not $imagePath) {
        Push-Location $InstallDir
        try { & $exe -s install -l $agLog } finally { Pop-Location }
        if ($LASTEXITCODE) { throw "サービス登録に失敗しました ($LASTEXITCODE)" }
    }
    Set-Service AdGuardHome -StartupType Automatic
    & sc.exe failure AdGuardHome reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
    if ((Get-Service AdGuardHome).Status -ne 'Running') { Start-Service AdGuardHome }

    # トレイアプリと管理スクリプト
    Copy-Item (Join-Path $root 'build\AdGuardHomeTray.exe') $TrayDir -Force
    # 管理画面ウィンドウ用の WebView2 SDK（同梱。描画するランタイムは Windows が自動更新する）
    Copy-Item (Join-Path $root 'lib\webview2\*.dll') $TrayDir -Force
    # 以前の版が作っていた読み込み先の設定（SDK の自動更新用）は不要になったので消す
    Remove-Item (Join-Path $TrayDir 'AdGuardHomeTray.exe.config') -Force -ErrorAction SilentlyContinue
    Get-ChildItem $TrayDir -Filter '*.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root 'scripts\manage.ps1') $TrayDir -Force
    Copy-Item (Join-Path $root 'uninstall.ps1') $TrayDir -Force

    # 設定値（自動更新は既定で有効）。旧版が HKLM に保存した自動更新の設定は引き継いでからキーを削除する
    if ($null -eq (Get-Settings).AutoUpdate) {
        $old = (Get-ItemProperty $legacyKey -Name AutoUpdate -ErrorAction SilentlyContinue).AutoUpdate
        Set-Setting AutoUpdate $(if ($null -ne $old) { [int]$old } else { 1 })
    }
    Set-Setting InstallDir $InstallDir
    Set-Setting TrayDir $TrayDir
    [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree('SOFTWARE\AdGuardHomeTray', $false)
    if (Test-Path $legacyKey) { throw "旧設定のレジストリ キーを削除できません: $legacyKey" }

    # 情報更新 + 自動更新タスク
    $manage = Join-Path $TrayDir 'manage.ps1'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$manage`" -Action AutoUpdate"
    $daily = New-ScheduledTaskTrigger -Daily -At '04:30' -RandomDelay (New-TimeSpan -Minutes 30)
    $boot = New-ScheduledTaskTrigger -AtStartup; $boot.Delay = 'PT3M'
    $logon = New-ScheduledTaskTrigger -AtLogOn; $logon.Delay = 'PT1M'
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName 'AdGuardHome AutoUpdate' -Action $action -Trigger $daily, $boot, $logon `
        -Settings $settings -Principal $taskPrincipal -Description 'AdGuard Home のバージョン情報を記録し、自動更新が有効なら最新版に更新します' -Force | Out-Null

    # 初回の情報記録
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $manage -Action Refresh

    # スタートメニュー: 「AdGuard Home」1 つだけ（管理画面を開く。トレイが動いていなければ起動してから開く）
    $programs = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $trayExe = Join-Path $TrayDir 'AdGuardHomeTray.exe'
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut((Join-Path $programs 'AdGuard Home.lnk'))
    $lnk.TargetPath = $trayExe
    $lnk.Arguments = '--dashboard'
    $lnk.WorkingDirectory = $TrayDir
    $lnk.Description = 'AdGuard Home の管理画面を開く'
    $lnk.Save()
    # 以前の版が作っていたフォルダー（トレイ・管理画面・アンインストールの 3 つ）は消す
    Remove-Item (Join-Path $programs 'AdGuard Home') -Recurse -Force -ErrorAction SilentlyContinue

    # 「設定 → アプリ → インストールされているアプリ」に登録（ここからアンインストールできる）。
    # 書くレジストリはこのアンインストール情報だけで、アンインストール時に削除する
    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdGuardHomeTray'
    New-Item -Path $uninstallKey -Force | Out-Null
    $size = [int]((Get-ChildItem $TrayDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $props = @{
        DisplayName     = 'AdGuard Home（トレイアプリ）'
        DisplayVersion  = (Get-Item $trayExe).VersionInfo.FileVersion
        Publisher       = 'AdGuard Home Tray (unofficial)'
        DisplayIcon     = "$trayExe,0"
        InstallLocation = $TrayDir
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $TrayDir 'uninstall.ps1')`""
        URLInfoAbout    = 'https://github.com/hinatamaxxx/adguard-home-windows-tray'
    }
    foreach ($k in $props.Keys) { Set-ItemProperty $uninstallKey -Name $k -Value $props[$k] }
    Set-ItemProperty $uninstallKey -Name EstimatedSize -Value $size -Type DWord
    Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

    # 旧レイアウトの残骸を掃除
    foreach ($f in 'AdGuardHomeTray.exe', 'manage.ps1', 'uninstall.ps1') { Remove-Item (Join-Path $InstallDir $f) -Force -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $InstallDir 'logs') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'INSTALL OK'
} catch {
    Write-Host ('INSTALL FAILED: ' + $_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage)
    Stop-Transcript | Out-Null
    exit 1
}
Stop-Transcript | Out-Null
exit 0
