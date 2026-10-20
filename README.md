# AdGuard Home Tray for Windows

> **English summary:** Run the official AdGuard Home as a Windows service and manage it from a tray icon (status, pause protection, updates, a dedicated dashboard window). Includes a one-click setup, automatic updates, and a script to use it from iOS outside your home via Tailscale Funnel. Install with one PowerShell command (`irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows-tray/main/get.ps1 | iex`), or give this repository URL to an AI coding agent (Claude Code / Codex) and ask it to set it up — see [AGENTS.md](AGENTS.md). Documentation is in Japanese. Not affiliated with AdGuard. Provided as-is; issues, bug reports and support requests are not accepted.

公式の [AdGuard Home](https://github.com/AdguardTeam/AdGuardHome) を Windows サービスとして動かし、通知領域（タスクトレイ）のアイコンから状態の確認・保護の一時停止・更新・管理画面の表示ができるようにするセットアップ一式です。コンソール画面を出しっぱなしにする必要はなく、PC の起動時に自動で動きます。

## かんたん導入

**コマンド 1 行で入れる**（AdGuard Home 本体とこのアプリ一式）

PowerShell（スタートボタンを右クリック →「ターミナル」）に次を貼り付けて Enter を押します。管理者として開く必要はありません。

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows-tray/main/get.ps1 | iex
```

最新のリリースをダウンロードして SHA-256 を照合し、セットアップ画面を開きます（中身は [get.ps1](get.ps1)）。「インストール」を押し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を選んでください。終わったら、通知領域のアイコンをダブルクリックして、AdGuard Home の初期設定（管理者のユーザー名・パスワードを決める）を行います。

**AI に頼む**

Claude Code や Codex などの AI エージェントに、次のように頼んでください。導入手順は [AGENTS.md](AGENTS.md) にまとめてあり、AI はそれに沿って進めます。管理者権限の確認（UAC）やパスワードの設定など、本人が行う操作は AI が案内します。

```
https://github.com/hinatamaxxx/adguard-home-windows-tray を導入して。
```

必要に応じて、次のことも一緒に伝えると、その設定まで案内します。

- 家のほかの機器（スマホ・タブレット・ゲーム機など）でも使いたい
- 外出先の iPhone / iPad でも使いたい（Tailscale を使います）

**ZIP から入れる**

1. [Releases](https://github.com/hinatamaxxx/adguard-home-windows-tray/releases/latest) からビルド済みの `AdGuardHomeTray.zip` をダウンロードして展開する（ソースから入れる場合は ZIP / `git clone` でも可。その場合は初回に自動でビルドします）
2. `Setup.cmd` をダブルクリックする
3. 「インストール」を押し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」
4. 通知領域のアイコンをダブルクリックして、AdGuard Home の初期設定（管理者のユーザー名・パスワードを決める）を行う

家のほかの機器や、外出先の機器から使う設定は [AGENTS.md](AGENTS.md) の 2・3 を参照してください。

> AdGuard Home 本体はこのリポジトリに含みません。セットアップ時に AdGuard 公式の配布元からダウンロードし、AdGuard の署名を確認してから入れます。このプロジェクトは AdGuard 公式のものではありません。

## スクリーンショット

**通知領域のメニュー**（Windows 標準のメニュー。ダークモードに追従）

![右クリックメニュー](screenshots/tray-menu.png)

| 保護の一時停止とサービス操作 | アップデート |
|---|---|
| ![保護とサービス](screenshots/tray-menu-protection.png) | ![アップデート](screenshots/tray-menu-update.png) |

| その他 | 接続用アドレス（DNS サーバー） |
|---|---|
| ![その他](screenshots/tray-menu-more.png) | ![接続用アドレス](screenshots/tray-menu-addresses.png) |

| 設定ページを開く |
|---|
| ![設定ページ](screenshots/tray-menu-pages.png) |

※ 接続用アドレスの画像は、例示用のアドレスを表示する撮影専用のビルドで撮影しています（配布するアプリにはこの機能は含まれません）。

| ツールチップ | 状態別のアイコン（AdGuard Home 導入後は、盾の部分が AdGuard Home のロゴになります） |
|---|---|
| ![ツールチップ](screenshots/tray-tooltip.png) | ![アイコン](screenshots/icons.png) |

**管理画面ウィンドウ**（専用ウィンドウ。Windows のライト/ダークに追従し、保存したログイン情報で自動ログイン）

![管理画面ウィンドウ](screenshots/dashboard.png)

| セットアップ | アンインストール |
|---|---|
| ![セットアップ](screenshots/setup.png) | ![アンインストール](screenshots/dialog-uninstall.png) |

| 停止の確認 | 終了の確認 |
|---|---|
| ![停止の確認](screenshots/dialog-stop.png) | ![終了の確認](screenshots/dialog-exit.png) |

## 主な機能

- AdGuard Home を Windows サービスとして登録（PC 起動時に自動開始、異常終了時は自動で再起動、コンソール画面なし）
- 通知領域のアイコンとメニュー（Windows 標準のメニュー。ダークモードに追従）: 状態表示、管理画面を開く、保護の一時停止（1 分〜8 時間・再開まで）、サービスの開始/停止/再起動、アップデート
- 管理画面を専用ウィンドウ（WebView2）で表示。ブラウザー風の部品は出さず、ログイン画面も自動で通過
- 自動更新: AdGuard Home 本体とこのトレイアプリの両方（毎日・SYSTEM 権限のタスク。UAC の確認なし。既定でオン）
- ログオン時の自動起動は Windows の「設定 → アプリ → スタートアップ」からもオン/オフできる
- レジストリは AdGuard Home のサービス登録以外に書かない。アンインストールは既定で設定・データもすべて削除（「設定とデータを残す」を選べる）
- 外出先の iOS / iPadOS からも使える（Tailscale Funnel ＋ DNS 構成プロファイル。下の「iOS」を参照）。Tailscale アプリを入れた機器なら、Tailscale の DNS 設定だけで使える

## PC のスリープについて

AdGuard Home はこの PC で動くので、**PC がスリープ・電源オフの間は、DNS をこの PC に向けた機器（Tailscale でつないだ機器や、DNS プロファイルを入れた iPhone・iPad を含む）がインターネットにつながらなくなります。** 外の機器でも使う場合は、Windows の設定でスリープしないようにしてください。

- 「設定」→「システム」→「電源とバッテリー」→「画面、スリープ、休止状態のタイムアウト」で、「電源接続時に、次の時間が経過した後にデバイスをスリープ状態にする」を「なし」にする（ノート PC はバッテリー駆動時も必要に応じて）
- 画面の電源は切れても問題ありません

## 動作環境

- Windows 10 / 11（x64）、.NET Framework 4.8（Windows に標準で入っています）
- Microsoft Edge WebView2 ランタイム（Windows 11 は標準で入っています。ない場合、管理画面は既定のブラウザーで開きます）
- 外出先から使う場合は [Tailscale](https://tailscale.com/)
## インストール・更新

1. `Setup.cmd` をダブルクリックする（`build\AdGuardHomeTray.exe` がなければ先に `build.ps1` でビルドする）。セットアップ画面で入るものを確認し、「インストール」（導入済みなら「更新」）を押す
2. 「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を選ぶ。完了画面が出たら終わり

セットアップ画面では「Windows の起動時にトレイアプリを自動で起動する」（新規は既定でオフ、更新時は今の状態）と「完了したらトレイアプリを起動する」を選べます。`install.ps1` を管理者の PowerShell で直接実行しても同じようにインストールできます（画面なし）。

## 構成

| 場所 | 内容 |
|---|---|
| `C:\Program Files\AdGuardHome\` | AdGuard Home 本体・設定・データ（本体の権限チェックにより管理者専用） |
| `C:\Program Files\AdGuardHome Tray\` | トレイアプリ `AdGuardHomeTray.exe`、WebView2 の部品（`Microsoft.Web.WebView2.*.dll`、`WebView2Loader.dll`）、`manage.ps1`、`uninstall.ps1` |
| `C:\ProgramData\AdGuardHome\settings.json` | トレイと `manage.ps1` の共有設定（インストール先・自動更新のオン/オフ・バージョン・管理画面 URL）。SYSTEM・管理者が書き、ユーザーは読み取りのみ |
| `C:\ProgramData\AdGuardHome\logs\` | `AdGuardHome.log`（本体）と `manage.log`（操作・更新の記録） |
| `%APPDATA%\AdGuardHomeTray\credentials.dat` | 管理画面のログイン情報（Windows DPAPI でユーザー単位に暗号化） |
| サービス `AdGuardHome` | PC 起動時に自動開始。コンソール画面は出ない。異常終了したら自動で再起動 |
| タスク `AdGuardHome AutoUpdate` | 起動時・ログオン時・毎日 4:30 頃に SYSTEM で実行。バージョン情報を記録し、自動更新が有効なら AdGuard Home とトレイアプリを最新版へ更新 |
| タスク `AdGuardHome UpdateNow` | トレイの「今すぐ確認して更新」で起動（トリガーなし）。ログオンした利用者も実行できるので UAC の確認は出ない |
| `スタートアップ\AdGuard Home.lnk` | ログオン時にトレイアプリを起動（利用者ごとのスタートアップフォルダー）。オン/オフは Windows の「設定 → アプリ → スタートアップ」とトレイメニューのどちらでも切り替えられる |

## トレイアイコン

- 緑の盾＋チェック: 実行中 / 橙: 新しい版あり（自動更新をオフにしているときだけ） / 灰＋×: 停止中 / 赤＋×: サービス未登録
- ダブルクリック: 管理画面を専用ウィンドウで開く（WebView2。Windows のライト/ダークに追従、保存済みのログイン情報で自動ログイン）。スタートメニューの「AdGuard Home」からも開ける（トレイが動いていなければ起動してから開く）
- 専用ウィンドウでは F5 / Ctrl+R で再読み込み、Alt+← で戻る。管理画面以外へのリンクは既定のブラウザーで開く
- 右クリックメニュー（Windows のダークモードに追従）は、状態（バージョン）・保護・管理画面を開く・サービス・アップデート・その他・終了だけ。「アプリを終了する」は常にメニューの最後に表示し、AdGuard Home の停止中だけ押せる（実行中はグレー表示。マウスを乗せると理由を表示）。押すと、終了で使えなくなる機能と、もう一度表示する方法を書いた確認画面が出て、「終了する」で終了する（最初のフォーカスは「キャンセル」）。接続用アドレス、設定ページ、ブラウザーで開く、ログイン情報、自動起動は「その他」にまとめている（ログを開く項目と統計表示は 2026-10-19 に削除）。以前の項目: 管理画面、管理画面をブラウザーで開く、サービスの開始・停止・再起動、更新確認、今すぐ更新、自動更新のオン・オフ、ログオン時起動のオン・オフ、ログを開く
- 「ログイン情報を設定…」で管理画面のユーザー名とパスワードを保存すると（Windows DPAPI で暗号化して `%APPDATA%\AdGuardHomeTray\credentials.dat`）、次の項目が表示されます
  - 保護の状態と一時停止（1分・10分・1時間・8時間・再開まで）。保護オフ中はアイコンが灰色
  - 統計（クエリ数・ブロック数）
- 「接続用アドレス（DNS サーバー）」: 端末の DNS サーバー欄に入れるアドレスを、このPC用・LAN 用・Tailscale 用に分けて表示（クリックでコピー、ログイン不要）。ファイアウォールの受信許可がない場合は警告を表示
- 「設定ページを開く」から、DNS 設定・ブロックリスト・クエリログなどの各ページを直接開けます
- サービスの開始・停止・再起動と、自動更新のオン/オフの切り替えは UAC で管理者権限を求めます（更新そのものは UAC なし。下の「更新のしくみ」）
- 「AdGuard Home が停止しました」の通知は、思いがけず止まったときだけ出します。更新や再起動でサービスを止めている間（`manage.ps1` が `C:\ProgramData\AdGuardHome\maintenance.json` を置いている間）や、トレイの「停止」で止めたときは出しません。一瞬止まってすぐ戻る場合に出さないよう、停止が 10 秒以上続いてから知らせます
- 更新や再起動が途中で失敗しても、`manage.ps1` がサービスを起動し直してから終えるので、DNS が止まったままになりません。自動更新後のバージョン表示は、トレイが 1 分ごとに読み直します
- 管理画面ウィンドウで手入力してログインすると、その情報をトレイにも保存するか確認します（保存済みと違う場合は更新するか確認）

## 更新のしくみ

「アップデート」メニューには、何の版かを名前で表示します（AdGuard Home 本体と、このアプリ）。

```
アップデート ▸
  AdGuard Home v0.107.79（最新です）
  このアプリ v1.7.3（最新です）
  ─────────────
  今すぐ確認して更新
  ✓ 自動で更新する（毎日）
```

- **自動で更新する（既定でオン）**: SYSTEM 権限のタスクが毎日（と起動時・ログオン時）、AdGuard Home は公式のアップデーターで、このアプリは GitHub Releases の最新版で更新します。UAC の確認は出ません。成功しても通知はしません（メニューの版の表示で分かります）。続けて 2 回失敗したときだけ通知します
- **今すぐ確認して更新**: 同じ処理を今すぐ実行します（UAC なし）。押したときは結果を通知します
- 自動更新をオフにした場合だけ、新しい版が出たときに通知し、アイコンを橙にします（通知をクリックすると更新）
- このアプリの更新は、このリポジトリのリリースにある `AdGuardHomeTray.zip` を、同じリリースの `AdGuardHomeTray.zip.sha256` と照合してから入れ替えます。動いているトレイは止めず、新しい版に入れ替わったことに気づくと自分で再起動します（管理画面などを開いているときは、閉じてから）
- 記録は `C:\ProgramData\AdGuardHome\logs\manage.log` にあります

## ファイル

- `get.ps1`: コマンド 1 行での導入用（`irm … | iex` で読み込むため BOM なしの UTF-8）。最新リリースの ZIP を SHA-256 で照合してセットアップ画面を開く
- `src/AdGuardHomeTray.cs`: トレイアプリ（.NET Framework 4.x の `csc.exe` でビルド）
- `src/DashboardForm.cs`: 管理画面の専用ウィンドウ（WebView2） / `src/Theme.cs`: ライト/ダーク対応
- `src/DialogKit.cs`: 確認画面の共通部品（Windows 11 風。角丸ボタン・ボタン帯・ライト/ダーク） / `src/SetupDialog.cs`・`src/UninstallDialog.cs`・`src/ExitDialog.cs`: セットアップ・アンインストール・終了の画面
- `src/TrayContext.MenuHover.cs`: メニュー項目にマウスを乗せたときの説明表示
- `src/TrayContext.Update.cs`: 版の表示・今すぐ更新・自動更新の失敗通知・更新後の自動再起動
- `src/TrayContext.SubmenuUp.cs`: 子メニューを親の項目より上に向かって開く
- `lib/webview2/`: WebView2 SDK（Microsoft.Web.WebView2 1.0.4258.31、nuget.org。Microsoft の署名を確認済み）
- `build.ps1`: ビルド（`build\AdGuardHomeTray.exe` を生成）
- `scripts/manage.ps1`: サービス操作・更新（管理者で実行）
- `scripts/configure-adguard.ps1`: AdGuard Home の設定変更（`-EnableFunnelDoH` で Funnel 経由の DoH を受ける、`-Faster` で高速化。検証してから適用、バックアップあり）
- `scripts/new-ios-dns-profile.ps1` / `scripts/set-client-name.ps1`: iOS 用 DNS プロファイルの作成 / 端末名の登録
- `AGENTS.md`（`CLAUDE.md` から読み込み）: AI エージェント向けの導入手順
- `Setup.cmd`: セットアップ画面を開く（`build\AdGuardHomeTray.exe --setup`）
- `install.ps1`: インストール本体（管理者で実行。セットアップ画面から UAC で呼ばれる）
- `uninstall.ps1`: アンインストール（Windows の「設定 → アプリ → インストールされているアプリ」の「AdGuard Home（トレイアプリ）」から実行できる。自動で管理者に昇格。既定で設定・データもすべて削除し、確認画面の「設定とデータを残す」にチェックを入れたときだけ残す。無人実行は `-Quiet`、残す場合は `-KeepSettings`）

## 変更するレジストリとファイルの一覧

AdGuard Home 本体が Windows サービスのため、PC 全体（`C:\Program Files`）へのインストールです。設定・ログイン情報・自動起動はファイルで扱い、レジストリに書くのはサービス登録と「インストールされているアプリ」への登録だけです。

### レジストリ

| 場所 | 作るもの | アンインストール |
|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Services\AdGuardHome` | サービス登録（`AdGuardHome.exe -s install` が作成） | 削除 |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AdGuardHomeTray` | 「インストールされているアプリ」への登録（名前・版・アンインストール方法） | 削除 |

Windows の「スタートアップ」でオン/オフを切り替えると、Windows 自身が `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder` の値 `AdGuard Home Tray.lnk` を書きます。トレイはこれを読むだけで書かず、Windows が管理する値なのでアンインストールでも消しません。

旧版が書いていた次の値は、ファイルまたはショートカットへ移してから削除します。アンインストール時にも、残っていれば削除します。

| 旧版の場所 | 移行先 | 移行するタイミング |
|---|---|---|
| `HKLM\SOFTWARE\AdGuardHomeTray`（共有設定） | `C:\ProgramData\AdGuardHome\settings.json` | インストール時 |
| `HKCU\Software\AdGuardHomeTray`（ログイン情報） | `%APPDATA%\AdGuardHomeTray\credentials.dat` | トレイの初回起動時 |
| `HKCU\...\CurrentVersion\Run` と `HKCU\...\Explorer\StartupApproved\Run` の値 `AdGuardHomeTray`（自動起動） | スタートアップのショートカット（オンだった場合だけ） | トレイの初回起動時 |

### ファイル・フォルダー・その他

| 場所 | 内容 | アンインストール |
|---|---|---|
| `C:\Program Files\AdGuardHome\AdGuardHome.exe` | AdGuard Home 本体 | 削除 |
| `C:\Program Files\AdGuardHome\AdGuardHome.yaml`、`data\` | AdGuard Home の設定・データ（本体が作成） | 削除（「設定とデータを残す」を選んだときは残す） |
| `C:\Program Files\AdGuardHome Tray\` | トレイアプリ、`manage.ps1`、`uninstall.ps1` | 削除 |
| `C:\ProgramData\AdGuardHome\`（`settings.json`、`logs\`、更新中だけ置く `maintenance.json`） | 共有設定・ログ | 削除 |
| `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\AdGuard Home.lnk` | スタートメニューの「AdGuard Home」（管理画面を開く） | 削除 |
| タスク スケジューラ `AdGuardHome AutoUpdate`、`AdGuardHome UpdateNow` | 情報更新・自動更新・今すぐ更新 | 削除 |
| `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\AdGuard Home.lnk` | ログオン時の自動起動（トレイメニューでオンにしたときだけ） | 削除 |
| `%APPDATA%\AdGuardHomeTray\` | トレイのログイン情報（利用者ごと） | 削除（「設定とデータを残す」を選んだときは残す） |
| `%LOCALAPPDATA%\AdGuardHomeTray\` | 管理画面ウィンドウのデータ（WebView2 のキャッシュ・ログイン状態、`window.txt` にウィンドウ位置） | 削除（「設定とデータを残す」を選んだときは残す） |

## iOS / iPadOS（Tailscale アプリなし）

- Tailscale Funnel で `https://<PC名>.<tailnet>.ts.net/dns-query/<鍵>` だけをインターネットに公開し、AdGuard Home の `/dns-query/<ClientID>` へ中継する。鍵付きパス以外は Tailscale が 404 を返し、AdGuard Home には届かない。
- TLS は Funnel が終端するため、AdGuard Home は `http.doh.insecure_enabled: true`。
- iOS 用プロファイルは `scripts/new-ios-dns-profile.ps1` で作る（例: `-ClientId my-iphone -DisplayName "My iPhone"`。鍵の生成と Funnel への追加も行う）。できたプロファイルは鍵入りなので、他人に渡したりリポジトリに入れたりしない（`.gitignore` で `*.mobileconfig` を除外）。
- 端末名を付けるには、管理画面の「設定 → クライアント設定」で永続的クライアント（識別子に ClientID）を追加するか、`scripts/set-client-name.ps1 -Id my-iphone -Name "My iPhone"` を使う。
- 鍵を変えるとき: スクリプトで新しいプロファイルを作り、古い鍵は `tailscale funnel --https=443 --set-path /dns-query/<古い鍵> off` で無効にする。
- 公開をやめるとき: `tailscale funnel --https=443 off`。
- AdGuard Home が止まっているとき（停止中、PC のスリープ・電源オフ）は、DNS をこの PC に向けた機器（プロファイルを入れた iPhone・iPad を含む）は名前解決ができず、インターネットにつながらなくなる。DNS プロファイルには、応答がないときに別の DNS へ切り替える設定がない。
- そのときは iPhone・iPad の「設定」→「一般」→「VPN とデバイス管理」→「DNS」で「自動」を選ぶと、回線の DNS に戻る。AdGuard Home を再開したら、同じ画面で AdGuard Home のプロファイルを選び直す。
- トレイの「AdGuard Home を停止…」を選ぶと、この注意と戻し方を書いた確認画面が出る。トレイアプリを終了しても AdGuard Home（サービス）は止まらないので、DNS には影響しない。

## Tailscale の設定

- tailnet 全体で AdGuard Home を使う: Tailscale 管理画面 → DNS → Global nameservers に PC の Tailscale IP（`100.x.y.z`）を追加し、Override local DNS をオン
- Funnel を使う場合は、管理画面で HTTPS 証明書を有効にし、初回の `tailscale funnel` 実行時に表示されるリンクから Funnel を有効化する
- Funnel の設定は PowerShell から実行する（Git Bash では `/dns-query/...` のパスが書き換えられてしまう）
- ファイアウォールで AdGuard Home の受信（UDP/TCP 53）を許可する（Tailscale は「プライベート」扱い）

## サポートについて

このプロジェクトは個人用に作ったものを、現状のまま公開しています。不具合や脆弱性の報告、質問、要望は受け付けていません。使う場合は自己責任でお願いします（[LICENSE](LICENSE) の免責のとおり）。

## ライセンスと商標

- このリポジトリのコードは [MIT ライセンス](LICENSE) です（`lib/webview2/` を除く）。
- AdGuard Home は AdGuard Software Ltd. のソフトウェア（GPL-3.0）で、このリポジトリには含みません。セットアップ時に公式の配布元から取得します。
- 「AdGuard」は AdGuard Software Ltd. の商標です。このプロジェクトは AdGuard 公式のものではなく、同社とは関係ありません。
- トレイアイコンの盾のロゴは、リポジトリには含めず、インストールした AdGuard Home の管理画面から取得して使います。取得できない間は、独自に描いた盾のアイコンを表示します。
- `lib/webview2/` は Microsoft の WebView2 SDK です（ライセンスは同フォルダーの `LICENSE.txt`）。画面を描く WebView2 ランタイムは Windows が自動で更新するため、このアプリでは更新しません。SDK はアプリの新しい版を出すときに更新します。

## メモ

- WSL / Hyper-V を使っている PC では、Windows の「インターネット接続の共有」(SharedAccess) が `0.0.0.0:53` を使っていることがあり、初期設定で DNS の待ち受けを「すべてのインターフェース」にすると失敗します。その場合は DNS を `127.0.0.1`（その PC だけで使う場合）か LAN の IP アドレスで待ち受けてください。
