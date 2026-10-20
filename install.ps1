# AdGuard Home + トレイアプリのインストーラー（管理者権限で実行）。
#   - AdGuard Home を Windows サービスとして登録（コンソール画面なし・PC 起動時に自動開始）
#   - トレイアプリと管理スクリプトを別フォルダーに配置（本体フォルダーは AdGuard Home が管理者専用にするため）
#   - 情報更新・自動更新タスク（起動時・ログオン時・毎日、SYSTEM 権限）を登録
#   - スタートメニューにショートカットを作成
# トレイアプリのログオン時自動起動（スタートアップフォルダーのショートカット）は、トレイのメニューから利用者本人が設定する。
param(
    [string]$AdGuardExe,     # 検証済みの AdGuardHome.exe。未指定なら公式サイトから取得
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'AdGuardHome'),
    [string]$TrayDir = (Join-Path $env:ProgramFiles 'AdGuardHome Tray'),
    [switch]$SelfUpdate      # manage.ps1（SYSTEM）からの自己更新。動いているトレイは止めず、既存のタスクは作り直さない
)

$ErrorActionPreference = 'Stop'
# 表示の言語（Windows の表示言語が日本語なら日本語、それ以外は英語）
$ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja'
function T([string]$jaText, [string]$enText) { if ($ja) { $jaText } else { $enText } }
$root = $PSScriptRoot
Start-Transcript -Path (Join-Path $root 'install.log') -Force | Out-Null

# トレイと manage.ps1 の共有設定（manage.ps1 と同じ形式）。レジストリには保存しない
$dataDir = Join-Path $env:ProgramData 'AdGuardHome'
$settingsFile = Join-Path $dataDir 'settings.json'

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

# 使用中のファイル（動いているトレイの exe・DLL）は上書きできないので、名前を変えてどかしてから置く。
# どかした *.old-* は次回のインストール・更新で消す
function Copy-Replace([string]$src, [string]$dstDir) {
    foreach ($f in Get-Item $src) {
        $dst = Join-Path $dstDir $f.Name
        try { Copy-Item $f.FullName $dst -Force }
        catch {
            Move-Item $dst ("$dst.old-" + [DateTime]::Now.Ticks) -Force
            Copy-Item $f.FullName $dst -Force
        }
    }
}

# タスクを、SYSTEM・管理者に加えて、ログオンした利用者（Authenticated Users）も実行できるようにする（UAC なしの「今すぐ更新」用）
function Grant-TaskRun([string]$name) {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $svc.GetFolder('\').GetTask($name).SetSecurityDescriptor('D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)', 0)
}

# ショートカットに AppUserModelID（System.AppUserModel.ID）を付ける
function Set-ShortcutAppId([string]$path, [string]$appId) {
    if (-not ('LnkAppId' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LnkAppId
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    public static void Set(string path, string appId)
    {
        var link = new ShellLink();
        ((IPersistFile)link).Load(path, 2);   // STGM_READWRITE
        var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        var value = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(appId) };   // VT_LPWSTR
        try
        {
            var store = (IPropertyStore)link;
            store.SetValue(ref key, ref value);
            store.Commit();
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FreeCoTaskMem(value.p); }
    }
}
'@
    }
    [LnkAppId]::Set($path, $appId)
}

