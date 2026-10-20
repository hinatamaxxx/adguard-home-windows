# セキュリティ

脆弱性や、鍵・個人情報が漏れるおそれのある問題を見つけた場合は、公開の Issue には書かず、GitHub の「Security」タブ →「Report a vulnerability」（Private vulnerability reporting）から知らせてください。

AdGuard Home 本体の脆弱性は、AdGuard の窓口（https://github.com/AdguardTeam/AdGuardHome/security）へ報告してください。

## このプロジェクトで気をつけていること

- AdGuard Home は公式の配布元から取得し、AdGuard の署名を確認してから入れます。
- 管理画面のログイン情報は、Windows DPAPI でユーザー単位に暗号化して保存します。
- 外出先向けに公開するのは、推測できない鍵付きの DNS-over-HTTPS の入口（Tailscale Funnel）だけです。鍵入りの iOS プロファイルはリポジトリに含めないでください（`.gitignore` で除外しています）。
