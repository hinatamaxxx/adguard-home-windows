# AdGuard Home の管理 API クライアントと、ログイン情報の保存。
# ログイン情報は Secret Service（KDE では KWallet）に保存する。使えない環境では
# ~/.config/adguard-home-tray/credentials.json（所有者だけが読める 0600）に保存する。
import base64
import http.cookiejar
import json
import os
import urllib.error
import urllib.request

from . import APP_ID, VERSION
from .l10n import T

CONFIG_DIR = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), APP_ID)
CRED_FILE = os.path.join(CONFIG_DIR, "credentials.json")
KEYRING_SERVICE = "AdGuardHomeTray"


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


class AghApi:
    def __init__(self):
        self.base_url = None   # 例: http://127.0.0.1/
        self.user, self.password = load_credentials()
        self._opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.ProxyHandler({}))

    @property
    def has_credentials(self):
        return bool(self.user)

    def set_credentials(self, user, password):
        self.user, self.password = user, password

    def get(self, path):
        return self._request("GET", path, None)

    def post(self, path, body):
        return self._request("POST", path, body)

    def _request(self, method, path, body):
        try:
            return self._send(method, path, body, True)
        except ApiError as ex:
            # Basic 認証が通らない場合はセッション（Cookie）でログインして再試行
            if ex.status not in (401, 403) or not self.has_credentials:
                raise
            self._send("POST", "control/login", {"name": self.user, "password": self.password}, False)
            return self._send(method, path, body, False)

    def _send(self, method, path, body, basic):
        if not self.base_url:
            raise ApiError(0, T("管理画面のアドレスが見つかりません", "Dashboard address not found"))
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base_url.rstrip("/") + "/" + path, data=data, method=method)
        req.add_header("User-Agent", f"AdGuardHomeTray/{VERSION}")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        if basic and self.has_credentials:
            token = base64.b64encode(f"{self.user}:{self.password}".encode()).decode()
            req.add_header("Authorization", "Basic " + token)
        try:
            with self._opener.open(req, timeout=5) as resp:
                text = resp.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as ex:
            msg = ex.read().decode("utf-8", "replace").strip()
            raise ApiError(ex.code, f"{ex.code} {msg[:120]}") from None
        except (urllib.error.URLError, OSError) as ex:
            raise ApiError(0, str(getattr(ex, "reason", ex))) from None
        if not text.strip():
            return None
        try:
            return json.loads(text)
        except ValueError:
            return text


def probe(url):
    """url が AdGuard Home の管理画面（初期設定の画面を含む）なら True"""
    for path in ("control/status", "control/install/get_addresses"):
        req = urllib.request.Request(url.rstrip("/") + "/" + path)
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=2):
                return True
        except urllib.error.HTTPError as ex:
            if ex.code in (401, 403):
                return True
        except Exception:
            return False
    return False


# ---------- ログイン情報の保存 ----------

def _keyring():
    try:
        import keyring
        from keyring.backends import fail
        kr = keyring.get_keyring()
        if isinstance(kr, fail.Keyring) or getattr(kr, "priority", 1) <= 0:
            return None
        return keyring
    except Exception:
        return None


def load_credentials():
    kr = _keyring()
    if kr:
        try:
            raw = kr.get_password(KEYRING_SERVICE, "login")
            if raw:
                d = json.loads(raw)
                if d.get("user") and d.get("password") is not None:
                    return d["user"], d["password"]
        except Exception:
            pass
    try:
        with open(CRED_FILE, encoding="utf-8") as f:
            d = json.load(f)
        if d.get("user") and d.get("password") is not None:
            return d["user"], d["password"]
    except Exception:
        pass
    return None, None


def save_credentials(user, password):
    raw = json.dumps({"user": user, "password": password})
    kr = _keyring()
    if kr:
        try:
            kr.set_password(KEYRING_SERVICE, "login", raw)
            _remove_file()
            return "keyring"
        except Exception:
            pass
    os.makedirs(CONFIG_DIR, mode=0o700, exist_ok=True)
    tmp = CRED_FILE + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(raw)
    os.replace(tmp, CRED_FILE)
    return "file"


def delete_credentials():
    kr = _keyring()
    if kr:
        try:
            kr.delete_password(KEYRING_SERVICE, "login")
        except Exception:
            pass
    _remove_file()


def _remove_file():
    try:
        os.remove(CRED_FILE)
    except FileNotFoundError:
        pass
