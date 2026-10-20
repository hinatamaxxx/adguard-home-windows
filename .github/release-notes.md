AdGuard Home を Windows サービスとして動かし、通知領域のアイコンから管理するためのセットアップ一式です（AdGuard 公式のものではありません）。

## 入れ方

PowerShell に次を貼り付けて Enter を押します（管理者でなくてかまいません）。最新版を SHA-256 で照合してから、セットアップ画面を開きます。

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/get.ps1 | iex
```

`AdGuardHomeTray.zip` を展開して `Setup.cmd` をダブルクリックする方法もあります。AI エージェントに頼む方法は [README](https://github.com/hinatamaxxx/adguard-home-windows#かんたん導入) を見てください。

## 主な機能

- AdGuard Home を Windows サービスとして登録（PC の起動時に自動で開始し、コンソール画面は出ない）
- 通知領域のアイコンとメニュー（Windows 標準のメニュー、ダークモード対応）: 状態の表示、保護の一時停止、サービスの開始・停止・再起動、接続用アドレスのコピー
- 管理画面を専用のウィンドウで表示し、保存したログイン情報で自動ログイン
- 自動更新: AdGuard Home 本体とこのアプリの両方を毎日更新（UAC の確認なし、既定でオン）。成功は通知せず、続けて失敗したときだけ通知する。「アップデート」→「今すぐ確認して更新」で手動でも更新できる
- Windows の「設定 → アプリ → スタートアップ」で自動起動をオン/オフできる
- 表示は日本語と英語に対応（Windows の表示言語に合わせる）
- 外出先の iPhone / iPad からも使える（Tailscale Funnel ＋ DNS 構成プロファイル）

## 添付ファイル

- `AdGuardHomeTray.zip`: ビルド済みのセットアップ一式
- `AdGuardHomeTray.zip.sha256`: 上の ZIP の SHA-256（自動更新と `get.ps1` が照合に使う）

AdGuard Home 本体は含みません。セットアップ時に AdGuard 公式の配布元から取得し、署名を確認してから入れます。不具合の報告やサポートは受け付けていません（[LICENSE](https://github.com/hinatamaxxx/adguard-home-windows/blob/main/LICENSE) のとおり、現状のまま提供します）。

---

## English

A setup kit that runs AdGuard Home as a Windows service and lets you manage it from a notification area icon (not affiliated with AdGuard).

**Install:** paste this into PowerShell. It checks the SHA-256 of the latest version and opens the setup window:

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/get.ps1 | iex
```

See the [English README](https://github.com/hinatamaxxx/adguard-home-windows/blob/main/README.en.md) for details. AdGuard Home itself is not included; the setup downloads it from the official AdGuard servers and checks its signature. Provided as-is: bug reports and support requests are not accepted.
