# AdGuard Home の「永続的クライアント」を API で登録・名前変更する。
# 識別子（ClientID・IP・MAC など）に表示名を付けると、クエリログや統計にその名前で出る。
# プロファイルや Funnel の設定は変えない。
#
# 例: プロファイルの ClientID が iphone の端末を「iPhone17」と表示する
#   powershell -ExecutionPolicy Bypass -File set-client-name.ps1 -Id iphone -Name iPhone17
# 管理画面のユーザー名・パスワードは実行時に Windows の入力画面で聞く（ファイルや引数に書かない）。
param(
    [Parameter(Mandatory)][string]$Id,            # 識別子（例: iphone、192.168.1.20、aa:bb:cc:dd:ee:ff）
    [Parameter(Mandatory)][string]$Name,          # 表示名（例: iPhone17）
    [string]$BaseUrl = 'http://127.0.0.1/',       # 管理画面の URL
    [pscredential]$Credential
)
$ErrorActionPreference = 'Stop'
if (-not $Credential) { $Credential = Get-Credential -Message 'AdGuard Home の管理画面のユーザー名とパスワード' }
$pair = $Credential.UserName + ':' + $Credential.GetNetworkCredential().Password
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair)) }
$base = $BaseUrl.TrimEnd('/')

function Call($method, $path, $body) {
    $p = @{ Method = $method; Uri = "$base/$path"; Headers = $headers; UseBasicParsing = $true }
    if ($body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6 -Compress)); $p.ContentType = 'application/json' }
    Invoke-RestMethod @p
}

$clients = @((Call GET 'control/clients').clients) | Where-Object { $_ }
$existing = $clients | Where-Object { $_.ids -contains $Id } | Select-Object -First 1

if ($existing) {
    # 既存の設定はそのままに、名前だけ変える
    $data = $existing | Select-Object * -ExcludeProperty name
    $data | Add-Member -NotePropertyName name -NotePropertyValue $Name -Force
    Call POST 'control/clients/update' @{ name = $existing.name; data = $data } | Out-Null
    "変更しました: $($existing.name) → $Name（識別子 $Id）"
} else {
    $body = @{
        name = $Name; ids = @($Id); tags = @(); upstreams = @()
        use_global_settings = $true; use_global_blocked_services = $true
        filtering_enabled = $true; parental_enabled = $false; safebrowsing_enabled = $false
    }
    Call POST 'control/clients/add' $body | Out-Null
    "登録しました: $Name（識別子 $Id、フィルターなどは全体の設定を使用）"
}
