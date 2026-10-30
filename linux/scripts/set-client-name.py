#!/usr/bin/env python3
# AdGuard Home の「永続的クライアント」を API で登録・名前変更する。
# 識別子（ClientID・IP・MAC など）に表示名を付けると、クエリログや統計にその名前で出る。
# プロファイルや Funnel の設定は変えない。
#
# 例: プロファイルの ClientID が iphone の端末を「わたしの iPhone」と表示する
#   set-client-name.py --id iphone --name "わたしの iPhone"
# 管理画面のログイン情報は、トレイに保存したものを使う。なければ実行時に聞く（ファイルや引数に書かない）。
import argparse
import getpass
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.realpath(__file__))))

from adguard_home_tray import api  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description="Add or rename a persistent client in AdGuard Home")
    ap.add_argument("--id", required=True, help="識別子（例: iphone、192.168.1.20、aa:bb:cc:dd:ee:ff）")
    ap.add_argument("--name", required=True, help="表示名")
    ap.add_argument("--base-url", default="http://127.0.0.1/", help="管理画面の URL")
    a = ap.parse_args()

    c = api.AghApi()
    c.base_url = a.base_url
    if not c.has_credentials:
        user = input("AdGuard Home のユーザー名: ")
        c.set_credentials(user, getpass.getpass("パスワード: "))

    clients = (c.get("control/clients") or {}).get("clients") or []
    existing = next((x for x in clients if a.id in (x.get("ids") or [])), None)
    if existing:
        # 既存の設定はそのままに、名前だけ変える
        data = dict(existing, name=a.name)
        c.post("control/clients/update", {"name": existing["name"], "data": data})
        print(f"変更しました: {existing['name']} → {a.name}（識別子 {a.id}）")
    else:
        c.post("control/clients/add", {
            "name": a.name, "ids": [a.id], "tags": [], "upstreams": [],
            "use_global_settings": True, "use_global_blocked_services": True,
            "filtering_enabled": True, "parental_enabled": False, "safebrowsing_enabled": False,
        })
        print(f"登録しました: {a.name}（識別子 {a.id}、フィルターなどは全体の設定を使用）")


if __name__ == "__main__":
    main()
