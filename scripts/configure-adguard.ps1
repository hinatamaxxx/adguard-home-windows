# AdGuard Home の設定ファイル（AdGuardHome.yaml）を、外出先から使うためや高速化のために変更する（管理者で実行）。
# 変更前にバックアップを取り、AdGuard Home 自身の --check-config で検証してから適用する。
#   -EnableFunnelDoH  Tailscale Funnel 経由の DNS-over-HTTPS を受ける（http.doh.insecure_enabled: true）。
#                     TLS は Funnel が終端し、PC 内では HTTP で受けるため。iOS の DNS プロファイルを使う場合に必要
#   -Faster           レート制限をなくし（家の機器と PC 自身で枠を共有して問い合わせが捨てられるのを防ぐ）、
#                     上流 DNS を Cloudflare / Google / Quad9 への並列問い合わせ、楽観的キャッシュ、キャッシュ 32MB にする
# 例: powershell -ExecutionPolicy Bypass -File configure-adguard.ps1 -EnableFunnelDoH -Faster
param([switch]$EnableFunnelDoH, [switch]$Faster, [string]$InstallDir = (Join-Path $env:ProgramFiles 'AdGuardHome'))

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '管理者の PowerShell で実行してください。' }
if (-not $EnableFunnelDoH -and -not $Faster) { throw '-EnableFunnelDoH か -Faster を指定してください。' }

$yaml = Join-Path $InstallDir 'AdGuardHome.yaml'
$exe = Join-Path $InstallDir 'AdGuardHome.exe'
if (-not (Test-Path $yaml)) { throw "設定ファイルがありません（初期設定がまだ？）: $yaml" }
$text = [IO.File]::ReadAllText($yaml)
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

function SetScalar([string]$t, [string]$indentKey, [string]$value) {
    $re = "(?m)^(\s*${indentKey}:)[ \t]*\S.*$"
    if (-not [regex]::IsMatch($t, $re)) { throw "$indentKey が設定ファイルに見つかりません" }
    return [regex]::Replace($t, $re, "`${1} $value", 1)
}
function SetList([string]$t, [string]$key, [string[]]$items) {
    $body = ($items | ForEach-Object { "    - $_$nl" }) -join ''
    $re = "(?m)^  ${key}:[ \t]*(\[\])?[ \t]*\r?\n(?:    - .*\r?\n)*"
    if (-not [regex]::IsMatch($t, $re)) { throw "$key が設定ファイルに見つかりません" }
    return [regex]::Replace($t, $re, "  ${key}:$nl$body", 1)
}

$new = $text
if ($EnableFunnelDoH) {
    if ($new -match '(?m)^\s+insecure_enabled:') { $new = SetScalar $new 'insecure_enabled' 'true' }      # v0.107.5x 以降
    else { $new = SetScalar $new 'allow_unencrypted_doh' 'true' }                                        # 古い版
}
if ($Faster) {
    $new = SetList $new 'upstream_dns' @('https://dns.cloudflare.com/dns-query', 'https://dns.google/dns-query', 'https://dns10.quad9.net/dns-query')
    $new = SetList $new 'bootstrap_dns' @('1.1.1.1', '8.8.8.8', '9.9.9.10', '2606:4700:4700::1111', '2001:4860:4860::8888')
    $new = SetScalar $new '  ratelimit' '0'
    $new = SetScalar $new '  upstream_mode' 'parallel'
    $new = SetScalar $new '  cache_size' '33554432'
    $new = SetScalar $new '  cache_optimistic' 'true'
}
if ($new -eq $text) { 'すでに設定済みです。'; exit 0 }

# 検証してから適用
$check = Join-Path $InstallDir 'AdGuardHome.check.yaml'
[IO.File]::WriteAllText($check, $new, (New-Object Text.UTF8Encoding $false))
$ErrorActionPreference = 'Continue'
$out = & $exe --check-config -c $check -w $InstallDir --no-permcheck 2>&1 | Out-String
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Remove-Item $check -Force
if ($code -ne 0) { throw "設定の検証に失敗したため、変更していません: $out" }

$backup = "$yaml.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item $yaml $backup
Stop-Service AdGuardHome
try { [IO.File]::WriteAllText($yaml, $new, (New-Object Text.UTF8Encoding $false)) }
finally { Start-Service AdGuardHome }
"変更しました（バックアップ: $backup）"
