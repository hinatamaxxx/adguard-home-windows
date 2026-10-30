# システムトレイのアイコンとメニュー（KDE Plasma では StatusNotifierItem + DBusMenu として表示される）。
# メニューの形は Windows 版と同じ: 状態 / 管理画面を開く / 保護とサービス / アップデート / その他 / アプリを終了する
import concurrent.futures
import os
import subprocess
import sys
import time

from PyQt6.QtCore import QObject, Qt, QTimer, QUrl, pyqtSignal
from PyQt6.QtGui import QAction, QDesktopServices
from PyQt6.QtNetwork import QLocalServer, QLocalSocket
from PyQt6.QtWidgets import (QApplication, QDialog, QDialogButtonBox, QFormLayout, QLabel, QLineEdit,
                             QMenu, QMessageBox, QSystemTrayIcon, QVBoxLayout)

from . import APP_ID, VERSION, api, dashboard, icons, system, update
from .l10n import T, ago, format_duration

PAGES = [
    (T("ダッシュボード", "Dashboard"), ""), (T("一般設定", "General settings"), "settings"),
    (T("DNS 設定", "DNS settings"), "dns"), (T("暗号化設定", "Encryption settings"), "encryption"),
    (T("クライアント設定", "Client settings"), "clients"), (T("DHCP 設定", "DHCP settings"), "dhcp"),
    None,
    (T("DNS ブロックリスト", "DNS blocklists"), "filters"), (T("DNS 許可リスト", "DNS allowlists"), "allowlists"),
    (T("DNS 書き換え", "DNS rewrites"), "dns_rewrites"), (T("ブロックするサービス", "Blocked services"), "blocked_services"),
    (T("カスタムフィルタリングルール", "Custom filtering rules"), "custom_rules"),
    None,
    (T("クエリログ", "Query log"), "logs"),
]
PAUSES = [(T("1 分間 無効にする", "Pause for 1 minute"), 60000), (T("10 分間 無効にする", "Pause for 10 minutes"), 600000),
          (T("1 時間 無効にする", "Pause for 1 hour"), 3600000), (T("8 時間 無効にする", "Pause for 8 hours"), 28800000),
          (T("再開するまで無効にする", "Pause until resumed"), 0)]


class _Bridge(QObject):
    """作業スレッドから UI スレッドへ処理を戻す"""
    call = pyqtSignal(object)


