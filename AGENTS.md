# 導入手順（AI エージェント向け）

このリポジトリは Windows で AdGuard Home をサービスとして動かし、通知領域のトレイアプリで管理するためのものです。ユーザーから「このリポジトリを導入して」と頼まれたら、この手順に従ってください。基本の導入（1）のあと、家のほかの機器（2）や外出先の iPhone / iPad（3）でも使いたいかを確かめ、望んだものだけ進めてください。ユーザーへの説明はユーザーの言語で、専門用語を避けて行ってください。

## 守ること

- **管理者権限が必要な操作は UAC で実行し、承認はユーザーに任せる。** UAC を迂回しない。
- **パスワードを決めたり入力したりしない。** AdGuard Home の管理者パスワードは、ユーザーが初期設定画面で自分で決める。
- **ファイアウォール・Tailscale の管理画面・ネットワークの DNS 設定はユーザーが操作する。** コマンドや手順を示し、実行はユーザーに任せる（セキュリティや共有設定の変更のため）。
- **インターネットへの公開（Tailscale Funnel）は、ユーザーが望んだ場合だけ。** 何が公開されるか（鍵付きの DNS 入口だけ）を説明して了承を得る。
- **鍵入りの iOS プロファイル（`*.mobileconfig`）や AdGuard Home の設定ファイルを、リポジトリやチャットに貼らない。**
- `tailscale funnel` のパス指定は **PowerShell から実行する**（Git Bash では `/dns-query/...` が `C:/Program Files/Git/...` に書き換えられる）。
- PowerShell 5.1 で日本語を含むスクリプトを実行するときは、ファイルが UTF-8（BOM 付き）であること。

## 1. 基本の導入（この PC で使う）

1. 前提を確認する: Windows 10/11 x64、`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` があること（.NET Framework 4.8。通常は標準で入っている）。
2. リポジトリを取得する（`git clone <URL>`、または ZIP をダウンロードして展開）。取得とセットアップ画面の起動をまとめて行う場合は、PowerShell で `irm https://raw.githubusercontent.com/hinatamaxxx/adguard-home-windows/main/get.ps1 | iex`（最新リリースの ZIP を SHA-256 で照合してからセットアップ画面を開く。この場合 3 は不要）。
3. `Setup.cmd` を実行する（初回は自動でビルドしてからセットアップ画面を開く）。ユーザーに次のことを伝える。
   - セットアップ画面で「インストール」を押す
   - 「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を押す
   - 画面なしで入れる場合は `build.ps1` のあと、`install.ps1` を管理者で実行してもよい（`Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "<repo>\install.ps1"'`）
4. AdGuard Home の初期設定（ユーザーが行う）: トレイアイコンをダブルクリックすると `http://127.0.0.1:3000/` の初期設定画面が開く。
   - 管理画面のポートは 80 のままでよい。DNS の待ち受けが「ポートが使用中」で失敗したら（WSL / Hyper-V の「インターネット接続の共有」が `0.0.0.0:53` を使っている場合）、`127.0.0.1` か LAN の IP を選ぶ。
   - ユーザー名とパスワードはユーザーが決める。
   - 完了時に「Failed to fetch」と出ても、管理画面が 80 番に移っただけで設定は完了している。`http://127.0.0.1/` を開き直す。
5. トレイの右クリック →「その他」→「ログイン情報を設定…」で、管理画面のユーザー名とパスワードを保存してもらう（保護の一時停止・ツールチップのフィルター更新時刻・管理画面の自動ログインに使う。Windows DPAPI で暗号化して保存）。
6. 確認: `Resolve-DnsName example.com -Server 127.0.0.1` が応答し、`Resolve-DnsName doubleclick.net -Server 127.0.0.1` が `0.0.0.0` を返す。
7. （任意）速くする: 管理者で `scripts\configure-adguard.ps1 -Faster`（レート制限の解除、上流 DNS の並列化、楽観的キャッシュ）。既定のレート制限（1 秒 20 件）は、家の機器と PC 自身が同じ枠を使うため、問い合わせが捨てられて遅くなりやすい。

この PC 自身で使うには、ネットワーク設定の DNS を `127.0.0.1` にする（ユーザーが行う）。

## 2. 家のほかの機器で使う（任意）

