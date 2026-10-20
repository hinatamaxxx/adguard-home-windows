# AdGuard Home Tray をコマンド 1 行で導入する。PowerShell に次を貼り付けて実行する（管理者でなくてよい）:
#   irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows-tray/main/get.ps1 | iex
# 最新のリリースの ZIP を取得し、同じリリースの SHA-256 と照合してから、セットアップ画面を開く。
# AdGuard Home 本体は、セットアップ（install.ps1）が AdGuard 公式の配布元から取得し、署名を確認して入れる。
# このファイルは irm | iex で読み込むため、BOM なしの UTF-8 で保存する（BOM があると iex が失敗する）。
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # 進捗表示があると Windows PowerShell のダウンロードが極端に遅くなる
    $repo = 'hinatamaxxx/adguard-home-windows-tray'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    Write-Host 'AdGuard Home Tray の最新版を確認しています...'
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'AdGuardHomeTray-get'; 'Accept' = 'application/vnd.github+json' }
    $prefix = "https://github.com/$repo/releases/download/"
    $zipUrl = ($rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip' } | Select-Object -First 1).browser_download_url
    $sumUrl = ($rel.assets | Where-Object { $_.name -eq 'AdGuardHomeTray.zip.sha256' } | Select-Object -First 1).browser_download_url
    if (-not $zipUrl -or -not $sumUrl -or -not $zipUrl.StartsWith($prefix) -or -not $sumUrl.StartsWith($prefix)) {
        throw 'リリースに AdGuardHomeTray.zip が見つかりません。'
    }

    $work = Join-Path $env:TEMP ('AdGuardHomeTray-setup-' + [guid]::NewGuid())
    New-Item -ItemType Directory $work | Out-Null
    try {
        $zip = Join-Path $work 'AdGuardHomeTray.zip'
        Write-Host "$($rel.tag_name) をダウンロードしています..."
        Invoke-WebRequest $zipUrl -OutFile $zip -UseBasicParsing
        $sum = (Invoke-WebRequest $sumUrl -UseBasicParsing).Content
        if ($sum -is [byte[]]) { $sum = [Text.Encoding]::ASCII.GetString($sum) }
        $expected = ($sum.Trim() -split '\s+')[0]
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) {
            throw 'ダウンロードしたファイルが一致しません（SHA-256）。もう一度実行してください。'
        }
        Expand-Archive $zip (Join-Path $work 'x')
        $exe = Join-Path $work 'x\AdGuardHomeTray\build\AdGuardHomeTray.exe'
        if (-not (Test-Path $exe)) { throw 'ZIP の中身が想定と違います。' }

        Write-Host 'セットアップ画面を開きます。「インストール」を押し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を選んでください。'
        Start-Process $exe '--setup' -Wait
        Write-Host 'セットアップ画面を閉じました。インストールした場合は、通知領域の AdGuard Home のアイコンをダブルクリックすると、初期設定の画面が開きます。'
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
