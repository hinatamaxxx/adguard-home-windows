# systemd のサービス、待ち受けアドレス、自動起動（~/.config/autostart）、設定ファイルの扱い。
import ipaddress
import json
import os
import re
import shutil
import subprocess

from . import APP_ID

SERVICE = os.environ.get("ADGUARD_TRAY_SERVICE", "AdGuardHome.service")
UPDATE_TIMER = "adguard-home-update.timer"
UPDATE_SERVICE = "adguard-home-update.service"
# 確認用: ADGUARD_TRAY_USER_UNITS=1 なら systemctl --user のユニットを見る
_SCOPE = ["--user"] if os.environ.get("ADGUARD_TRAY_USER_UNITS") == "1" else []

CONFIG_DIR = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), APP_ID)
SETTINGS_FILE = os.path.join(CONFIG_DIR, "settings.json")
AUTOSTART_FILE = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"),
                              "autostart", APP_ID + ".desktop")
APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _run(args, timeout=5):
    try:
        p = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
        return p.returncode, p.stdout
    except Exception:
        return -1, ""


# ---------- サービス ----------

def unit_state(unit):
    """(LoadState, ActiveState)。例: ('loaded', 'active')、未登録なら ('not-found', 'inactive')"""
    code, out = _run(["systemctl", *_SCOPE, "show", unit, "-p", "LoadState,ActiveState,UnitFileState"])
    props = dict(line.split("=", 1) for line in out.splitlines() if "=" in line)
    return props.get("LoadState", "not-found"), props.get("ActiveState", "inactive"), props.get("UnitFileState", "")


def service_status():
    """None（未登録）/ 'active' / 'inactive' / 'failed' / 'activating' / 'deactivating'"""
    load, active, _ = unit_state(SERVICE)
    if load == "not-found":
        return None
    return active


def systemctl_command(action, unit=SERVICE):
    """サービスの操作。一般ユーザーで実行すると polkit が管理者の確認画面を出す"""
    if action in ("enable", "disable"):
        return ["systemctl", *_SCOPE, action, "--now", unit]
    return ["systemctl", *_SCOPE, action, unit]


def auto_update_timer():
    """自動更新のタイマー: None（未導入）/ True（オン）/ False（オフ）"""
    load, _, file_state = unit_state(UPDATE_TIMER)
    if load == "not-found":
        return None
    return file_state == "enabled"


# ---------- 待ち受けアドレス ----------

def _ss_local_addresses(proto, port):
    flag = "-lun" if proto == "udp" else "-ltn"
    code, out = _run(["ss", "-H", flag, f"sport = :{port}"])
    result = []
    for line in out.splitlines():
        cols = line.split()
        if len(cols) < 4:
            continue
        local = cols[3]
        host = local.rsplit(":", 1)[0].strip("[]")
        host = host.split("%", 1)[0]
        result.append(host)
    return result


# systemd-resolved のスタブ（AdGuard Home ではない）
_RESOLVED_STUBS = {"127.0.0.53", "127.0.0.54"}


def dns_listeners():
    """(ポート 53 で待ち受けているアドレスの一覧, DNS-over-TLS の有無)"""
    bound = [h for h in _ss_local_addresses("udp", 53) if h not in _RESOLVED_STUBS]
    dot = bool(_ss_local_addresses("tcp", 853))
    return list(dict.fromkeys(bound)), dot


def _is_bound(addr, bound):
    v6 = ":" in addr
    if v6 and ("::" in bound or "*" in bound):
        return True
    if not v6 and ("0.0.0.0" in bound or "*" in bound):
        return True
    return addr in bound


_SKIP_IFACES = re.compile(r"^(lo|docker\d*|br-|veth|virbr|vnet|lxcbr|lxdbr|podman|cni|flannel|tun-|zt)")


def interface_addresses(bound):
    """(LAN の [(アドレス, 接続名)], Tailscale の [(アドレス, 接続名)])"""
    code, out = _run(["ip", "-j", "addr", "show", "up"])
    lan, vpn = [], []
    try:
        ifaces = json.loads(out or "[]")
    except ValueError:
        ifaces = []
    for nic in ifaces:
        name = nic.get("ifname", "")
        if _SKIP_IFACES.match(name):
            continue
        is_vpn = name.startswith("tailscale")
        for a in nic.get("addr_info", []):
            addr = a.get("local", "")
            if not addr or a.get("scope") not in ("global", None):
                continue
            if a.get("temporary") or "temporary" in a.get("flags", []):
                continue
            try:
                ip = ipaddress.ip_address(addr)
            except ValueError:
                continue
            if ip.is_link_local or ip.is_loopback:
                continue
            if not _is_bound(addr, bound):
                continue
            (vpn if is_vpn else lan).append((addr, name, ip.version))
    key = lambda e: e[2]   # IPv4 を先に並べる
    return [(a, n) for a, n, _ in sorted(lan, key=key)], [(a, n) for a, n, _ in sorted(vpn, key=key)]


def loopbacks(bound):
    return [a for a in ("127.0.0.1", "::1") if _is_bound(a, bound)]


# ---------- 設定 ----------

def load_settings():
    try:
        with open(SETTINGS_FILE, encoding="utf-8") as f:
            d = json.load(f)
            return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def save_settings(d):
    os.makedirs(CONFIG_DIR, mode=0o700, exist_ok=True)
    tmp = SETTINGS_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)
    os.replace(tmp, SETTINGS_FILE)


# ---------- 自動起動（~/.config/autostart。Plasma の「システム設定 → 自動起動」と同じファイル） ----------

def autostart_enabled():
    try:
        with open(AUTOSTART_FILE, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return False
    return not re.search(r"(?m)^\s*Hidden\s*=\s*true\s*$", text, re.I)


def launcher_path():
    found = shutil.which(APP_ID)
    return found or os.path.join(APP_DIR, APP_ID)


def desktop_entry(autostart):
    lines = [
        "[Desktop Entry]",
        "Type=Application",
        "Name=AdGuard Home",
        "Comment=Manage AdGuard Home from the system tray",
        "Comment[ja]=AdGuard Home をシステムトレイから管理",
        f"Exec={launcher_path()}" + ("" if autostart else " --open"),
        f"Icon={APP_ID}",
        "Terminal=false",
        "Categories=Network;System;",
    ]
    if autostart:
        lines += ["X-GNOME-Autostart-enabled=true", "X-KDE-autostart-after=panel"]
    return "\n".join(lines) + "\n"


def set_autostart(on):
    if on:
        os.makedirs(os.path.dirname(AUTOSTART_FILE), exist_ok=True)
        with open(AUTOSTART_FILE, "w", encoding="utf-8") as f:
            f.write(desktop_entry(True))
    else:
        try:
            os.remove(AUTOSTART_FILE)
        except FileNotFoundError:
            pass
