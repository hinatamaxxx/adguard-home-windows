# トレイアプリ (AdGuardHomeTray.exe) をビルドする。.NET Framework 4.x の csc.exe を使用。
# -Screenshot: README のスクリーンショット撮影用ビルド（build\screenshot\ に出力。配布しない）
param([switch]$Screenshot)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = if ($Screenshot) { Join-Path $root 'build\screenshot' } else { Join-Path $root 'build' }
$src = Join-Path $root 'src\*.cs'
$manifest = '/win32manifest:' + (Join-Path $root 'src\app.manifest')
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.ServiceProcess.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll', $manifest, ('/r:' + (Join-Path $root 'lib\webview2\Microsoft.Web.WebView2.Core.dll')), ('/r:' + (Join-Path $root 'lib\webview2\Microsoft.Web.WebView2.WinForms.dll'))
if ($Screenshot) { $refs += '/define:SCREENSHOT_BUILD' }
$exe = Join-Path $out 'AdGuardHomeTray.exe'
$tmpExe = Join-Path $out 'icon-gen.exe'

# 1回目: アイコン生成用にビルドし、描画コードから .ico (PNG 格納形式) を作る
& $csc /nologo /target:winexe /optimize+ "/out:$tmpExe" @refs $src
if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }

$ico = Join-Path $out 'AdGuardHomeTray.ico'
$code = @'
param($asmPath, $icoPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($asmPath))
$t = $asm.GetType('AdGuardHomeTray.TrayContext')
$m = $t.GetMethod('RenderIcon', [Reflection.BindingFlags]'Static,NonPublic,Public')
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = $m.Invoke($null, @([int]$s, [int]0))
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$fs = [IO.File]::Create($icoPath); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
$prev = New-Object Drawing.Bitmap 360, 100
$g = [Drawing.Graphics]::FromImage($prev); $g.Clear([Drawing.Color]::FromArgb(240,240,240))
for ($st = 0; $st -lt 4; $st++) { $x = 10 + $st * 88; foreach ($s in 16, 24, 48) { $b = $m.Invoke($null, @([int]$s, [int]$st)); $g.DrawImageUnscaled($b, $x, 10); $x += $s + 2 } }
$g.Dispose(); $prev.Save((Join-Path (Split-Path $icoPath) 'icon-preview.png'))
'@
$gen = Join-Path $out 'icon-gen.ps1'
Set-Content -Path $gen -Value $code -Encoding UTF8
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $gen $tmpExe $ico
if ($LASTEXITCODE) { throw "icon generation failed" }
Remove-Item $tmpExe, $gen -Force

# 2回目: アイコン付きで本ビルド
& $csc /nologo /target:winexe /optimize+ "/win32icon:$ico" "/out:$exe" @refs $src
if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
# WebView2 の部品（管理画面ウィンドウ用）を exe の隣に置く
Copy-Item (Join-Path $root 'lib\webview2\*.dll') $out -Force
Write-Host "Built: $exe"
