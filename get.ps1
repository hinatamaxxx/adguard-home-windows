# AdGuard Home for Windows をコマンド 1 行で導入する。PowerShell に次を貼り付けて実行する（管理者でなくてよい）:
#   irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/get.ps1 | iex
# 最新のリリースの ZIP を取得し、同じリリースの SHA-256 と照合してから、セットアップ画面を開く。
# AdGuard Home 本体は、セットアップ（install.ps1）が AdGuard 公式の配布元から取得し、署名を確認して入れる。
# このファイルは irm | iex で読み込むため、BOM なしの UTF-8 で保存する（BOM があると iex が失敗する）。
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # 進捗表示があると Windows PowerShell のダウンロードが極端に遅くなる
    $repo = 'hinatamaxxx/adguard-home-windows'
    $ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja'   # 表示の言語
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
        Expand-Archive $zip (Join-Path $work 'x')
        $exe = Join-Path $work 'x\AdGuardHomeTray\build\AdGuardHomeTray.exe'
        if (-not (Test-Path $exe)) { throw (T 'ZIP の中身が想定と違います。' 'The ZIP file does not contain the expected files.') }

        Write-Host (T 'セットアップ画面を開きます。「インストール」を押し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を選んでください。' 'Opening the setup window. Click Install, and choose Yes when Windows asks "Do you want to allow this app to make changes to your device?"')
        Start-Process $exe '--setup' -Wait
        Write-Host (T 'セットアップ画面を閉じました。インストールした場合は、通知領域の AdGuard Home のアイコンをダブルクリックすると、初期設定の画面が開きます。' 'The setup window was closed. If you installed it, double-click the AdGuard Home icon in the notification area to start the initial setup.')
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
