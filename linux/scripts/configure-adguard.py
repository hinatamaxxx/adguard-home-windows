#!/usr/bin/env python3
# AdGuard Home の設定ファイル（/opt/AdGuardHome/AdGuardHome.yaml）を、外出先から使うためや高速化のために変更する（sudo で実行）。
# 変更前にバックアップを取り、AdGuard Home 自身の --check-config で検証してから適用する。
#   --enable-funnel-doh  Tailscale Funnel 経由の DNS-over-HTTPS を受ける（http.doh.insecure_enabled: true）。
#                        TLS は Funnel が終端し、PC 内では HTTP で受けるため。iOS の DNS プロファイルを使う場合に必要
#   --faster             レート制限をなくし、上流 DNS を Cloudflare / Google / Quad9 への並列問い合わせ、
#                        楽観的キャッシュ、キャッシュ 32MB にする
# 例: sudo ~/.local/share/adguard-home-tray/scripts/configure-adguard.py --enable-funnel-doh --faster
import argparse
import os
import re
import shutil
import subprocess
import sys
import time


def set_scalar(text, key, value):
    # key の先頭の空白は字下げ（例: "  ratelimit" は dns: の直下）。Windows 版と同じ書き換え方
    pattern = re.compile(rf"(?m)^([ \t]*{re.escape(key)}:)[ \t]*\S.*$")
    if not pattern.search(text):
        raise SystemExit(f"{key.strip()} が設定ファイルに見つかりません")
    return pattern.sub(lambda m: f"{m.group(1)} {value}", text, count=1)


def set_list(text, key, items):
    pattern = re.compile(rf"(?m)^  {re.escape(key)}:[ \t]*(\[\])?[ \t]*\n(?:    - .*\n)*")
    if not pattern.search(text):
        raise SystemExit(f"{key} が設定ファイルに見つかりません")
    body = "".join(f"    - {i}\n" for i in items)
    return pattern.sub(lambda m: f"  {key}:\n{body}", text, count=1)


def main():
    ap = argparse.ArgumentParser(description="Change AdGuardHome.yaml for remote use or speed")
    ap.add_argument("--enable-funnel-doh", action="store_true")
    ap.add_argument("--faster", action="store_true")
    ap.add_argument("--install-dir", default="/opt/AdGuardHome")
    ap.add_argument("--service", default="AdGuardHome.service")
    a = ap.parse_args()
    if os.geteuid() != 0:
        sys.exit("sudo を付けて実行してください。")
    if not (a.enable_funnel_doh or a.faster):
        sys.exit("--enable-funnel-doh か --faster を指定してください。")

    yaml = os.path.join(a.install_dir, "AdGuardHome.yaml")
    exe = os.path.join(a.install_dir, "AdGuardHome")
    if not os.path.exists(yaml):
        sys.exit(f"設定ファイルがありません（初期設定がまだ？）: {yaml}")
    with open(yaml, encoding="utf-8") as f:
        text = f.read()

    new = text
    if a.enable_funnel_doh:
        if re.search(r"(?m)^\s+insecure_enabled:", new):
            new = set_scalar(new, "insecure_enabled", "true")       # v0.107.5x 以降
        else:
            new = set_scalar(new, "allow_unencrypted_doh", "true")  # 古い版
    if a.faster:
        new = set_list(new, "upstream_dns", ["https://dns.cloudflare.com/dns-query", "https://dns.google/dns-query",
                                             "https://dns10.quad9.net/dns-query"])
        new = set_list(new, "bootstrap_dns", ["1.1.1.1", "8.8.8.8", "9.9.9.10", "2606:4700:4700::1111",
                                              "2001:4860:4860::8888"])
        new = set_scalar(new, "  ratelimit", "0")
        new = set_scalar(new, "  upstream_mode", "parallel")
        new = set_scalar(new, "  cache_size", "33554432")
        new = set_scalar(new, "  cache_optimistic", "true")
    if new == text:
        print("すでに設定済みです。")
        return

    # 検証してから適用
    check = os.path.join(a.install_dir, "AdGuardHome.check.yaml")
    fd = os.open(check, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(new)
    try:
        p = subprocess.run([exe, "--check-config", "-c", check, "-w", a.install_dir, "--no-permcheck"],
                           capture_output=True, text=True)
    finally:
        os.remove(check)
    if p.returncode != 0:
        sys.exit("設定の検証に失敗したため、変更していません: " + (p.stderr or p.stdout))

    backup = f"{yaml}.bak-{time.strftime('%Y%m%d-%H%M%S')}"
    shutil.copy2(yaml, backup)
    subprocess.run(["systemctl", "stop", a.service], check=True)
    try:
        st = os.stat(yaml)
        with open(yaml, "w", encoding="utf-8") as f:
            f.write(new)
        os.chmod(yaml, st.st_mode & 0o777)
    finally:
        subprocess.run(["systemctl", "start", a.service], check=True)
    print(f"変更しました（バックアップ: {backup}）")


if __name__ == "__main__":
    main()
