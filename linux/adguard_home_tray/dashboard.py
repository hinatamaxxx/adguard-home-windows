# 管理画面の専用ウィンドウ（Qt WebEngine）。ブラウザーの部品は出さず、保存したログイン情報で自動ログインする。
# Qt WebEngine が入っていない環境では、既定のブラウザーで開く。
import json
import os

from PyQt6.QtCore import QUrl, Qt
from PyQt6.QtGui import QDesktopServices, QKeySequence, QShortcut

from . import APP_ID, icons

try:
    from PyQt6.QtWebEngineCore import QWebEnginePage, QWebEngineProfile, QWebEngineSettings
    from PyQt6.QtWebEngineWidgets import QWebEngineView
    AVAILABLE = True
except ImportError:
    AVAILABLE = False

_window = None


def open_page(url, credentials):
    """url を専用のウィンドウで開く（すでに開いていれば、そのウィンドウで移動して前面へ）"""
    global _window
    if not AVAILABLE:
        QDesktopServices.openUrl(QUrl(url))
        return
    if _window is None:
        _window = DashboardWindow()
    _window.credentials = credentials
    _window.navigate(url)


if AVAILABLE:
    class _Page(QWebEnginePage):
        def __init__(self, profile, parent, home):
            super().__init__(profile, parent)
            self.home = home

        # 管理画面以外へのリンクは既定のブラウザーで開く
        def acceptNavigationRequest(self, url, nav_type, is_main_frame):
            if is_main_frame and nav_type == QWebEnginePage.NavigationType.NavigationTypeLinkClicked:
                if self.home() and url.host() != QUrl(self.home()).host():
                    QDesktopServices.openUrl(url)
                    return False
            return super().acceptNavigationRequest(url, nav_type, is_main_frame)

        def createWindow(self, _type):
            page = QWebEnginePage(self.profile(), self)
            page.urlChanged.connect(lambda u: (QDesktopServices.openUrl(u), page.deleteLater()))
            return page

    class DashboardWindow(QWebEngineView):
        def __init__(self):
            super().__init__()
            self.credentials = (None, None)
            self.home_url = None
            self.auto_login_tried = False
            profile = QWebEngineProfile("adguard-home-tray", self)
            # 管理画面の Cookie とキャッシュは、アプリの入れ替え（更新）で消えない場所に置く
            state = os.environ.get("XDG_STATE_HOME") or os.path.expanduser("~/.local/state")
            cache = os.environ.get("XDG_CACHE_HOME") or os.path.expanduser("~/.cache")
            profile.setPersistentStoragePath(os.path.join(state, APP_ID, "webengine"))
            profile.setCachePath(os.path.join(cache, APP_ID))
            profile.setPersistentCookiesPolicy(QWebEngineProfile.PersistentCookiesPolicy.AllowPersistentCookies)
            self.setPage(_Page(profile, self, lambda: self.home_url))
            self.settings().setAttribute(QWebEngineSettings.WebAttribute.ScrollAnimatorEnabled, True)
            self.setContextMenuPolicy(Qt.ContextMenuPolicy.NoContextMenu)
            self.setWindowTitle("AdGuard Home")
            self.setWindowIcon(icons.icon("running"))
            self.resize(1200, 820)
            self.loadFinished.connect(self._on_load)
            for keys, action in (("F5", self.reload), ("Ctrl+R", self.reload), ("Alt+Left", self.back)):
                QShortcut(QKeySequence(keys), self, action)

        def navigate(self, url):
            self.home_url = url
            self.auto_login_tried = False
            self.load(QUrl(url))
            self.show()
            self.raise_()
            self.activateWindow()

        def _on_load(self, ok):
            if not ok or not self.url().path().endswith("/login.html"):
                self.auto_login_tried = False
                return
            user, password = self.credentials
            if not user or self.auto_login_tried:
                return   # 失敗したら繰り返さず、ログイン画面のままにする
            self.auto_login_tried = True
            body = json.dumps(json.dumps({"name": user, "password": password}))
            target = json.dumps(self.home_url or "/")
            self.page().runJavaScript(
                "fetch('/control/login',{method:'POST',headers:{'Content-Type':'application/json'},body:" + body + "})"
                ".then(function(r){ if (r.ok) location.href = " + target + "; });")

        def closeEvent(self, e):
            global _window
            _window = None
            self.deleteLater()
            super().closeEvent(e)
