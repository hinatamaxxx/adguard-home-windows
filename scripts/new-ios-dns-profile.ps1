# 自宅 PC の AdGuard Home を、外出先の iPhone / iPad から暗号化 DNS（DoH）で使うための構成プロファイルを作る。
#   1) 推測できない鍵（ランダムな文字列）を作る
#   2) Tailscale Funnel で https://<PC>.<tailnet>.ts.net/dns-query/<鍵> だけを公開し、
#      AdGuard Home の /dns-query/<ClientID> へ中継する（鍵付きパス以外は Tailscale が 404 で拒否）
#   3) その URL を入れた .mobileconfig を作る
# 鍵は合鍵なので、出力したプロファイルを他人に渡さない。このスクリプト自体には秘密を書かない。
#
# 使い方（このPCで、Tailscale にログイン済み・Funnel 有効化済みの状態で）:
#   powershell -ExecutionPolicy Bypass -File new-ios-dns-profile.ps1 -ClientId my-iphone -DisplayName "My iPhone" -OutDir C:\path\to\out
#   -NoFunnel を付けると Funnel を変更せず、プロファイルだけ作る（URL の確認用）
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,63}$')][string]$ClientId,   # AdGuard Home に表示される端末名（ClientID）
    [string]$DisplayName = 'AdGuard Home',                                           # iPhone の「DNS」に表示される名前
    [string]$OutDir = (Get-Location).Path,
    [string]$ServerName,          # 例: desktop-xxxx.tailxxxx.ts.net（省略時は tailscale status から取得）
    [int]$AdGuardPort = 80,       # AdGuard Home の管理画面（HTTP）のポート
    [switch]$NoFunnel
)
$ErrorActionPreference = 'Stop'

$ts = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not $ServerName) {
    if (-not (Test-Path $ts)) { throw 'Tailscale が見つかりません。-ServerName を指定してください。' }
    $ServerName = ((& $ts status --json | ConvertFrom-Json).Self.DNSName).TrimEnd('.')
}

# 1) 鍵（紛らわしい文字を除いた 24 文字）
$chars = [char[]]'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$secret = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
$url = "https://$ServerName/dns-query/$secret"

# 2) Funnel に鍵付きパスを追加（既存の公開設定は残る）
if (-not $NoFunnel) {
    & $ts funnel --bg --set-path "/dns-query/$secret" "http://127.0.0.1:$AdGuardPort/dns-query/$ClientId" | Out-Null
    if ($LASTEXITCODE) { throw "tailscale funnel に失敗しました（Funnel が有効か確認してください）" }
}

# 3) プロファイル（端末ごとに識別子を分けるので、複数台に別々の鍵を配れる）
$u1 = [guid]::NewGuid().ToString().ToUpper(); $u2 = [guid]::NewGuid().ToString().ToUpper()
$esc = [Security.SecurityElement]::Escape($DisplayName)
$xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>PayloadContent</key>
  <array>
    <dict>
      <key>DNSSettings</key>
      <dict>
        <key>DNSProtocol</key>
        <string>HTTPS</string>
        <key>ServerURL</key>
        <string>$url</string>
      </dict>
      <key>PayloadDisplayName</key>
      <string>$esc</string>
      <key>PayloadIdentifier</key>
      <string>local.adguardhome.dns.$ClientId.$u1</string>
      <key>PayloadType</key>
      <string>com.apple.dnsSettings.managed</string>
      <key>PayloadUUID</key>
      <string>$u1</string>
      <key>PayloadVersion</key>
      <integer>1</integer>
    </dict>
  </array>
  <key>PayloadDescription</key>
  <string>自宅PCの AdGuard Home を暗号化DNS（DNS-over-HTTPS）で使います。</string>
  <key>PayloadDisplayName</key>
  <string>$esc</string>
  <key>PayloadIdentifier</key>
  <string>local.adguardhome.profile.$ClientId</string>
  <key>PayloadRemovalDisallowed</key>
  <false/>
  <key>PayloadType</key>
  <string>Configuration</string>
  <key>PayloadUUID</key>
  <string>$u2</string>
  <key>PayloadVersion</key>
  <integer>1</integer>
</dict>
</plist>
"@
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path $OutDir "AdGuardHome-DNS-$ClientId.mobileconfig"
[IO.File]::WriteAllText($out, $xml, (New-Object Text.UTF8Encoding $false))
[xml](Get-Content $out -Raw -Encoding UTF8) | Out-Null   # XML として正しいか確認

[pscustomobject]@{
    Profile  = $out
    ClientId = $ClientId
    Server   = $ServerName
    Funnel   = $(if ($NoFunnel) { '変更なし（-NoFunnel）' } else { "/dns-query/<鍵> → http://127.0.0.1:$AdGuardPort/dns-query/$ClientId" })
}
