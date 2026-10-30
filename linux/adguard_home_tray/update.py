# 新しい版の確認と、このアプリ自身の更新。
# AdGuard Home 本体の更新は、root で動く systemd のユニット adguard-home-update.service（AdGuardHome --update）が行う。
# このアプリはユーザーの領域（~/.local/share/adguard-home-tray）にあるので、管理者の確認なしで自分で入れ替える。
import hashlib
import json
import os
import re
import subprocess
import tarfile
import tempfile
import urllib.request

from . import REPO, VERSION

AGH_VERSION_URL = "https://static.adtidy.org/adguardhome/release/version.json"
RELEASE_API = f"https://api.github.com/repos/{REPO}/releases/latest"
PACKAGE = "adguard-home-linux.tar.gz"


def parse_version(v):
    nums = re.findall(r"\d+", v or "")
    return tuple(int(n) for n in nums[:4])


def newer(latest, current):
    return bool(latest) and bool(current) and parse_version(latest) > parse_version(current)


def _get(url, timeout=30):
    req = urllib.request.Request(url, headers={"User-Agent": f"AdGuardHomeTray/{VERSION}",
                                               "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def latest_adguard_home():
    """AdGuard Home の最新版（例: v0.107.79）"""
    return json.loads(_get(AGH_VERSION_URL)).get("version")


def latest_release():
    """このアプリの最新リリース (版, {添付ファイル名: URL})"""
    rel = json.loads(_get(RELEASE_API))
    assets = {a["name"]: a["browser_download_url"] for a in rel.get("assets", [])}
    return rel.get("tag_name", "").lstrip("v"), assets


def self_update():
    """最新版があれば SHA-256 を照合して入れ替える。戻り値: 'updated' / 'latest'。失敗は例外"""
    version, assets = latest_release()
    if not newer(version, VERSION):
        return "latest"
    if PACKAGE not in assets or PACKAGE + ".sha256" not in assets:
        raise RuntimeError(f"{PACKAGE} is not attached to the release v{version}")
    prefix = f"https://github.com/{REPO}/releases/download/"
    for name in (PACKAGE, PACKAGE + ".sha256"):
        if not assets[name].startswith(prefix):
            raise RuntimeError(f"unexpected download URL {assets[name]}")
    data = _get(assets[PACKAGE], timeout=120)
    expected = _get(assets[PACKAGE + ".sha256"]).decode().split()[0].lower()
    if hashlib.sha256(data).hexdigest() != expected:
        raise RuntimeError("SHA-256 mismatch")
    with tempfile.TemporaryDirectory(prefix="adguard-home-tray-") as tmp:
        path = os.path.join(tmp, PACKAGE)
        with open(path, "wb") as f:
            f.write(data)
        with tarfile.open(path) as tar:
            tar.extractall(tmp, filter="data")
        installer = os.path.join(tmp, "adguard-home-linux", "install.sh")
        p = subprocess.run(["bash", installer, "--update"], capture_output=True, text=True, timeout=300)
        if p.returncode != 0:
            raise RuntimeError((p.stderr or p.stdout).strip()[-300:])
    return "updated"
