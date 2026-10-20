AdGuard Home を Windows サービスとして動かし、通知領域のアイコンから管理するためのセットアップ一式です（AdGuard 公式のものではありません）。

## 入れ方

PowerShell に次を貼り付けて Enter（管理者でなくてよい）。最新版を SHA-256 で照合してから、セットアップ画面を開きます。

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows-tray/main/get.ps1 | iex
```

または、下の `AdGuardHomeTray.zip` を展開して `Setup.cmd` をダブルクリック。AI エージェントに頼む方法は [README](https://github.com/hinatamaxxx/adguard-home-windows-tray#かんたん導入) を見てください。

## 主な機能

- AdGuard Home を Windows サービスとして登録（PC 起動時に自動開始、コンソール画面なし）
- 通知領域のアイコンとメニュー（Windows 標準のメニュー、ダークモード対応）: 状態の表示、保護の一時停止、サービスの開始・停止・再起動、接続用アドレスのコピー
- 管理画面を専用ウィンドウで表示し、保存したログイン情報で自動ログイン
- **自動更新**: AdGuard Home 本体とこのアプリの両方を毎日更新（UAC の確認なし、既定でオン）。成功は通知せず、続けて失敗したときだけ通知。「アップデート」→「今すぐ確認して更新」で手動でも更新できる
- Windows の「設定 → アプリ → スタートアップ」で自動起動をオン/オフ
- 外出先の iPhone / iPad からも使える（Tailscale Funnel ＋ DNS 構成プロファイル）

## 添付ファイル

- `AdGuardHomeTray.zip`: ビルド済みのセットアップ一式
- `AdGuardHomeTray.zip.sha256`: 上の ZIP の SHA-256（自動更新と `get.ps1` が照合に使う）

AdGuard Home 本体は含みません。セットアップ時に AdGuard 公式の配布元から取得し、署名を確認してから入れます。不具合の報告やサポートは受け付けていません（[LICENSE](https://github.com/hinatamaxxx/adguard-home-windows-tray/blob/main/LICENSE) のとおり現状のまま提供）。
