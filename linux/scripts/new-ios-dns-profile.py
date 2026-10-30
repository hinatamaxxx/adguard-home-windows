#!/usr/bin/env python3
# 自宅 PC の AdGuard Home を、外出先の iPhone / iPad から暗号化 DNS（DoH）で使うための構成プロファイルを作る。
#   1) 推測できない鍵（ランダムな文字列）を作る
#   2) Tailscale Funnel で https://<PC>.<tailnet>.ts.net/dns-query/<鍵> だけを公開し、
#      AdGuard Home の /dns-query/<ClientID> へ中継する（鍵付きパス以外は Tailscale が 404 で拒否）
#   3) その URL を入れた .mobileconfig を作る
# 鍵は合鍵なので、出力したプロファイルを他人に渡さない。このスクリプト自体には秘密を書かない。
#
# 使い方（Tailscale にログイン済み・Funnel 有効化済みの状態で）:
#   new-ios-dns-profile.py --client-id my-iphone --display-name "My iPhone" --out-dir ~/Documents
#   --no-funnel を付けると Funnel を変更せず、プロファイルだけ作る（URL の確認用）
# Funnel の変更に root が要る環境では、一度 `sudo tailscale set --operator=$USER` を実行しておく。
import argparse
import json
import os
import plistlib
import re
import secrets
import subprocess
import sys
import uuid

CHARS = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"   # 紛らわしい文字を除く


def main():
    ap = argparse.ArgumentParser(description="Make an iOS DNS profile (DoH through Tailscale Funnel)")
    ap.add_argument("--client-id", required=True, help="AdGuard Home に表示される端末名（英小文字・数字・-）")
    ap.add_argument("--display-name", default="AdGuard Home", help="iPhone の「DNS」に表示される名前")
    ap.add_argument("--out-dir", default=os.getcwd())
    ap.add_argument("--server-name", help="例: my-pc.tailxxxx.ts.net（省略時は tailscale status から取得）")
    ap.add_argument("--adguard-port", type=int, default=80, help="AdGuard Home の管理画面（HTTP）のポート")
    ap.add_argument("--no-funnel", action="store_true")
    a = ap.parse_args()
    if not re.fullmatch(r"[a-z0-9-]{1,63}", a.client_id):
        sys.exit("--client-id は英小文字・数字・- で 63 文字までにしてください。")

    server = a.server_name
    if not server:
        p = subprocess.run(["tailscale", "status", "--json"], capture_output=True, text=True)
        if p.returncode != 0:
            sys.exit("Tailscale の状態を取得できません。--server-name を指定してください。")
        server = json.loads(p.stdout)["Self"]["DNSName"].rstrip(".")

    # 1) 鍵（24 文字）
    secret = "".join(secrets.choice(CHARS) for _ in range(24))
    url = f"https://{server}/dns-query/{secret}"

    # 2) Funnel に鍵付きパスを追加（既存の公開設定は残る）
    if not a.no_funnel:
        p = subprocess.run(["tailscale", "funnel", "--bg", "--set-path", f"/dns-query/{secret}",
                            f"http://127.0.0.1:{a.adguard_port}/dns-query/{a.client_id}"],
                           capture_output=True, text=True)
        if p.returncode != 0:
            sys.exit("tailscale funnel に失敗しました（Funnel が有効か、`sudo tailscale set --operator=$USER` 済みか確認してください）\n"
                     + (p.stderr or p.stdout))

    # 3) プロファイル（端末ごとに識別子を分けるので、複数台に別々の鍵を配れる）
    u1, u2 = str(uuid.uuid4()).upper(), str(uuid.uuid4()).upper()
    profile = {
        "PayloadContent": [{
            "DNSSettings": {"DNSProtocol": "HTTPS", "ServerURL": url},
            "PayloadDisplayName": a.display_name,
            "PayloadIdentifier": f"local.adguardhome.dns.{a.client_id}.{u1}",
            "PayloadType": "com.apple.dnsSettings.managed",
            "PayloadUUID": u1,
            "PayloadVersion": 1,
        }],
        "PayloadDescription": "自宅PCの AdGuard Home を暗号化DNS（DNS-over-HTTPS）で使います。",
        "PayloadDisplayName": a.display_name,
        "PayloadIdentifier": f"local.adguardhome.profile.{a.client_id}",
        "PayloadRemovalDisallowed": False,
        "PayloadType": "Configuration",
        "PayloadUUID": u2,
        "PayloadVersion": 1,
    }
    os.makedirs(a.out_dir, exist_ok=True)
    out = os.path.join(a.out_dir, f"AdGuardHome-DNS-{a.client_id}.mobileconfig")
    fd = os.open(out, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as f:
        plistlib.dump(profile, f)

    print(f"Profile : {out}")
    print(f"ClientId: {a.client_id}")
    print(f"Server  : {server}")
    print("Funnel  : " + ("変更なし（--no-funnel）" if a.no_funnel
                          else f"/dns-query/<鍵> → http://127.0.0.1:{a.adguard_port}/dns-query/{a.client_id}"))


if __name__ == "__main__":
    main()
