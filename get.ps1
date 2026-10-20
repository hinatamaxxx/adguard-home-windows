# AdGuard Home for Windows をコマンド 1 行で導入する。PowerShell に次を貼り付けて実行する（管理者でなくてよい）:
#   irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/get.ps1 | iex
# 最新のリリースの ZIP を取得し、同じリリースの SHA-256 と照合してから、画面を出さずにインストールする
# （管理者権限が要るので UAC の確認だけは出る）。続けて、ログオン時の自動起動を設定してトレイアプリを起動する。
# AdGuard Home 本体は、install.ps1 が AdGuard 公式の配布元から取得し、署名を確認して入れる。
# このファイルは irm | iex で読み込むため、BOM なしの UTF-8 で保存する（BOM があると iex が失敗する）。
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # 進捗表示があると Windows PowerShell のダウンロードが極端に遅くなる
    $repo = 'hinatamaxxx/adguard-home-windows'
    # Windows の表示言語（PowerShell 自体の設定に左右されないよう、OS に直接たずねる）
    try { $ja = (((Add-Type -MemberDefinition '[DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();' -Name UiLanguage -Namespace AdGuardHomeSetup -PassThru)::GetUserDefaultUILanguage()) -band 0x3FF) -eq 0x11 } catch { $ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja' }
    function T([string]$jaText, [string]$enText) { if ($ja) { $jaText } else { $enText } }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    Write-Host (T 'AdGuard Home for Windows の最新版を確認しています...' 'Checking the latest version of AdGuard Home for Windows...')
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'AdGuardHomeTray-get'; 'Accept' = 'application/vnd.github+json' }
    $prefix = "https://github.com/$repo/releases/download/"
    $zipUrl = ($rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip' } | Select-Object -First 1).browser_download_url
    $sumUrl = ($rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip.sha256' } | Select-Object -First 1).browser_download_url
    if (-not $zipUrl -or -not $sumUrl -or -not $zipUrl.StartsWith($prefix) -or -not $sumUrl.StartsWith($prefix)) {
        throw (T 'リリースに AdGuardHomeTray.zip が見つかりません。' 'AdGuardHomeTray.zip was not found in the release.')
    }

    $work = Join-Path $env:TEMP ('AdGuardHomeTray-setup-' + [guid]::NewGuid())
    New-Item -ItemType Directory $work | Out-Null
    try {
        $zip = Join-Path $work 'AdGuardHomeTray.zip'
        Write-Host (T "$($rel.tag_name) をダウンロードしています..." "Downloading $($rel.tag_name)...")
        Invoke-WebRequest $zipUrl -OutFile $zip -UseBasicParsing
        $sum = (Invoke-WebRequest $sumUrl -UseBasicParsing).Content
        if ($sum -is [byte[]]) { $sum = [Text.Encoding]::ASCII.GetString($sum) }
        $expected = ($sum.Trim() -split '\s+')[0]
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) {
            throw (T 'ダウンロードしたファイルが一致しません（SHA-256）。もう一度実行してください。' 'The downloaded file does not match (SHA-256). Please run the command again.')
        }
        # Expand-Archive は、言語の表示用データが欠けた Windows（Windows Sandbox など）で読み込めないことがあるので、.NET で展開する
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $work 'x'))
        $pkg = Join-Path $work 'x\AdGuardHomeTray'
        if (-not (Test-Path (Join-Path $pkg 'install.ps1'))) { throw (T 'ZIP の中身が想定と違います。' 'The ZIP file does not contain the expected files.') }

        Write-Host (T 'インストールしています。「このアプリがデバイスに変更を加えることを許可しますか？」と表示されたら「はい」を選んでください...' 'Installing. When Windows asks "Do you want to allow this app to make changes to your device?", choose Yes...')
        try {
            $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
                -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $pkg 'install.ps1')`""
        } catch {
            throw (T '管理者の許可が得られなかったため、中止しました。' 'Canceled because administrator permission was not given.')
        }
        if ($p.ExitCode -ne 0) {
            $reason = Select-String (Join-Path $pkg 'install.log') -Pattern 'INSTALL FAILED: (.*?)( @ |$)' -ErrorAction SilentlyContinue | Select-Object -Last 1
            throw ((T 'インストールに失敗しました: ' 'Installation failed: ') + $(if ($reason) { $reason.Matches[0].Groups[1].Value } else { "exit code $($p.ExitCode)" }))
        }

        # ログオン時の自動起動（スタートアップフォルダーのショートカット。トレイのメニューや Windows の設定でオフにできる）
        $trayDir = Join-Path $env:ProgramFiles 'AdGuardHome Tray'
        try { $set = Get-Content (Join-Path $env:ProgramData 'AdGuardHome\settings.json') -Raw | ConvertFrom-Json; if ($set.TrayDir) { $trayDir = $set.TrayDir } } catch { }
        $trayExe = Join-Path $trayDir 'AdGuardHomeTray.exe'
        $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'AdGuard Home.lnk'))
        $lnk.TargetPath = $trayExe
        $lnk.WorkingDirectory = $trayDir
        $lnk.IconLocation = "$trayExe,0"
        $lnk.Description = (T 'AdGuard Home を通知領域から管理' 'Manage AdGuard Home from the notification area')
        $lnk.Save()

        if (-not (Get-Process AdGuardHomeTray -ErrorAction SilentlyContinue)) { Start-Process $trayExe -WorkingDirectory $trayDir }
        Write-Host (T 'インストールしました。通知領域の AdGuard Home のアイコンをダブルクリックすると、初期設定の画面が開きます。' 'Installed. Double-click the AdGuard Home icon in the notification area to start the initial setup.')
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
