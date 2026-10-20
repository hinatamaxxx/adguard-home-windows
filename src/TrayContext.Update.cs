// 更新: AdGuard Home 本体とこのアプリ（トレイ）の版の表示、今すぐ更新、自動更新の失敗通知、更新後の自動再起動。
// 入れ替えは SYSTEM のタスク（manage.ps1）が行うので UAC は出ない。自動更新がオンなら、成功や新しい版は通知しない。
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    partial class TrayContext
    {
        const string TrayReleaseApi = "https://api.github.com/repos/hinatamaxxx/adguard-home-windows/releases/latest";
        const string UpdateNowTask = "AdGuardHome UpdateNow";
        // 起動したときの exe の場所（更新で中身が入れ替わっても、同じ場所の新しい版を見る）
        static readonly string ExePath = Application.ExecutablePath;
        static readonly Version AppVersion = Assembly.GetExecutingAssembly().GetName().Version;
        // 起動時の exe の更新日時とサイズ。入れ替わりはこれで判断する
        // （FileVersionInfo は、読み込み中の exe と同じパスだとメモリ上の古い版の情報を返すため使えない）
        static readonly DateTime ExeStamp = File.GetLastWriteTimeUtc(ExePath);
        static readonly long ExeLength = new FileInfo(ExePath).Length;

        string appLatestVersion = "";
        string notifiedAgh = "", notifiedApp = "";   // 自動更新オフのとき、同じ版は 1 回だけ知らせる
        int notifiedFailures;
        bool updateTaskRunning;

        bool IsAppUpdateAvailable() { return CompareVersions(appLatestVersion, AppVersion.ToString(3)) > 0; }

        // 自動更新がオフのときだけ出す「新しい版があります」（オンならすぐ入れ替わるので出さない）
        string PendingUpdateText()
        {
            if (IsAutoUpdateEnabled()) return null;
            if (IsUpdateAvailable()) return L.T("新しい AdGuard Home " + latestVersion + " があります", "AdGuard Home " + latestVersion + " is available");
            if (IsAppUpdateAvailable()) return L.T("このアプリの新しいバージョン v" + appLatestVersion + " があります", "App v" + appLatestVersion + " is available");
            return null;
        }

        // 「アップデート」の中の版の表示（押せない項目）と「今すぐ確認して更新」
        void UpdateVersionItems()
        {
            string agh = "AdGuard Home" + (currentVersion == "" ? "" : " " + currentVersion);
            if (latestVersion != "" && currentVersion != "")
                agh += IsUpdateAvailable() ? L.T("（" + latestVersion + " があります）", " (" + latestVersion + " available)") : L.T("（最新です）", " (up to date)");
            string app = L.T("このアプリ v", "This app v") + AppVersion.ToString(3);
            if (appLatestVersion != "")
                app += IsAppUpdateAvailable() ? L.T("（v" + appLatestVersion + " があります）", " (v" + appLatestVersion + " available)") : L.T("（最新です）", " (up to date)");
            miAghVersion.Text = agh;
            miAppVersion.Text = app;
            miUpdateNow.Enabled = !updateTaskRunning;
            miUpdateNow.Text = updateTaskRunning ? L.T("更新しています…", "Updating…") : L.T("今すぐ確認して更新", "Check and update now");
        }

        // AdGuard Home と、このアプリ（GitHub Releases）の最新版を調べる
        void CheckUpdateAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string agh = FetchVersion(VersionUrl, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                string app = FetchVersion(TrayReleaseApi, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                Post(delegate
                {
                    if (agh != null) latestVersion = agh;
                    if (app != null) appLatestVersion = app;
                    UpdateVisuals();
                    NotifyAvailableUpdates();
                });
            });
        }

        static string FetchVersion(string url, string pattern)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "AdGuardHomeTray/" + AppVersion.ToString(3);
                    var m = Regex.Match(wc.DownloadString(url), pattern);
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        // 自動更新がオフのときだけ、新しい版を 1 回知らせる（クリックで今すぐ更新）
        void NotifyAvailableUpdates()
        {
            if (IsAutoUpdateEnabled()) return;
            if (IsUpdateAvailable() && notifiedAgh != latestVersion)
            {
                notifiedAgh = latestVersion;
                ShowBalloon(8000, L.T("AdGuard Home の更新", "AdGuard Home update"),
                    L.T("新しい AdGuard Home " + latestVersion + " があります。クリックで更新します。", "AdGuard Home " + latestVersion + " is available. Click to update."),
                    ToolTipIcon.Info, delegate { UpdateNow(); });
            }
            else if (IsAppUpdateAvailable() && notifiedApp != appLatestVersion)
            {
                notifiedApp = appLatestVersion;
                ShowBalloon(8000, L.T("このアプリの更新", "App update"),
                    L.T("このアプリの新しいバージョン v" + appLatestVersion + " があります。クリックで更新します。", "App v" + appLatestVersion + " is available. Click to update."),
                    ToolTipIcon.Info, delegate { UpdateNow(); });
            }
        }

        // 今すぐ確認して更新: SYSTEM のタスクを起動する（利用者にも実行を許可してあるので UAC は出ない）
        void UpdateNow()
        {
            if (updateTaskRunning) return;
            object task = GetUpdateTask();
            if (task == null) { RunManage("Update"); return; }   // タスクのない古いインストールは、従来どおり UAC で
            try { ComCall(task, "Run", (object)null); }
            catch (Exception ex)
            {
                ShowBalloon(5000, "AdGuard Home", L.T("更新を開始できませんでした: ", "Could not start the update: ") + ex.Message, ToolTipIcon.Error);
                return;
            }
            updateTaskRunning = true;
            ownAction = "Update";   // 更新中に AdGuard Home が止まっても「停止しました」の警告を出さない
            ownActionUntil = DateTime.Now.AddMinutes(10);
            UpdateVisuals();
            ThreadPool.QueueUserWorkItem(delegate
            {
                int result = -1;
                var end = DateTime.Now.AddMinutes(15);
                Thread.Sleep(2000);
                while (DateTime.Now < end)
                {
                    try
                    {
                        object t = GetUpdateTask();
                        int state = Convert.ToInt32(ComGet(t, "State"));   // 2 = 待機中, 4 = 実行中
                        if (state != 2 && state != 4) { result = Convert.ToInt32(ComGet(t, "LastTaskResult")); break; }
                    }
                    catch { }
                    Thread.Sleep(1500);
                }
                Post(delegate { OnUpdateNowFinished(result); });
            });
        }

        // 利用者が押した操作なので、結果は知らせる（終了コード: 0=更新した, 2=どちらも最新, それ以外=失敗）
        void OnUpdateNowFinished(int code)
        {
            updateTaskRunning = false;
            RefreshCurrentVersionAsync();
            CheckUpdateAsync();
            if (code == 0) ShowBalloon(5000, "AdGuard Home", L.T("最新版に更新しました。", "Updated to the latest version."), ToolTipIcon.Info, null, 0);
            else if (code == 2) ShowBalloon(4000, "AdGuard Home", L.T("AdGuard Home もこのアプリも最新です。", "AdGuard Home and this app are up to date."), ToolTipIcon.Info);
            else ShowBalloon(6000, "AdGuard Home", L.T("更新に失敗しました。ログ（manage.log）を確認してください。", "Update failed. Check the log (manage.log)."), ToolTipIcon.Error);
            notifiedFailures = SettingInt("UpdateFailures");   // 今知らせたので、自動更新の失敗通知は重ねない
            UpdateVisuals();
            CheckSelfUpdated();
        }

        // 自動更新が続けて失敗しているときだけ、1 回知らせる（黙って古い版のまま止まらないように）
        void CheckUpdateFailures()
        {
            int n = SettingInt("UpdateFailures");
            if (n < notifiedFailures) notifiedFailures = n;   // 成功して 0 に戻った
            if (n >= 2 && notifiedFailures < 2)
            {
                notifiedFailures = n;
                ShowBalloon(8000, L.T("AdGuard Home の自動更新", "AdGuard Home automatic updates"),
                    L.T("自動更新に " + n + " 回続けて失敗しています。インターネット接続を確認するか、「アップデート」→「今すぐ確認して更新」を試してください。",
                        "Automatic updates have failed " + n + " times in a row. Check your internet connection, or try Updates > Check and update now."),
                    ToolTipIcon.Warning);
            }
        }

        // SYSTEM のタスクが exe を新しい版に入れ替えたら、自分で再起動して切り替える
        void CheckSelfUpdated()
        {
            if (InMaintenance()) return;   // 入れ替えの途中
            try
            {
                var fi = new FileInfo(ExePath);
                if (!fi.Exists || (fi.LastWriteTimeUtc == ExeStamp && fi.Length == ExeLength)) return;
            }
            catch { return; }
            foreach (Form f in Application.OpenForms) if (f.Visible) return;   // 開いている画面があれば、閉じるまで待つ
            try { Process.Start(ExePath, "--wait-pid " + Process.GetCurrentProcess().Id); }
            catch { return; }
            ExitTray();
        }

        static int SettingInt(string name)
        {
            int n;
            return int.TryParse(ReadSetting(name), out n) ? n : 0;
        }

        static object GetUpdateTask()
        {
            try
            {
                object svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                ComCall(svc, "Connect");
                return ComCall(ComCall(svc, "GetFolder", "\\"), "GetTask", UpdateNowTask);
            }
            catch { return null; }
        }

        static object ComCall(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        static object ComGet(object o, string name)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
        }
    }
}
