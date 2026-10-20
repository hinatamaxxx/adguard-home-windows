# AdGuard Home の管理操作（管理者権限で実行される）。
# トレイアプリから UAC 昇格で、またはタスク（起動時・ログオン時・毎日、および利用者が UAC なしで起動できる「今すぐ更新」）から SYSTEM で呼ばれる。
# 終了コード: 0=成功/更新済み, 2=更新不要, 1=失敗
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Stop', 'Restart', 'Update', 'AutoUpdate', 'Refresh', 'EnableAutoUpdate', 'DisableAutoUpdate')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
$versionUrl = 'https://static.adtidy.org/adguardhome/release/version.json'
# トレイアプリの配布元（GitHub Releases）
$trayRepo = 'hinatamaxxx/adguard-home-windows-tray'
# トレイアプリと共有する設定。管理者・SYSTEM だけが書き、ユーザーは読み取りのみ（install.ps1 がフォルダーの権限を設定）
$settingsFile = Join-Path $env:ProgramData 'AdGuardHome\settings.json'

function Get-Settings {
    $h = @{}
    if (Test-Path $settingsFile) {
        $obj = [IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json
        foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = $p.Value }
    }
    return $h
}

# 一時ファイルに書いてから置き換え、読み取り中のトレイが壊れた JSON を見ないようにする
function Set-Setting([string]$name, $value) {
    $h = Get-Settings
    $h[$name] = $value
    $tmp = "$settingsFile.tmp"
    [IO.File]::WriteAllText($tmp, ($h | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    Move-Item $tmp $settingsFile -Force
}

$aghDir = (Get-Settings).InstallDir
if (-not $aghDir) { $aghDir = Join-Path $env:ProgramFiles 'AdGuardHome' }
$exe = Join-Path $aghDir 'AdGuardHome.exe'
$yaml = Join-Path $aghDir 'AdGuardHome.yaml'
$logDir = Join-Path $env:ProgramData 'AdGuardHome\logs'
$log = Join-Path $logDir 'manage.log'
New-Item -ItemType Directory -Force $logDir | Out-Null
# 更新・再起動でサービスを止めている間だけ置く印。トレイはこれを見て「停止しました」の警告を出さない
$maintFile = Join-Path $env:ProgramData 'AdGuardHome\maintenance.json'

function Enter-Maintenance([string]$what) {
    $json = @{ action = $what; started = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($maintFile, $json, (New-Object Text.UTF8Encoding $false))
}

function Exit-Maintenance { Remove-Item $maintFile -Force -ErrorAction SilentlyContinue }

# 更新や再起動が途中で失敗しても、DNS が止まったままにならないようにサービスを動かす
function Confirm-ServiceRunning([string]$context) {
    $svc = Get-Service AdGuardHome -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Running') { return }
    try { Start-Service AdGuardHome; Log "service started after $context" }
    catch { Log ("ERROR: could not start service after {0}: {1}" -f $context, $_.Exception.Message) }
}

function Log([string]$msg) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $Action, $msg
    Add-Content -Path $log -Value $line -Encoding UTF8
    # 1MB を超えたら古い分を退避
    if ((Get-Item $log).Length -gt 1MB) { Move-Item $log "$log.old" -Force }
}

function Get-Ver([string]$s) {
    if ($s -match '(\d+)\.(\d+)\.(\d+)') { return [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" }
    return $null
}

function Get-CurrentVersion {
    $out = (& $exe --version 2>&1 | Out-String).Trim()
    if ($out -match 'v\d+\.\d+\.\d+\S*') { return $Matches[0] }
    return $out
}

# トレイのアイコンに使う AdGuard のロゴを、インストールした AdGuard Home の管理画面から取得する
# （ロゴは AdGuard 社の商標のため、このリポジトリには含めない）。取得できなければトレイは独自の盾アイコンを使う。
function Save-Logo([string]$baseUrl) {
    $dir = Join-Path $env:ProgramData 'AdGuardHome'
    # 図形データ（SVG。どの大きさでもくっきり描ける）と、予備の画像（PNG）
    $files = @(@{ Name = 'logo.svg'; Path = 'assets/safari-pinned-tab.svg'; Check = { param($b) ([Text.Encoding]::UTF8.GetString($b)) -match '<svg' } },
               @{ Name = 'logo.png'; Path = 'assets/apple-touch-icon-180x180.png'; Check = { param($b) $b.Length -gt 8 -and $b[0] -eq 0x89 -and $b[1] -eq 0x50 } })
    foreach ($f in $files) {
        $dst = Join-Path $dir $f.Name
        if (Test-Path $dst) { continue }
        foreach ($u in @($baseUrl, 'http://127.0.0.1:3000/') | Select-Object -Unique) {
            try {
                $tmp = "$dst.tmp"
                Invoke-WebRequest ($u.TrimEnd('/') + '/' + $f.Path) -OutFile $tmp -UseBasicParsing -TimeoutSec 10
                if (& $f.Check ([IO.File]::ReadAllBytes($tmp))) { Move-Item $tmp $dst -Force; Log "$($f.Name) saved from $u"; break }
                Remove-Item $tmp -Force
            } catch { Remove-Item "$dst.tmp" -Force -ErrorAction SilentlyContinue }
        }
    }
}# トレイアプリが読む情報（バージョン・管理画面 URL）を settings.json に記録する
function Save-Info {
    Set-Setting Version (Get-CurrentVersion)
    $url = 'http://127.0.0.1:3000/'
    if (Test-Path $yaml) {
        $text = Get-Content $yaml -Raw
        if ($text -match '(?m)^http:\s*\r?\n(?:[ \t]+.*\r?\n)*?[ \t]+address:\s*(\S+)') {
            $addr = $Matches[1].Trim('"', "'")
            $i = $addr.LastIndexOf(':')
            $h = $addr.Substring(0, $i); $p = $addr.Substring($i + 1)
            if ($h -in '0.0.0.0', '', '[::]', '::') { $h = '127.0.0.1' }
            $url = 'http://' + $h + $(if ($p -eq '80') { '' } else { ":$p" }) + '/'
        }
    }
    Set-Setting WebUrl $url
    Save-Logo $url}

# AdGuard Home 本体の更新。0=更新した, 2=最新
function Invoke-AghUpdate {
    $cur = Get-CurrentVersion
    $latest = (Invoke-RestMethod -Uri $versionUrl -UseBasicParsing -TimeoutSec 60).version
    $vc = Get-Ver $cur; $vl = Get-Ver $latest
    Log "AdGuard Home: current=$cur latest=$latest"
    if (-not $vc -or -not $vl) { throw "version parse failed (current='$cur', latest='$latest')" }
    if ($vl -le $vc) { return 2 }

    # AdGuard Home 内蔵のアップデーター: バイナリを置換し、サービスを再起動する
    Enter-Maintenance 'Update'
    try {
        Push-Location $aghDir
        try {
            $out = & $exe --update 2>&1 | Out-String
            $code = $LASTEXITCODE
        } finally { Pop-Location }
        Log ("--update exit={0} output={1}" -f $code, $out.Trim())
        if ($code -ne 0) { throw "AdGuardHome --update failed ($code)" }
        Start-Sleep -Seconds 3
    } finally {
        Confirm-ServiceRunning 'update'
        Exit-Maintenance
    }
    Save-Info
    Log ('AdGuard Home updated to ' + (Get-CurrentVersion))
    return 0
}

# トレイアプリ自身の更新（GitHub Releases の最新版）。SYSTEM で動くので UAC は出ない。0=更新した, 2=最新
# 取得元はこのリポジトリのリリースに固定し、同じリリースの SHA-256 と一致したものだけを入れる。
function Invoke-TrayUpdate {
    $trayDir = (Get-Settings).TrayDir
    if (-not $trayDir) { $trayDir = Join-Path $env:ProgramFiles 'AdGuardHome Tray' }
    $trayExe = Join-Path $trayDir 'AdGuardHomeTray.exe'
    if (-not (Test-Path $trayExe)) { Log 'tray: not installed; skip'; return 2 }

    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$trayRepo/releases/latest" -UseBasicParsing -TimeoutSec 60 `
        -Headers @{ 'User-Agent' = 'AdGuardHomeTray-manage'; 'Accept' = 'application/vnd.github+json' }
    $cur = (Get-Item $trayExe).VersionInfo.FileVersion
    $vc = Get-Ver $cur; $vl = Get-Ver $rel.tag_name
    Log "tray: current=$cur latest=$($rel.tag_name)"
    if (-not $vc -or -not $vl) { throw "tray version parse failed (current='$cur', latest='$($rel.tag_name)')" }
    Set-Setting TrayLatestVersion ([string]$vl)
    if ($vl -le $vc) { return 2 }

    $prefix = "https://github.com/$trayRepo/releases/download/"
    $zipAsset = $rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip' } | Select-Object -First 1
    $sumAsset = $rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip.sha256' } | Select-Object -First 1
    if (-not $zipAsset -or -not $sumAsset) { throw 'tray: release has no AdGuardHomeTray.zip / .sha256' }
    foreach ($a in $zipAsset, $sumAsset) {
        if (-not $a.browser_download_url.StartsWith($prefix)) { throw "tray: unexpected download URL $($a.browser_download_url)" }
    }

    $work = Join-Path $env:ProgramData ('AdGuardHome\update\' + [guid]::NewGuid())
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        $zip = Join-Path $work 'AdGuardHomeTray.zip'
        Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing -TimeoutSec 300
        $sum = (Invoke-WebRequest $sumAsset.browser_download_url -UseBasicParsing -TimeoutSec 60).Content
        if ($sum -is [byte[]]) { $sum = [Text.Encoding]::ASCII.GetString($sum) }
        $expected = ($sum.Trim() -split '\s+')[0]
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected.Trim()) { throw "tray: SHA-256 mismatch (expected=$expected actual=$actual)" }

        Expand-Archive $zip (Join-Path $work 'x')
        $pkg = Join-Path $work 'x\AdGuardHomeTray'
        $newExe = Join-Path $pkg 'build\AdGuardHomeTray.exe'
        if (-not (Test-Path $newExe) -or -not (Test-Path (Join-Path $pkg 'install.ps1'))) { throw 'tray: unexpected package layout' }
        if ((Get-Ver (Get-Item $newExe).VersionInfo.FileVersion) -ne $vl) { throw 'tray: package version does not match the release' }

        # 新しい版の install.ps1 で入れ替える。動いているトレイは止めず、新しい exe に気づいて自分で再起動する
        Enter-Maintenance 'TrayUpdate'
        try {
            $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'install.ps1') -SelfUpdate -InstallDir $aghDir -TrayDir $trayDir 2>&1 | Out-String
            $code = $LASTEXITCODE
        } finally { Exit-Maintenance }
        if ($code -ne 0) { throw ("tray: install.ps1 failed ($code): " + $out.Trim()) }
        Log "tray updated to $vl"
        return 0
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# AdGuard Home とトレイアプリを、それぞれ独立に更新する（片方が失敗してももう片方は進める）。
# 結果は settings.json に記録し、トレイは連続して失敗したときだけ通知する。0=どれか更新した, 2=どちらも最新, 1=失敗あり
function Invoke-Update([bool]$respectSetting) {
    if ($respectSetting -and (Get-Settings).AutoUpdate -ne 1) { Log 'auto update is disabled; skip'; return 2 }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $codes = @(); $errors = @()
    foreach ($step in @(@{ Name = 'AdGuard Home'; Run = { Invoke-AghUpdate } }, @{ Name = 'tray'; Run = { Invoke-TrayUpdate } })) {
        try { $codes += [int](& $step.Run | Select-Object -Last 1) }
        catch { $codes += 1; $errors += "$($step.Name): $($_.Exception.Message)"; Log ("ERROR: {0}: {1}" -f $step.Name, $_.Exception.Message) }
    }
    Set-Setting LastUpdateCheck ((Get-Date).ToUniversalTime().ToString('o'))
    if ($errors) {
        Set-Setting UpdateFailures ([int]((Get-Settings).UpdateFailures) + 1)
        Set-Setting LastUpdateError ($errors -join ' / ')
        return 1
    }
    Set-Setting UpdateFailures 0
    Set-Setting LastUpdateError ''
    if ($codes -contains 0) { return 0 }
    Log 'already up to date'
    return 2
}

try {
    switch ($Action) {
        'Start'   { Start-Service AdGuardHome; Log 'started'; exit 0 }
        'Stop'    { Stop-Service AdGuardHome; Log 'stopped'; exit 0 }
        'Restart' {
            Enter-Maintenance 'Restart'
            try { Restart-Service AdGuardHome } finally { Confirm-ServiceRunning 'restart'; Exit-Maintenance }
            Save-Info; Log 'restarted'; exit 0
        }
        'Refresh' { Save-Info; exit 0 }
        'Update'  { Save-Info; exit (Invoke-Update $false) }
        'AutoUpdate' {
            Save-Info
            $code = Invoke-Update $true
            exit $code
        }
        'EnableAutoUpdate' {
            Set-Setting AutoUpdate 1
            Log 'auto update enabled'; exit 0
        }
        'DisableAutoUpdate' {
            Set-Setting AutoUpdate 0
            Log 'auto update disabled'; exit 0
        }
    }
} catch {
    Log ('ERROR: ' + $_.Exception.Message)
    exit 1
}