class Tray(QObject):
    def __init__(self, app):
        super().__init__()
        self.app = app
        self.pool = concurrent.futures.ThreadPoolExecutor(max_workers=4)
        self.bridge = _Bridge()
        self.bridge.call.connect(lambda f: f())
        self.api = api.AghApi()
        self.settings = system.load_settings()

        self.status = "unknown-yet"     # None（未登録）/ 'active' / 'inactive' / …
        self.protection = None          # True / False / None（不明）
        self.protection_note = ""
        self.api_reason = None
        self.agh_version = ""
        self.latest_agh = None
        self.latest_app = None
        self.filters_updated = None
        self.filters_checked = 0
        self.api_busy = False
        self.updating = False
        self.web_url = None
        self.stopped_since = None
        self.stop_notified = False
        self.quiet_until = 0            # 自分で止めた・更新中の間は「停止しました」を知らせない

        self.tray = QSystemTrayIcon(icons.icon("running"))
        self.tray.activated.connect(self._on_activated)
        self._build_menu()
        self.tray.setContextMenu(self.menu)
        self.tray.show()

        self.status_timer = QTimer(self, interval=2000, timeout=self.refresh_status)
        self.api_timer = QTimer(self, interval=15000, timeout=self.refresh_api)
        self.update_timer = QTimer(self, interval=6 * 3600 * 1000, timeout=self.check_updates)
        self.status_timer.start()
        self.api_timer.start()
        self.update_timer.start()
        self.refresh_status()
        QTimer.singleShot(1500, self.refresh_api)
        QTimer.singleShot(5000, self.check_updates)

    # ---------- 共通 ----------

    def run_async(self, work, done):
        def task():
            try:
                result, error = work(), None
            except Exception as ex:   # noqa: BLE001 - 失敗は通知で知らせる
                result, error = None, ex
            self.bridge.call.emit(lambda: done(result, error))
        self.pool.submit(task)

    def notify(self, text, state=None, ms=5000):
        # 本文のアイコンは、トレイアイコンの今の状態と同じものにする
        self.tray.showMessage("AdGuard Home", text, icons.icon(state or self.icon_state()), ms)

    @property
    def running(self):
        return self.status == "active"

    @property
    def auto_update(self):
        timer = system.auto_update_timer()
        if timer is not None:
            return timer
        return self.settings.get("auto_update", True)

    # ---------- メニュー ----------

    def _action(self, menu, text, slot=None, enabled=True):
        a = QAction(text, menu)
        if slot:
            a.triggered.connect(lambda _=False: slot())
        a.setEnabled(enabled)
        menu.addAction(a)
        return a

    def _build_menu(self):
        m = self.menu = QMenu()
        self.mi_status = self._action(m, "AdGuard Home", enabled=False)
        self.mi_state = self._action(m, T("確認中…", "Checking…"), enabled=False)
        self.mi_new = self._action(m, "", enabled=False)
        self.mi_new.setVisible(False)
        m.addSeparator()
        self._action(m, T("管理画面を開く", "Open dashboard"), self.open_dashboard)

        self.m_protection = m.addMenu(T("保護とサービス", "Protection and service"))
        self.m_protection.aboutToShow.connect(self._fill_protection)

        self.m_update = m.addMenu(T("アップデート", "Updates"))
        self.m_update.aboutToShow.connect(self._fill_update)

        more = m.addMenu(T("その他", "More"))
        self.m_dns = more.addMenu(T("接続用アドレス（DNS サーバー）", "DNS server addresses"))
        self.m_dns.aboutToShow.connect(self._fill_addresses)
        pages = more.addMenu(T("設定ページを開く", "Open settings page"))
        for page in PAGES:
            if page is None:
                pages.addSeparator()
            else:
                self._action(pages, page[0], lambda h=page[1]: self.open_page(h))
        self._action(more, T("管理画面をブラウザーで開く", "Open dashboard in browser"), self.open_in_browser)
        more.addSeparator()
        self.mi_login = self._action(more, "", self.edit_login)
        self.mi_logout = self._action(more, T("保存したログイン情報を削除", "Delete saved login info"), self.delete_login)
        self.mi_autostart = self._action(more, T("ログイン時に自動起動", "Start at login"), self.toggle_autostart)
        self.mi_autostart.setCheckable(True)
        more.aboutToShow.connect(self._refresh_more)
        self._refresh_more()

        m.addSeparator()
        self._action(m, T("アプリを終了する", "Exit"), self.confirm_exit)
        self._fill_protection()
        self._fill_update()

    def _refresh_more(self):
        self.mi_login.setText(T("ログイン情報を変更…", "Change login info…") if self.api.has_credentials
                              else T("ログイン情報を設定…", "Set login info…"))
        self.mi_logout.setEnabled(self.api.has_credentials)
        self.mi_autostart.setChecked(system.autostart_enabled())

    def _fill_protection(self):
        m = self.m_protection
        m.clear()
        if not self.running:
            pass   # 止まっているときはサービスの操作だけ
        elif self.protection is True:
            for text, ms in PAUSES:
                self._action(m, text, lambda ms=ms: self.set_protection(False, ms))
        elif self.protection is False:
            self._action(m, T("保護を再開する", "Resume protection"), lambda: self.set_protection(True, 0))
        else:
            if self.api_reason:
                self._action(m, T("保護の状態: ", "Protection status: ") + self.api_reason, enabled=False)
            self._action(m, T("ログイン情報を設定…", "Set login info…"), self.edit_login)
        if m.actions():
            m.addSeparator()
        if self.running:
            self._action(m, T("AdGuard Home を再起動", "Restart AdGuard Home"), lambda: self.service("restart"))
            self._action(m, T("AdGuard Home を停止…", "Stop AdGuard Home…"), self.confirm_stop)
        else:
            self._action(m, T("AdGuard Home を開始", "Start AdGuard Home"), lambda: self.service("start"),
                         enabled=self.status is not None)

    def _version_line(self, name, current, latest):
        if not current:
            return name
        text = f"{name} v{current.lstrip('v')}"
        if update.newer(latest, current):
            return text + T(f"（v{latest.lstrip('v')} があります）", f" (v{latest.lstrip('v')} available)")
        return text + T("（最新です）", " (up to date)") if latest else text

    def _fill_update(self):
        m = self.m_update
        m.clear()
        self._action(m, self._version_line("AdGuard Home", self.agh_version, self.latest_agh), enabled=False)
        self._action(m, self._version_line(T("このアプリ", "This app"), VERSION, self.latest_app), enabled=False)
        m.addSeparator()
        if self.updating:
            self._action(m, T("更新しています…", "Updating…"), enabled=False)
        else:
            self._action(m, T("今すぐ確認して更新", "Check and update now"), self.update_now)
        a = self._action(m, T("自動で更新する（毎日）", "Update automatically (daily)"), self.toggle_auto_update)
        a.setCheckable(True)
        a.setChecked(self.auto_update)

    def _fill_addresses(self):
        m = self.m_dns
        m.clear()
        if not self.running:
            self._info(m, T("AdGuard Home が動いていません", "AdGuard Home is not running"))
            return
        bound, dot = system.dns_listeners()
        if not bound:
            self._info(m, T("DNS の待ち受けが見つかりません\n（初期設定前かもしれません）",
                            "No DNS listener found\n(initial setup may not be finished yet)"))
            return
        self._action(m, T("このPCで使う場合", "On this PC"), enabled=False)
        for a in system.loopbacks(bound):
            self._copy_item(m, a, a)
        lan, vpn = system.interface_addresses(bound)
        if lan:
            self._action(m, T("同じネットワークの機器から使う場合", "From devices on the same network"), enabled=False)
            for a, name in lan:
                self._copy_item(m, a + T(f"（{name}）", f" ({name})"), a)
        if vpn:
            self._action(m, T("Tailscale 経由で使う場合", "Through Tailscale"), enabled=False)
            for a, name in vpn:
                self._copy_item(m, a + T(f"（{name}）", f" ({name})"), a)
        if dot:
            self._action(m, T("暗号化 DNS", "Encrypted DNS"), enabled=False)
            self._info(m, T("    DNS-over-TLS（ポート 853）が有効です\n    サーバー名は暗号化設定で確認してください",
                            "    DNS-over-TLS (port 853) is on\n    Check the server name in Encryption settings"))
        m.addSeparator()
        self._info(m, T("項目をクリックするとアドレスをコピーします", "Click an item to copy the address"))

    def _info(self, menu, text):
        for line in text.split("\n"):   # メニューは改行を表示できないので、行ごとに項目を分ける
            self._action(menu, line, enabled=False)

    def _copy_item(self, menu, text, value):
        self._action(menu, "    " + text, lambda: QApplication.clipboard().setText(value))

    # ---------- 状態 ----------

    def icon_state(self):
        if self.status is None:
            return "unknown"
        if not self.running or self.protection is False:
            return "stopped"
        if self.pending_update_text():
            return "update"
        return "running"

    def pending_update_text(self):
        """新しい版の表示は、自動更新をオフにしているときだけ"""
        if self.auto_update:
            return None
        if update.newer(self.latest_agh, self.agh_version):
            return T(f"新しい AdGuard Home {self.latest_agh} があります", f"AdGuard Home {self.latest_agh} is available")
        if update.newer(self.latest_app, VERSION):
            return T(f"このアプリの新しい版 v{self.latest_app} があります", f"This app v{self.latest_app} is available")
        return None

    def state_text(self):
        if self.status is None:
            return T("サービスが登録されていません", "The service is not installed")
        if not self.running:
            return {"activating": T("開始中", "Starting"), "deactivating": T("停止中…", "Stopping…"),
                    "failed": T("異常終了しました", "Failed")}.get(self.status, T("停止しています", "Stopped"))
        if self.protection is False:
            return T("保護は無効です", "Protection is off") + self.protection_note
        if self.protection is True:
            return T("保護は有効です", "Protection is on")
        return T("動作中", "Running")

    def update_visuals(self):
        self.tray.setIcon(icons.icon(self.icon_state()))
        title = "AdGuard Home" + (f" {self.agh_version}" if self.agh_version else "")
        self.mi_status.setText(title)
        self.mi_state.setText(self.state_text())
        pending = self.pending_update_text()
        self.mi_new.setVisible(bool(pending))
        self.mi_new.setText(pending or "")
        lines = [title, self.state_text()]
        if pending:
            lines.append(pending)
        if self.running and self.filters_updated:
            lines.append(T(f"フィルタは{ago(time.time() - self.filters_updated)}に更新されました",
                           f"Filters: {ago(time.time() - self.filters_updated)}"))
        self.tray.setToolTip("\n".join(lines[:3]))

    def refresh_status(self):
        old = self.status
        self.status = system.service_status()
        now = time.time()
        if self.running:
            self.stopped_since = None
            self.stop_notified = False
        elif old == "active":
            self.stopped_since = now
        # 思いがけず止まったときだけ、停止が 10 秒以上続いてから知らせる
        if (self.stopped_since and not self.stop_notified and now - self.stopped_since >= 10
                and now > self.quiet_until and self.status is not None):
            self.stop_notified = True
            self.notify(T("AdGuard Home が停止しました。", "AdGuard Home has stopped."), "stopped")
        if old != self.status:
            self.web_url = None
            self._fill_protection()
            if self.running and old != "unknown-yet":
                QTimer.singleShot(1500, self.refresh_api)
            elif not self.running:
                self.show_api_unavailable(T("サービスが停止しています", "The service is stopped"))
        self.update_visuals()

    def dashboard_url(self):
        """管理画面の URL。設定（web_url）→ 80 番 → 初期設定用の 3000 番の順に探す"""
        if self.web_url:
            return self.web_url
        candidates = [self.settings.get("web_url"), "http://127.0.0.1/", "http://127.0.0.1:3000/"]
        for url in filter(None, candidates):
            if api.probe(url):
                self.web_url = url
                return url
        return self.settings.get("web_url") or "http://127.0.0.1/"

    def show_api_unavailable(self, reason):
        self.protection = None
        self.protection_note = ""
        self.api_reason = reason
        self._fill_protection()
        self.update_visuals()

    def refresh_api(self):
        if self.api_busy:
            return
        if not self.running:
            self.show_api_unavailable(T("サービスが停止しています", "The service is stopped"))
            return
        if not self.api.has_credentials:
            self.show_api_unavailable(T("ログイン情報を設定すると表示されます", "Set login info to see this"))
            return
        self.api_busy = True

        def work():
            self.api.base_url = self.dashboard_url()
            status = self.api.get("control/status")
            filters = None
            if time.time() - self.filters_checked >= 300:
                filters = self.api.get("control/filtering/status")
            return status, filters

        def done(result, error):
            self.api_busy = False
            if error:
                if isinstance(error, api.ApiError) and error.status in (401, 403):
                    self.show_api_unavailable(T("ログイン情報が正しくありません", "The login info is incorrect"))
                else:
                    self.show_api_unavailable(T(f"取得できません（{error}）", f"Unavailable ({error})"))
                return
            status, filters = result
            status = status or {}
            self.protection = bool(status.get("protection_enabled"))
            ms = 0 if self.protection else int(status.get("protection_disabled_duration") or 0)
            self.protection_note = T(f"（あと {format_duration(ms)}）", f" ({format_duration(ms)} left)") if ms > 0 else ""
            self.api_reason = None
            if status.get("version"):
                self.agh_version = status["version"]
            if filters is not None:
                self.filters_checked = time.time()
                self.filters_updated = _latest_filter_update(filters)
            self._fill_protection()
            self.update_visuals()

        self.run_async(work, done)

    # ---------- 操作 ----------

    def _on_activated(self, reason):
        if reason in (QSystemTrayIcon.ActivationReason.Trigger, QSystemTrayIcon.ActivationReason.DoubleClick):
            self.open_dashboard()

    def open_dashboard(self):
        self.open_page("")

    def open_page(self, hash_):
        url = self.dashboard_url() + (f"#{hash_}" if hash_ else "")
        dashboard.open_page(url, (self.api.user, self.api.password))

    def open_in_browser(self):
        QDesktopServices.openUrl(QUrl(self.dashboard_url()))

    def set_protection(self, enabled, duration_ms):
        def work():
            self.api.base_url = self.dashboard_url()
            body = {"enabled": enabled}
            if not enabled and duration_ms > 0:
                body["duration"] = duration_ms
            try:
                self.api.post("control/protection", body)
            except api.ApiError as ex:
                if ex.status not in (404, 405):
                    raise
                self.api.post("control/dns_config", {"protection_enabled": enabled})

        def done(_, error):
            if error:
                self.notify(T("保護の切り替えに失敗しました: ", "Could not change protection: ") + str(error))
            else:
                self.protection = enabled   # 通知とアイコンを新しい状態にそろえる
                self.update_visuals()
                self.notify(T("保護を再開しました。", "Protection resumed.") if enabled
                            else T(f"保護を無効にしました（{format_duration(duration_ms)}後に再開）。",
                                   f"Protection is off (back on in {format_duration(duration_ms)}).") if duration_ms
                            else T("保護を無効にしました。", "Protection is off."), ms=3000)
            self.refresh_api()

        self.run_async(work, done)

    def service(self, action):
        """開始・停止・再起動（polkit が管理者の確認画面を出す）"""
        self.quiet_until = time.time() + 120

        def work():
            p = subprocess.run(system.systemctl_command(action), capture_output=True, text=True, timeout=180)
            if p.returncode != 0:
                raise RuntimeError((p.stderr or p.stdout).strip() or f"exit {p.returncode}")

        def done(_, error):
            if error and "authoriz" not in str(error).lower() and "認証" not in str(error):
                self.notify(T("操作できませんでした: ", "The operation failed: ") + str(error)[-200:])
            self.refresh_status()

        self.run_async(work, done)

    def confirm_stop(self):
        box = QMessageBox(QMessageBox.Icon.Question, "AdGuard Home",
                          T("AdGuard Home を停止しますか？", "Stop AdGuard Home?"))
        box.setInformativeText(T("停止している間、DNS をこの PC に向けた機器（このPCを含む）はインターネットにつながりません。",
                                 "While it is stopped, devices that use this PC for DNS (including this PC) can't reach the internet."))
        stop = box.addButton(T("停止する", "Stop"), QMessageBox.ButtonRole.AcceptRole)
        cancel = box.addButton(T("キャンセル", "Cancel"), QMessageBox.ButtonRole.RejectRole)
        box.setDefaultButton(cancel)
        box.exec()
        if box.clickedButton() is stop:
            self.service("stop")

    def confirm_exit(self):
        box = QMessageBox(QMessageBox.Icon.Question, "AdGuard Home", T("アプリを終了しますか？", "Exit the app?"))
        box.setInformativeText(T("AdGuard Home 本体は動き続けます。状態の表示、保護の一時停止、停止の通知、このアプリの自動更新は使えなくなります。\n"
                                 "アプリケーションメニューの「AdGuard Home」で、もう一度表示できます。",
                                 "AdGuard Home itself keeps running. Status, pausing protection, stop alerts, and this app's updates won't be available.\n"
                                 "Open \"AdGuard Home\" from the application menu to show it again."))
        ok = box.addButton(T("終了する", "Exit"), QMessageBox.ButtonRole.AcceptRole)
        cancel = box.addButton(T("キャンセル", "Cancel"), QMessageBox.ButtonRole.RejectRole)
        box.setDefaultButton(cancel)
        box.exec()
        if box.clickedButton() is ok:
            self.tray.hide()
            self.app.quit()

    def toggle_autostart(self):
        try:
            system.set_autostart(not system.autostart_enabled())
        except OSError as ex:
            QMessageBox.warning(None, "AdGuard Home", T("自動起動を切り替えられませんでした: ", "Could not change autostart: ") + str(ex))
        self._refresh_more()

    # ---------- ログイン情報 ----------

    def edit_login(self):
        user, _ = api.load_credentials()
        dlg = LoginDialog(user)
        if dlg.exec() != QDialog.DialogCode.Accepted:
            return
        name, password = dlg.values()
        if not name:
            return
        test = api.AghApi()
        test.set_credentials(name, password)
        test.base_url = self.dashboard_url()
        try:
            test.get("control/status")
        except api.ApiError as ex:
            msg = T("ユーザー名またはパスワードが違います。", "The user name or password is incorrect.") \
                if ex.status in (401, 403) else str(ex)
            if ex.status in (401, 403) or self.running:
                QMessageBox.warning(None, "AdGuard Home", T("管理画面に接続できませんでした。\n", "Could not connect to the dashboard.\n") + msg)
                return
        api.save_credentials(name, password)
        self.api.set_credentials(name, password)
        self._refresh_more()
        self.notify(T("ログイン情報を保存しました。", "Login info saved."), ms=3000)
        self.refresh_api()

    def delete_login(self):
        api.delete_credentials()
        self.api.set_credentials(None, None)
        self._refresh_more()
        self.refresh_api()

    # ---------- 更新 ----------

    def check_updates(self):
        def work():
            agh = app = None
            try:
                agh = update.latest_adguard_home()
            except Exception:
                pass
            try:
                app = update.latest_release()[0]
            except Exception:
                pass
            return agh, app

        def done(result, _):
            agh, app = result or (None, None)
            notify_new = not self.auto_update
            if agh:
                if notify_new and update.newer(agh, self.agh_version) and agh != self.latest_agh:
                    self.notify(T(f"新しい AdGuard Home {agh} があります。", f"AdGuard Home {agh} is available."), "update")
                self.latest_agh = agh
            if app:
                if notify_new and update.newer(app, VERSION) and app != self.latest_app:
                    self.notify(T(f"このアプリの新しい版 v{app} があります。", f"This app v{app} is available."), "update")
                self.latest_app = app
            self._fill_update()
            self.update_visuals()
            # 自動更新: このアプリは自分で、AdGuard Home は root のタイマーが更新する（タイマーがない場合は API で）
            if self.auto_update and not self.updating and update.newer(self.latest_app, VERSION):
                self.update_now(quiet=True)
            elif self.auto_update and system.auto_update_timer() is None and update.newer(agh, self.agh_version):
                self.update_now(quiet=True)

        self.run_async(work, done)

    def update_now(self, quiet=False):
        if self.updating:
            return
        self.updating = True
        self._fill_update()
        self.quiet_until = time.time() + 600
        timer = system.auto_update_timer()

        def work():
            results = []
            # AdGuard Home 本体
            latest = update.latest_adguard_home()
            if not self.agh_version or update.newer(latest, self.agh_version):
                if timer is not None:
                    p = subprocess.run(system.systemctl_command("start", system.UPDATE_SERVICE),
                                       capture_output=True, text=True, timeout=600)
                    if p.returncode != 0:
                        raise RuntimeError((p.stderr or p.stdout).strip() or "adguard-home-update.service failed")
                    results.append("agh")
                elif self.api.has_credentials:
                    self.api.base_url = self.dashboard_url()
                    info = self.api.post("control/version.json", {"recheck_now": True}) or {}
                    if info.get("new_version") and info.get("can_autoupdate"):
                        self.api.post("control/update", None)
                        results.append("agh")
            # このアプリ
            if update.self_update() == "updated":
                results.append("app")
            return results

        def done(results, error):
            self.updating = False
            self._fill_update()
            if error:
                self.notify(T("更新に失敗しました: ", "Update failed: ") + str(error)[-200:], ms=8000)
            elif results and not quiet:
                self.notify(T("更新処理が完了しました。", "Update finished."), "running")
            elif not results and not quiet:
                self.notify(T("すでに最新バージョンです。", "Already up to date."))
            if results and "app" in results:
                self.restart_self()
                return
            QTimer.singleShot(3000, self.refresh_api)
            QTimer.singleShot(5000, self.check_updates_quietly)

        self.run_async(work, done)

    def check_updates_quietly(self):
        self.latest_agh = self.latest_app = None
        self.check_updates()

    def toggle_auto_update(self):
        on = not self.auto_update
        self.settings["auto_update"] = on
        system.save_settings(self.settings)
        if system.auto_update_timer() is not None:
            def work():
                p = subprocess.run(system.systemctl_command("enable" if on else "disable", system.UPDATE_TIMER),
                                   capture_output=True, text=True, timeout=120)
                if p.returncode != 0:
                    raise RuntimeError((p.stderr or p.stdout).strip())

            def done(_, error):
                if error and "authoriz" not in str(error).lower():
                    self.notify(T("自動更新を切り替えられませんでした: ", "Could not change automatic updates: ") + str(error)[-200:])
                self._fill_update()
                self.update_visuals()

            self.run_async(work, done)
        self._fill_update()
        self.update_visuals()

    def restart_self(self):
        """更新後、新しい版を起動して自分は終わる"""
        subprocess.Popen([system.launcher_path(), "--replace"], start_new_session=True,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.tray.hide()
        self.app.quit()


def _latest_filter_update(status):
    from datetime import datetime
    latest = None
    for f in (status or {}).get("filters") or []:
        if not f.get("enabled"):
            continue
        try:
            t = datetime.fromisoformat(f.get("last_updated", "").replace("Z", "+00:00")).timestamp()
        except ValueError:
            continue
        if t > 946684800 and (latest is None or t > latest):
            latest = t
    return latest


class LoginDialog(QDialog):
    def __init__(self, user):
        super().__init__()
        self.setWindowTitle(T("AdGuard Home - ログイン情報", "AdGuard Home - Login info"))
        self.setWindowIcon(icons.icon("running"))
        layout = QVBoxLayout(self)
        where = T("KDE ウォレットに保存します。", "It is saved in KDE Wallet.") if api._keyring() \
            else T("このユーザーだけが読めるファイルに保存します。", "It is saved in a file only your account can read.")
        layout.addWidget(QLabel(T("管理画面のユーザー名とパスワードを入力してください。\n", "Enter the dashboard user name and password.\n") + where))
        form = QFormLayout()
        self.user = QLineEdit(user or "")
        self.password = QLineEdit()
        self.password.setEchoMode(QLineEdit.EchoMode.Password)
        form.addRow(T("ユーザー名", "User name"), self.user)
        form.addRow(T("パスワード", "Password"), self.password)
        layout.addLayout(form)
        buttons = QDialogButtonBox()
        buttons.addButton(T("保存", "Save"), QDialogButtonBox.ButtonRole.AcceptRole)
        buttons.addButton(T("キャンセル", "Cancel"), QDialogButtonBox.ButtonRole.RejectRole)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        (self.password if user else self.user).setFocus()

    def values(self):
        return self.user.text().strip(), self.password.text()


# ---------- 起動 ----------

def _server_name():
    return f"{APP_ID}-{os.getuid()}"


def main(argv):
    open_now = "--open" in argv
    replace = "--replace" in argv
    if dashboard.AVAILABLE:
        QApplication.setAttribute(Qt.ApplicationAttribute.AA_ShareOpenGLContexts)
    app = QApplication(argv)
    app.setApplicationName("AdGuard Home")   # トレイの項目名（Plasma の「システムトレイの設定」に出る）
    app.setDesktopFileName(APP_ID)
    app.setQuitOnLastWindowClosed(False)

    # 二重起動の防止: 動いているトレイがあれば、管理画面を開くよう頼んで終わる
    sock = QLocalSocket()
    sock.connectToServer(_server_name())
    if sock.waitForConnected(500):
        if replace:
            sock.write(b"quit\n")
            sock.waitForBytesWritten(1000)
            sock.disconnectFromServer()
            time.sleep(1.5)
        else:
            sock.write(b"open\n" if open_now else b"noop\n")
            sock.waitForBytesWritten(1000)
            return 0
    QLocalServer.removeServer(_server_name())
    server = QLocalServer()
    server.listen(_server_name())

    if not QSystemTrayIcon.isSystemTrayAvailable():
        # Plasma の起動直後はトレイがまだないことがある。少し待つ
        for _ in range(30):
            app.processEvents()
            time.sleep(1)
            if QSystemTrayIcon.isSystemTrayAvailable():
                break

    tray = Tray(app)

    def on_connection():
        conn = server.nextPendingConnection()

        def read():
            msg = bytes(conn.readAll()).decode(errors="replace").strip()
            if msg == "open":
                tray.open_dashboard()
            elif msg == "quit":
                tray.tray.hide()
                app.quit()
        conn.readyRead.connect(read)
    server.newConnection.connect(on_connection)

    if open_now:
        QTimer.singleShot(500, tray.open_dashboard)
    return app.exec()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