try {
    $principal =[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw (T '管理者権限で実行してください。' 'Run this as an administrator.')
    }

    $logDir = Join-Path $dataDir 'logs'
    New-Item -ItemType Directory -Force $InstallDir, $TrayDir, $logDir | Out-Null

    # ProgramData\AdGuardHome は SYSTEM・管理者が書き、ユーザーは読み取りのみ（既定の継承では Users に書き込みが付くため外す）
    & icacls.exe $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
    if ($LASTEXITCODE) { throw ((T "フォルダーの権限設定に失敗しました" "Could not set folder permissions") + " ($LASTEXITCODE)") }
    & icacls.exe $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls.exe "$dataDir\*" /reset /T /C /Q | Out-Null

    # インストール済みのトレイだけを止める（セットアップ画面も同じ名前の exe なので、名前だけで止めない）。
    # 自己更新では止めない（SYSTEM からは利用者の画面に起動し直せないため。トレイが新しい exe に気づいて自分で再起動する）
    if (-not $SelfUpdate) {
        $trayFull = [IO.Path]::GetFullPath($TrayDir).TrimEnd('\') + '\'
        Get-Process AdGuardHomeTray -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($trayFull, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
    }

    $exe = Join-Path $InstallDir 'AdGuardHome.exe'
    $agLog = Join-Path $logDir 'AdGuardHome.log'

    # 本体（未導入時のみ。導入後の更新は AdGuard Home 自身のアップデーターに任せる）
    if (-not (Test-Path $exe)) {
        if (-not $AdGuardExe) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $tmp = Join-Path $env:TEMP ('agh-' + [guid]::NewGuid())
            New-Item -ItemType Directory $tmp | Out-Null
            # CPU に合った版（x64 / ARM64）。PowerShell がエミュレーションで動いていても OS 本来の種類を見る
            $arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
            $pkgName = if ($arch -eq 'ARM64') { 'AdGuardHome_windows_arm64.zip' } else { 'AdGuardHome_windows_amd64.zip' }
            $zip = Join-Path $tmp $pkgName
            Invoke-WebRequest "https://static.adtidy.org/adguardhome/release/$pkgName" -OutFile $zip -UseBasicParsing
            Expand-Archive $zip (Join-Path $tmp 'x')
            $AdGuardExe = Join-Path $tmp 'x\AdGuardHome\AdGuardHome.exe'
        }
        $sig = Get-AuthenticodeSignature $AdGuardExe
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Adguard Software Limited') {
            throw ((T "AdGuardHome.exe の署名を確認できません: " "Could not verify the signature of AdGuardHome.exe: ") + $sig.Status)
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
        if ($LASTEXITCODE) { throw ((T "サービス登録に失敗しました" "Could not register the service") + " ($LASTEXITCODE)") }
    }
    Set-Service AdGuardHome -StartupType Automatic
    & sc.exe failure AdGuardHome reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
    if ((Get-Service AdGuardHome).Status -ne 'Running') { Start-Service AdGuardHome }

    # トレイアプリと管理スクリプト
    Get-ChildItem $TrayDir -Filter '*.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Replace (Join-Path $root 'build\AdGuardHomeTray.exe') $TrayDir
    # 管理画面ウィンドウ用の WebView2 SDK（同梱。描画するランタイムは Windows が自動更新する）
    Copy-Replace (Join-Path $root 'lib\webview2\*.dll') $TrayDir
    Copy-Item (Join-Path $root 'scripts\manage.ps1') $TrayDir -Force
    Copy-Item (Join-Path $root 'uninstall.ps1') $TrayDir -Force

    # 設定値（自動更新は既定で有効）
    if ($null -eq (Get-Settings).AutoUpdate) { Set-Setting AutoUpdate 1 }
    Set-Setting InstallDir $InstallDir
    Set-Setting TrayDir $TrayDir

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
    # 自己更新は実行中のタスクから呼ばれるので、既存のタスクは作り直さない
    if (-not ($SelfUpdate -and (Get-ScheduledTask -TaskName 'AdGuardHome AutoUpdate' -ErrorAction SilentlyContinue))) {
        Register-ScheduledTask -TaskName 'AdGuardHome AutoUpdate' -Action $action -Trigger $daily, $boot, $logon `
            -Settings $settings -Principal $taskPrincipal -Description 'AdGuard Home とトレイアプリのバージョン情報を記録し、自動更新が有効なら最新版に更新します' -Force | Out-Null
    }
    # 「今すぐ確認して更新」用（トリガーなし）。トレイが UAC なしで起動できるよう、利用者にも実行を許可する
    if (-not ($SelfUpdate -and (Get-ScheduledTask -TaskName 'AdGuardHome UpdateNow' -ErrorAction SilentlyContinue))) {
        $nowAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$manage`" -Action Update"
        Register-ScheduledTask -TaskName 'AdGuardHome UpdateNow' -Action $nowAction -Settings $settings -Principal $taskPrincipal `
            -Description 'AdGuard Home とトレイアプリを今すぐ最新版に更新します（トレイのメニューから実行）' -Force | Out-Null
        Grant-TaskRun 'AdGuardHome UpdateNow'
    }


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
    # アプリと同じ ID（AppUserModelID）を付ける。通知の見出しに、このショートカットの名前とアイコンが出る
    Set-ShortcutAppId (Join-Path $programs 'AdGuard Home.lnk') 'AdGuardHomeTray'

    # 「設定 → アプリ → インストールされているアプリ」に登録（ここからアンインストールできる）。
    # 書くレジストリはこのアンインストール情報だけで、アンインストール時に削除する
    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdGuardHomeTray'
    New-Item -Path $uninstallKey -Force | Out-Null
    $size = [int]((Get-ChildItem $TrayDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $props = @{
        DisplayName     = 'AdGuard Home for Windows'
        DisplayVersion  = (Get-Item $trayExe).VersionInfo.FileVersion
        Publisher       = 'AdGuard Home for Windows (unofficial)'
        DisplayIcon     = "$trayExe,0"
        InstallLocation = $TrayDir
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $TrayDir 'uninstall.ps1')`""
        URLInfoAbout    = 'https://github.com/hinatamaxxx/adguard-home-windows'
    }
    foreach ($k in $props.Keys) { Set-ItemProperty $uninstallKey -Name $k -Value $props[$k] }
    Set-ItemProperty $uninstallKey -Name EstimatedSize -Value $size -Type DWord
    Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

    # 初回の情報記録。手元の AdGuard Home からロゴを取れたら、スタートメニューと上の登録のアイコンもそのロゴにする
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $manage -Action Refresh

    Write-Host 'INSTALL OK'
} catch {
    Write-Host ('INSTALL FAILED: ' + $_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage)
    Stop-Transcript | Out-Null
    exit 1
}
Stop-Transcript | Out-Null
exit 0