1. ファイアウォールで受信を許可する（管理者の PowerShell。ユーザーに実行してもらう）。
   ```powershell
   New-NetFirewallRule -DisplayName "AdGuard Home DNS" -Direction Inbound -Program "C:\Program Files\AdGuardHome\AdGuardHome.exe" -Protocol UDP -LocalPort 53 -Action Allow -Profile Private,Public
   New-NetFirewallRule -DisplayName "AdGuard Home DNS (TCP)" -Direction Inbound -Program "C:\Program Files\AdGuardHome\AdGuardHome.exe" -Protocol TCP -LocalPort 53 -Action Allow -Profile Private,Public
   ```
2. 機器の DNS に PC の LAN の IP を設定する。IP はトレイの「その他」→「接続用アドレス」で確認できる。ルーターで PC の IP を固定できると安心（できない環境もある）。

## 3. 外出先でも使う（Tailscale、任意）

1. PC に Tailscale を入れてログインしてもらう。2. のファイアウォール設定も行う（Tailscale は「プライベート」扱い）。
2. **Tailscale アプリを入れた機器**: Tailscale 管理画面 → DNS → Global nameservers に PC の Tailscale IP（`tailscale ip -4`）を追加し、「Override local DNS」をオン（ユーザーが操作）。各機器で `tailscale dns status` の Resolvers を確認。
3. **Tailscale アプリなしの iOS / iPadOS**（DNS 構成プロファイル。Tailscale Funnel で鍵付きの DNS 入口だけを公開する）:
   1. ユーザーの了承を得たうえで、管理者で `scripts\configure-adguard.ps1 -EnableFunnelDoH` を実行（Funnel が TLS を終端し、PC 内では HTTP で受けるため）。
   2. Tailscale 管理画面で HTTPS 証明書を有効にしてもらう。
   3. PowerShell で `scripts\new-ios-dns-profile.ps1 -ClientId <英小文字の端末名> -DisplayName "<表示名>" -OutDir <出力先>` を実行。初回は Funnel が無効だと有効化用のリンクが出るので、ユーザーに開いて有効にしてもらい、もう一度実行する。
   4. できた `.mobileconfig` を iCloud Drive や AirDrop で iPhone / iPad に送り、「設定」→「一般」→「VPN とデバイス管理」でインストールしてもらう。
   5. 確認: 別の回線から鍵付き URL に DoH で問い合わせると 200、鍵なしや別のパスは 404。iPhone の Safari で `doubleclick.net` が開けない。
   6. 端末名を付ける: 管理画面の「設定 → クライアント設定」で永続的クライアント（識別子は ClientId）を追加するか、`scripts\set-client-name.ps1 -Id <ClientId> -Name "<表示名>"`。
4. 注意をユーザーに伝える: PC がスリープ・電源オフ・AdGuard Home 停止中は、DNS をこの PC に向けた機器はインターネットにつながらない。PC のスリープを切るのがおすすめ。

## トラブル対応

| 症状 | 対処 |
|---|---|
| iPhone でプロファイルを入れたら全く通信できない | 別の VPN（AltStore 用の LocalDevVPN など）がデバイス VPN として有効。「設定 → 一般 → VPNとデバイス管理 → VPN」で切り替える。AdGuard アプリの DNS/VPN もオンだと優先される |
| 「暗号化された DNS のトラフィックをブロックしています」 | その Wi-Fi で一度失敗した記録。Wi-Fi のオフ/オン、またはネットワーク設定を削除して再接続 |
| 「バージョンが対応していません」 | プロファイルの `PayloadVersion` は必ず 1 |
| Funnel 公開直後に TLS エラー | 証明書の発行中。1 分ほど待つ |
| 表示が遅い | `configure-adguard.ps1 -Faster`。Funnel 経由は中継のぶん 1 回 100〜250 ms かかる |

## 更新

導入後は、AdGuard Home 本体とトレイアプリの両方が毎日自動で更新される（SYSTEM のタスク。UAC なし、既定でオン）。手動で更新したいときは、トレイの右クリック →「アップデート」→「今すぐ確認して更新」。

## アンインストール

Windows の「設定 → アプリ → インストールされているアプリ」で「AdGuard Home（トレイアプリ）」をアンインストール。既定で設定・データもすべて削除する（確認画面で「設定とデータを残す」を選べる）。DNS をこの PC に向けた機器は、先に元に戻すようユーザーに伝える。
