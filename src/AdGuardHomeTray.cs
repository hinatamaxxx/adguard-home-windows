// AdGuard Home 用の通知領域（タスクトレイ）常駐アプリ。
// .NET Framework 4.x 付属の csc.exe (C# 5) でビルドする: build.ps1 を参照。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// AssemblyTitle（FileDescription）と AssemblyCompany は、Windows の「スタートアップ アプリ」に名前と発行元として表示される
[assembly: AssemblyTitle("AdGuard Home Tray")]
[assembly: AssemblyDescription("AdGuard Home を通知領域から管理")]
[assembly: AssemblyCompany("個人用ツール")]
[assembly: AssemblyProduct("AdGuard Home Tray")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace AdGuardHomeTray
{
    static class Program
    {
        // 起動済みのトレイに「管理画面を開いて」と伝えるイベント（--dashboard で起動したとき用）
        internal const string ShowDashboardEvent = "Local\\AdGuardHomeTray_ShowDashboard";

        [STAThread]
        static int Main(string[] args)
        {
            // アンインストールの確認画面だけを出して終了する（uninstall.ps1 から呼ばれる）
            if (Array.Exists(args, a => string.Equals(a, "--uninstall-dialog", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return UninstallDialog.Run(Array.Exists(args, a => string.Equals(a, "--keep", StringComparison.OrdinalIgnoreCase)));
            }
            // セットアップ画面だけを出して終了する（Setup.cmd から呼ばれる）
            if (Array.Exists(args, a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return SetupDialog.Run();
            }
            bool dashboard = Array.Exists(args, a => string.Equals(a, "--dashboard", StringComparison.OrdinalIgnoreCase));
            // 自分自身の再起動（--wait-pid <旧プロセス>）: 旧プロセスの終了を待ってから起動する
            int wi = Array.FindIndex(args, a => string.Equals(a, "--wait-pid", StringComparison.OrdinalIgnoreCase));
            int oldPid;
            if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out oldPid))
            {
                try { using (var old = System.Diagnostics.Process.GetProcessById(oldPid)) old.WaitForExit(15000); } catch { }
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\AdGuardHomeTray_SingleInstance", out created))
            {
                if (!created)
                {
                    if (dashboard)
                    {
                        try { using (var ev = EventWaitHandle.OpenExisting(ShowDashboardEvent)) ev.Set(); } catch { }
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Theme.FollowSystemForMenus();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(dashboard));
                return 0;
            }
        }
    }

    enum IconState { Running, Stopped, Update, Unknown }

    partial class TrayContext : ApplicationContext
    {
        const string ServiceName = "AdGuardHome";
        const string VersionUrl = "https://static.adtidy.org/adguardhome/release/version.json";
        // 自動起動はスタートアップフォルダーのショートカット。アプリはレジストリに書かない
        const string StartupLinkName = "AdGuard Home Tray.lnk";
        // Windows の「設定 → アプリ → スタートアップ」とタスク マネージャーのオン/オフを Windows が保存する場所（読むだけ）
        const string StartupApprovedFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        // 旧版が使っていた Run 方式の登録（起動時にショートカットへ移して削除する）
        const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string LegacyApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string LegacyRunValue = "AdGuardHomeTray";
        // manage.ps1（管理者・SYSTEM）が書き、トレイは読むだけの共有設定（InstallDir, TrayDir, AutoUpdate, Version, WebUrl）
        static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\settings.json");

        readonly string baseDir;
        readonly string aghDir;
        readonly string logDir;
        readonly string managePs1;

        readonly NotifyIcon tray;
        readonly ContextMenu menu;
        readonly MenuItem miStatus, miState, miNewVersion, miOpen, miStart, miStop, miRestart;
        readonly MenuItem miCheck, miUpdateNow, miAutoUpdate, miAutoStart;
        readonly MenuItem miProtection, miDns, miPages, miLogin, miLogout, miExit;
        readonly System.Windows.Forms.Timer statusTimer, updateTimer, apiTimer;
        readonly AghApi api = new AghApi();
        bool apiBusy;
        bool? protectionEnabled;
        string protectionNote = "";    // 例:（あと 10 分）
        string apiReason;              // 保護の状態を取れない理由

        Icon icoRunning, icoStopped, icoUpdate, icoUnknown;
        ServiceControllerStatus? lastStatus;
        bool firstPoll = true;
        string currentVersion = "";
        string latestVersion = "";
        bool notifiedLatest;

        public TrayContext(bool openDashboardOnStart)
        {
            // AdGuard Home の作業フォルダーは本体の権限チェックで管理者専用になるため、
            // トレイアプリは別フォルダーに置き、バージョン等は manage.ps1 が settings.json に書いた値を読む。
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            aghDir = ReadSetting("InstallDir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdGuardHome");
            logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\logs");
            managePs1 = Path.Combine(baseDir, "manage.ps1");
            MigrateLegacyAutoStart();

            icoRunning = MakeIcon(0);
            icoStopped = MakeIcon(1);
            icoUpdate = MakeIcon(2);
            icoUnknown = MakeIcon(3);

            // Windows 標準（Win32）のメニュー。見た目・ダークモード・角丸は OS に任せる
            menu = new ContextMenu();
            // 先頭の状態表示（標準メニューは 1 項目 1 行なので、行ごとに項目を分ける）
            miStatus = new MenuItem("AdGuard Home") { Enabled = false };
            miState = new MenuItem("確認中…") { Enabled = false };
            miNewVersion = new MenuItem("") { Enabled = false, Visible = false };
            miOpen = new MenuItem("管理画面を開く", delegate { OpenDashboard(); });
            miOpen.DefaultItem = true;   // 太字（ダブルクリックと同じ操作）
            miStart = new MenuItem("AdGuard Home を開始", delegate { RunManage("Start"); });
            miStop = new MenuItem("AdGuard Home を停止…", delegate { ConfirmStop(); });
            miRestart = new MenuItem("AdGuard Home を再起動", delegate { RunManage("Restart"); });

            miCheck = new MenuItem("更新を確認", delegate { CheckUpdateAsync(true); });
            miUpdateNow = new MenuItem("今すぐ更新", delegate { RunManage("Update"); });
            miAutoUpdate = new MenuItem("自動更新（毎日）", delegate { ToggleAutoUpdate(); });
            var miUpdate = new MenuItem("アップデート");
            miUpdate.MenuItems.AddRange(new MenuItem[] { miCheck, miUpdateNow, new MenuItem("-"), miAutoUpdate });

            miAutoStart = new MenuItem("Windows 起動時に自動起動", delegate { ToggleAutoStart(); });
            // 終了はメインメニューの最後に常に表示する（見つからないと不安になるため）。
            // AdGuard Home の実行中は誤操作を防ぐため押せなくし、停止中だけ確認画面を経て終了できる
            miExit = new MenuItem("終了…", delegate { ConfirmExit(); });
            var miBrowser = new MenuItem("管理画面をブラウザーで開く", delegate { Shell(GetDashboardUrl(), null); });

            // 保護の一時停止とサービスの開始・停止は「保護とサービス」にまとめる
            miProtection = new MenuItem("保護とサービス");
            miDns = new MenuItem("接続用アドレス（DNS サーバー）");
            miPages = new MenuItem("設定ページを開く");
            foreach (var page in new[] {
                new[] { "ダッシュボード", "" }, new[] { "一般設定", "settings" }, new[] { "DNS 設定", "dns" },
                new[] { "暗号化設定", "encryption" }, new[] { "クライアント設定", "clients" }, new[] { "DHCP 設定", "dhcp" },
                new[] { "-", "" },
                new[] { "DNS ブロックリスト", "filters" }, new[] { "DNS 許可リスト", "allowlists" },
                new[] { "DNS 書き換え", "dns_rewrites" }, new[] { "ブロックするサービス", "blocked_services" },
                new[] { "カスタムフィルタリングルール", "custom_rules" },
                new[] { "-", "" }, new[] { "クエリログ", "logs" } })
            {
                if (page[0] == "-") { miPages.MenuItems.Add(new MenuItem("-")); continue; }
                string hash = page[1];
                miPages.MenuItems.Add(new MenuItem(page[0], delegate { OpenPage(hash); }));
            }
            miLogin = new MenuItem("ログイン情報を設定…", delegate { EditLogin(); });
            miLogout = new MenuItem("保存したログイン情報を削除", delegate { DeleteLogin(); });
            BuildProtectionMenu();

            // よく使う操作だけを上に並べ、たまに使うものは「その他」にまとめる（縦に長くしない）
            var miMore = new MenuItem("その他");
            miMore.MenuItems.AddRange(new MenuItem[] {
                miDns, miPages, miBrowser, new MenuItem("-"),
                miLogin, miLogout, miAutoStart });
            menu.MenuItems.AddRange(new MenuItem[] {
                miStatus, miState, miNewVersion, new MenuItem("-"),
                miOpen, miProtection, miUpdate, miMore, new MenuItem("-"), miExit });
            menu.Popup += delegate
            {
                Theme.RefreshMenus(); BuildProtectionMenu(); RefreshMenuChecks(); BuildAddressMenu(); RefreshApiAsync();
            };

            tray = new NotifyIcon();
            tray.Icon = icoUnknown;
            tray.Text = "AdGuard Home";
            tray.ContextMenu = menu;
            tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenDashboard(); };
            tray.BalloonTipClicked += delegate
            {
                // ShowBalloon で操作を付けた通知だけ、クリックでその操作を行う
                if (balloonAction != null && tray.BalloonTipText == balloonActionText) balloonAction();
            };
            tray.Visible = true;
            InitMenuHover();

            statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            statusTimer.Tick += delegate { PollStatus(); };
            statusTimer.Start();

            updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
            updateTimer.Tick += delegate { CheckUpdateAsync(false); };
            updateTimer.Start();

            apiTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            apiTimer.Tick += delegate { bool svgChanged = File.Exists(LogoSvgFile) ? File.GetLastWriteTimeUtc(LogoSvgFile) != logoSvgStamp : logoSvgStamp != DateTime.MinValue; if (RefreshLogo() | svgChanged) RebuildIcons(); RefreshApiAsync(); FetchFilterStatus(); RefreshCurrentVersionAsync(); };
            apiTimer.Start();

            PollStatus();
            RefreshCurrentVersionAsync();
            RefreshApiAsync();
            CheckUpdateAsync(false);

            // 2 つ目の起動（--dashboard）から合図を受けたら管理画面を開く
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowDashboardEvent);
            ThreadPool.RegisterWaitForSingleObject(showEvent, delegate { Post(delegate { OpenDashboard(); }); }, null, -1, false);
            if (openDashboardOnStart) Post(delegate { OpenDashboard(); });
        }

        // ---------- 状態表示 ----------

        void PollStatus()
        {
            ServiceControllerStatus? st = null;
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    st = sc.Status;
                }
            }
            catch (InvalidOperationException) { /* サービス未登録 */ }

            bool changed = !st.Equals(lastStatus);
            CheckUnexpectedStop(st);
            if (changed && !firstPoll && st == ServiceControllerStatus.Running && lastStatus != ServiceControllerStatus.Running)
            {
                RefreshCurrentVersionAsync();
            }
            lastStatus = st;
            firstPoll = false;
            UpdateVisuals();
        }

        // 「停止しました」の警告は、思いがけず止まったときだけ出す。
        //   - 更新・再起動の最中（manage.ps1 が maintenance.json を置いている間、またはトレイから操作した直後）は出さない
        //   - トレイの「停止」で止めたときは出さない
        //   - 一瞬止まってすぐ戻る場合に出さないよう、停止が 10 秒以上続いてから出す
        // 停止処理中（StopPending）を挟んでも見逃さないよう、「動いていたか」を覚えておく
        static readonly string MaintenanceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\maintenance.json");
        bool wasRunning;
        DateTime? stoppedSince;
        string ownAction;
        DateTime ownActionUntil = DateTime.MinValue;

        static bool InMaintenance()
        {
            try
            {
                var fi = new FileInfo(MaintenanceFile);
                return fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes < 15;   // 古い印は無視する
            }
            catch { return false; }
        }

        bool OwnActionActive(params string[] actions)
        {
            return DateTime.Now < ownActionUntil && Array.IndexOf(actions, ownAction) >= 0;
        }

        void CheckUnexpectedStop(ServiceControllerStatus? st)
        {
            if (st == ServiceControllerStatus.Running) { wasRunning = true; stoppedSince = null; return; }
            if (st != ServiceControllerStatus.Stopped) return;   // 停止処理中・開始処理中・未登録は判断しない
            if (wasRunning)
            {
                wasRunning = false;
                // トレイから「停止」した場合は、利用者の操作なので警告しない
                stoppedSince = OwnActionActive("Stop") ? (DateTime?)null : DateTime.Now;
            }
            if (stoppedSince == null) return;
            if (InMaintenance() || OwnActionActive("Restart", "Update")) { stoppedSince = DateTime.Now; return; }
            if ((DateTime.Now - stoppedSince.Value).TotalSeconds < 10) return;
            stoppedSince = null;
            tray.ShowBalloonTip(5000, "AdGuard Home", "AdGuard Home が停止しました。", ToolTipIcon.Warning);
        }

        void UpdateVisuals()
        {
            string text;
            Icon icon;
            bool running = lastStatus == ServiceControllerStatus.Running;
            if (lastStatus == null) { text = "未登録"; icon = icoUnknown; }
            else if (running) { text = "実行中"; icon = icoRunning; }
            else if (lastStatus == ServiceControllerStatus.Stopped) { text = "停止中"; icon = icoStopped; }
            else { text = StatusText(lastStatus.Value); icon = icoStopped; }

            bool upd = IsUpdateAvailable();
            if (running && protectionEnabled == false) { icon = icoStopped; text = "実行中（保護オフ）"; }
            else if (upd && running) icon = icoUpdate;

            // 先頭の状態表示: ツールチップと同じく「名前とバージョン」「保護・動作の状態」
            string state;
            if (lastStatus == null) state = "サービスが登録されていません";
            else if (!running) state = lastStatus == ServiceControllerStatus.Stopped ? "停止しています" : StatusText(lastStatus.Value);
            else if (protectionEnabled == false) state = "保護は無効です" + protectionNote;
            else if (protectionEnabled == true) state = "保護は有効です";
            else state = "動作中";
            miStatus.Text = "AdGuard Home" + (currentVersion == "" ? "" : " " + currentVersion);
            miState.Text = state;
            miNewVersion.Text = upd ? "新しいバージョン " + latestVersion + " があります" : "";
            miNewVersion.Visible = upd;
            miStart.Enabled = lastStatus != null && !running;
            miStop.Enabled = running;
            miRestart.Enabled = running;
            miUpdateNow.Text = upd ? "今すぐ " + latestVersion + " に更新" : "今すぐ更新";
            miExit.Enabled = CanExit;
            miExit.Text = CanExit ? "終了…" : "終了（AdGuard Home の停止中のみ）";
            UpdateTooltip();
            if (tray.Icon != icon) tray.Icon = icon;
        }

        static string StatusText(ServiceControllerStatus s)
        {
            switch (s)
            {
                case ServiceControllerStatus.StartPending: return "開始中";
                case ServiceControllerStatus.StopPending: return "停止中…";
                case ServiceControllerStatus.Paused: return "一時停止";
                default: return s.ToString();
            }
        }

        void RefreshMenuChecks()
        {
            miAutoStart.Checked = IsAutoStartEnabled();
            miAutoUpdate.Checked = IsAutoUpdateEnabled();
            miLogin.Text = api.HasCredentials ? "ログイン情報を変更…" : "ログイン情報を設定…";
            miLogout.Visible = api.HasCredentials;
        }

        // ---------- バージョン / 更新 ----------

        bool IsUpdateAvailable()
        {
            return CompareVersions(latestVersion, currentVersion) > 0;
        }

        static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (!TryParseVersion(a, out va) || !TryParseVersion(b, out vb)) return 0;
            return va.CompareTo(vb);
        }

        static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            var m = Regex.Match(s, @"(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return false;
            v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            return true;
        }

        // manage.ps1（起動時・ログオン時・毎日のタスク、更新後）が記録したバージョン
        void RefreshCurrentVersionAsync()
        {
            string v = ReadSetting("Version") ?? "";
            if (v != currentVersion && currentVersion != "") notifiedLatest = false;   // 更新されたら、次の新しい版はまた知らせる
            currentVersion = v;
            UpdateVisuals();
        }

        internal static string ReadSetting(string name)
        {
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                object v;
                string s = d != null && d.TryGetValue(name, out v) && v != null ? Convert.ToString(v) : null;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        void CheckUpdateAsync(bool interactive)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string latest = null;
                string error = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "AdGuardHomeTray/1.0";
                        string json = wc.DownloadString(VersionUrl);
                        var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success) latest = m.Groups[1].Value;
                    }
                }
                catch (Exception ex) { error = ex.Message; }

                Post(delegate
                {
                    if (latest != null) latestVersion = latest;
                    UpdateVisuals();
                    if (error != null)
                    {
                        if (interactive) tray.ShowBalloonTip(5000, "AdGuard Home", "更新の確認に失敗しました: " + error, ToolTipIcon.Error);
                        return;
                    }
                    if (IsUpdateAvailable())
                    {
                        if (interactive || !notifiedLatest)
                        {
                            string msg = IsAutoUpdateEnabled()
                                ? "新しいバージョン " + latestVersion + " があります。自動更新で適用されます。クリックすると今すぐ更新します。"
                                : "新しいバージョン " + latestVersion + " があります。クリックで更新します。";
                            ShowBalloon(8000, "AdGuard Home の更新", msg, ToolTipIcon.Info, delegate { RunManage("Update"); });
                            notifiedLatest = true;
                        }
                    }
                    else if (interactive)
                    {
                        tray.ShowBalloonTip(4000, "AdGuard Home", "最新バージョンです（" + (currentVersion == "" ? latestVersion : currentVersion) + "）。", ToolTipIcon.Info);
                    }
                });
            });
        }

        // ---------- 操作 ----------

        // 管理画面は専用ウィンドウ（WebView2）で開く。使えなければ既定のブラウザー
        void OpenDashboard()
        {
            DashboardForm.ShowPage(GetDashboardUrl(), u => Shell(u, null), OnDashboardLogin);
        }

        // 実行中のサービスが LISTEN している TCP ポートから管理画面を探す。
        // 見つからなければ manage.ps1 が設定ファイルから記録した URL、最後に初期設定用の 3000 番。
        string GetDashboardUrl()
        {
            string live = FindListeningWebUrl();
            if (live != null) return live;
            return ReadSetting("WebUrl") ?? "http://127.0.0.1:3000/";
        }

        string FindListeningWebUrl()
        {
            try
            {
                int pid = GetServicePid();
                if (pid <= 0) return null;
                var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                string output;
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                }
                string best = null;
                int bestScore = -1;
                foreach (Match m in Regex.Matches(output, @"(?m)^\s*TCP\s+(\S+):(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$"))
                {
                    if (int.Parse(m.Groups[3].Value) != pid) continue;
                    int port = int.Parse(m.Groups[2].Value);
                    if (port == 53 || port == 853) continue; // DNS / DoT
                    int score = port == 3000 ? 3 : port == 80 ? 2 : port == 443 ? 0 : 1;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        string host = m.Groups[1].Value;
                        if (host == "0.0.0.0" || host.StartsWith("[")) host = "127.0.0.1";
                        string scheme = port == 443 ? "https" : "http";
                        best = scheme + "://" + host + ((port == 80 || port == 443) ? "" : ":" + port) + "/";
                    }
                }
                return best;
            }
            catch { return null; }
        }

        static int GetServicePid()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName)) { if (k == null) return 0; }
                var psi = new ProcessStartInfo("sc.exe", "queryex " + ServiceName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var m = Regex.Match(output, @"PID\s*:\s*(\d+)");
                    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
                }
            }
            catch { return 0; }
        }


        // 管理者権限が必要な操作は manage.ps1 を UAC 昇格で実行する（ウィンドウは表示しない）。
        void RunManage(string action)
        {
            if (!File.Exists(managePs1))
            {
                MessageBox.Show("manage.ps1 が見つかりません: " + managePs1, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + managePs1 + "\" -Action " + action)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = baseDir
            };
            try
            {
                var p = Process.Start(psi);
                // 自分で止める・再起動する・更新する操作の直後は、「停止しました」の警告を出さない
                if (action == "Stop" || action == "Restart" || action == "Update")
                {
                    ownAction = action;
                    ownActionUntil = DateTime.Now.AddMinutes(action == "Update" ? 10 : 2);
                }
                if (action == "Update" || action.EndsWith("AutoUpdate"))
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { p.WaitForExit(); } catch { }
                        int code = -1;
                        try { code = p.ExitCode; } catch { }
                        Post(delegate { OnManageFinished(action, code); });
                    });
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // UAC でキャンセルされた
            }
        }

        void OnManageFinished(string action, int code)
        {
            if (action == "Update")
            {
                if (code == 0) tray.ShowBalloonTip(5000, "AdGuard Home", "更新処理が完了しました。", ToolTipIcon.Info);
                else if (code == 2) tray.ShowBalloonTip(5000, "AdGuard Home", "すでに最新バージョンです。", ToolTipIcon.Info);
                else tray.ShowBalloonTip(5000, "AdGuard Home", "更新に失敗しました。ログ（manage.log）を確認してください。", ToolTipIcon.Error);
                notifiedLatest = false;
                RefreshCurrentVersionAsync();
            }
            RefreshMenuChecks();
        }

        bool IsAutoUpdateEnabled()
        {
            try
            {
                string v = ReadSetting("AutoUpdate");
                return v != null && Convert.ToInt32(v) != 0;
            }
            catch { return false; }
        }

        void ToggleAutoUpdate()
        {
            RunManage(IsAutoUpdateEnabled() ? "DisableAutoUpdate" : "EnableAutoUpdate");
        }

        // 自動起動はスタートアップフォルダーのショートカット。オン/オフは Windows のスタートアップ設定と共有し、
        // Windows が書く StartupApproved\StartupFolder の値は読むだけにする。
        internal static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLinkName); }
        }

        // Windows 側のオン/オフ。値なし、または先頭バイトが偶数（02/06）ならオン、奇数（03 など）ならオフ
        internal static bool IsApprovedByWindows()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(StartupApprovedFolderKey))
                {
                    var data = k == null ? null : k.GetValue(StartupLinkName) as byte[];
                    return data == null || data.Length == 0 || (data[0] & 1) == 0;
                }
            }
            catch { return true; }
        }

        bool IsAutoStartEnabled() { return IsStartupLinkEnabled(); }

        internal static bool IsStartupLinkEnabled()
        {
            return File.Exists(StartupLink) && IsApprovedByWindows();
        }

        internal static void RemoveStartupLink()
        {
            if (File.Exists(StartupLink)) File.Delete(StartupLink);
        }

        static void CreateStartupLink() { CreateStartupLink(Application.ExecutablePath); }

        internal static void CreateStartupLink(string exe)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartupLink });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "AdGuard Home を通知領域から管理" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        void ToggleAutoStart()
        {
            try
            {
                if (File.Exists(StartupLink) && IsApprovedByWindows())
                {
                    File.Delete(StartupLink);
                }
                else
                {
                    CreateStartupLink();   // 既存でもターゲットを現在の exe で作り直す
                    if (!IsApprovedByWindows())
                    {
                        MessageBox.Show("Windows のスタートアップ設定でオフになっています。\n開いた設定画面で「AdGuard Home Tray」をオンにしてください。",
                            "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Shell("ms-settings:startupapps", null);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("自動起動を切り替えられませんでした: " + ex.Message, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshMenuChecks();
        }

        // 旧版の Run 方式から移行する: オンだったときだけショートカットを作り、旧版が書いた値を削除する
        static void MigrateLegacyAutoStart()
        {
            try
            {
                bool hadRun, enabled;
                using (var run = Registry.CurrentUser.OpenSubKey(LegacyRunKey))
                    hadRun = run != null && run.GetValue(LegacyRunValue) != null;
                byte[] data;
                using (var approved = Registry.CurrentUser.OpenSubKey(LegacyApprovedKey))
                    data = approved == null ? null : approved.GetValue(LegacyRunValue) as byte[];
                if (!hadRun && data == null) return;
                enabled = hadRun && (data == null || data.Length == 0 || (data[0] & 1) == 0);
                if (enabled && !File.Exists(StartupLink)) CreateStartupLink();
                using (var run = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true))
                    if (run != null) run.DeleteValue(LegacyRunValue, false);
                using (var approved = Registry.CurrentUser.OpenSubKey(LegacyApprovedKey, true))
                    if (approved != null) approved.DeleteValue(LegacyRunValue, false);
            }
            catch { }   // 失敗したら旧登録を残し、次回の起動で再試行する
        }

        // 停止すると、この PC を DNS に使っている端末（iPhone など）がつながらなくなるので、確認してから止める
        void ConfirmStop()
        {
            using (var d = new StopDialog())
            {
                if (d.ShowDialog() != DialogResult.OK) return;
            }
            RunManage("Stop");
        }

        // 終了できるのは AdGuard Home が停止しているとき（サービス未登録を含む）だけ
        bool CanExit { get { return lastStatus == null || lastStatus == ServiceControllerStatus.Stopped; } }

        void ConfirmExit()
        {
            if (!CanExit) return;
            using (var d = new ExitDialog())
            {
                if (d.ShowDialog() != DialogResult.OK || !CanExit) return;
            }
            ExitTray();
        }

        void ExitTray()
        {
            tray.Visible = false;
            statusTimer.Stop();
            updateTimer.Stop();
            apiTimer.Stop();
            ExitThread();
        }

        static void Shell(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // UI スレッドへ処理を戻す。メニューはコントロールではないため、専用の非表示コントロールを使う
        readonly Control invoker = CreateInvoker();

        static Control CreateInvoker()
        {
            var c = new Control();
            var h = c.Handle; // UI スレッドでハンドルを作っておく
            return c;
        }

        void Post(MethodInvoker action)
        {
            try { invoker.BeginInvoke(action); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tray.Dispose();
                menu.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- 管理 API（保護・統計・DNS 設定） ----------

        // 管理画面ウィンドウで入力したログイン情報を保存したら、トレイの表示（保護・統計）にもすぐ使う
        void OnDashboardLogin(string user, string pass)
        {
            api.SetCredentials(user, pass);
            RefreshMenuChecks();
            RefreshApiAsync();
        }

        void OpenPage(string hash)
        {
            DashboardForm.ShowPage(GetDashboardUrl() + (hash == "" ? "" : "#" + hash), u => Shell(u, null), OnDashboardLogin);
        }

        void RefreshApiAsync()
        {
            if (apiBusy) return;
            if (lastStatus != ServiceControllerStatus.Running)
            {
                ShowApiUnavailable("サービスが停止しています");
                return;
            }
            if (!api.HasCredentials)
            {
                ShowApiUnavailable("ログイン情報を設定すると表示されます");
                return;
            }
            apiBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, object> status = null;
                string error = null;
                try
                {
                    api.BaseUrl = GetDashboardUrl();
                    status = api.Get("control/status");
                }
                catch (ApiException ex)
                {
                    error = (ex.Status == 401 || ex.Status == 403) ? "ログイン情報が正しくありません" : "取得できません（" + ex.Message + "）";
                }
                catch (Exception ex) { error = "取得できません（" + ex.Message + "）"; }

                Post(delegate
                {
                    apiBusy = false;
                    if (error != null) { ShowApiUnavailable(error); return; }
                    ApplyApiResult(status);
                });
            });
        }

        void ShowApiUnavailable(string reason)
        {
            protectionEnabled = null;
            protectionNote = "";
            apiReason = reason;
            BuildProtectionMenu();
            UpdateVisuals();
        }

        void ApplyApiResult(Dictionary<string, object> status)
        {
            FetchFilterStatus();
            protectionEnabled = AghApi.Bool(status, "protection_enabled");
            long ms = protectionEnabled == true ? 0 : AghApi.Num(status, "protection_disabled_duration");
            protectionNote = ms > 0 ? "（あと " + FormatDuration(ms) + "）" : "";
            apiReason = null;

            string ver = AghApi.Str(status, "version");
            if (!string.IsNullOrEmpty(ver)) currentVersion = ver;

            BuildProtectionMenu();
            UpdateVisuals();
        }

        static string FormatDuration(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalDays >= 1 && ts.TotalHours % 24 == 0) return ((int)ts.TotalDays) + " 日間";
            if (ts.TotalHours >= 1) return ((int)Math.Round(ts.TotalHours)) + " 時間";
            if (ts.TotalMinutes >= 1) return ((int)Math.Ceiling(ts.TotalMinutes)) + " 分";
            return ((int)Math.Ceiling(ts.TotalSeconds)) + " 秒";
        }

        void BuildProtectionMenu()
        {
            var items = miProtection.MenuItems;
            items.Clear();
            bool running = lastStatus == ServiceControllerStatus.Running;
            if (!running)
            {
                // 止まっているときは保護の操作はできないので、サービスの操作だけ
            }
            else if (protectionEnabled == true)
            {
                foreach (var opt in new[] { new object[] { "1 分間 無効にする", 60000L }, new object[] { "10 分間 無効にする", 600000L },
                                            new object[] { "1 時間 無効にする", 3600000L }, new object[] { "8 時間 無効にする", 28800000L },
                                            new object[] { "有効にするまで無効にする", 0L } })
                {
                    long ms = (long)opt[1];
                    items.Add(new MenuItem((string)opt[0], delegate { SetProtection(false, ms); }));
                }
            }
            else if (protectionEnabled == false)
            {
                items.Add(new MenuItem("保護を有効にする", delegate { SetProtection(true, 0); }));
            }
            else
            {
                if (!string.IsNullOrEmpty(apiReason)) items.Add(new MenuItem("保護の状態: " + apiReason) { Enabled = false });
                items.Add(new MenuItem("ログイン情報を設定…", delegate { EditLogin(); }));
            }
            if (items.Count > 0) items.Add(new MenuItem("-"));
            miStart.Enabled = lastStatus != null && !running;
            miStop.Enabled = running;
            miRestart.Enabled = running;
            items.Add(running ? miRestart : miStart);
            if (running) items.Add(miStop);
        }

        void SetProtection(bool enabled, long durationMs)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    var body = new Dictionary<string, object> { { "enabled", enabled } };
                    if (!enabled && durationMs > 0) body["duration"] = durationMs;
                    try { api.Post("control/protection", body); }
                    catch (ApiException ex)
                    {
                        if (ex.Status != 404 && ex.Status != 405) throw;
                        api.Post("control/dns_config", new Dictionary<string, object> { { "protection_enabled", enabled } });
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (error != null)
                        tray.ShowBalloonTip(5000, "AdGuard Home", "保護の切り替えに失敗しました: " + error, ToolTipIcon.Error);
                    else
                        tray.ShowBalloonTip(3000, "AdGuard Home", enabled ? "保護を有効にしました。"
                            : "保護を無効にしました" + (durationMs > 0 ? "（" + FormatDuration(durationMs) + "後に再開）。" : "。"), ToolTipIcon.Info);
                    RefreshApiAsync();
                });
            });
        }

        // 他の機器（やこのPC）の DNS サーバー欄に入れるアドレスを、実際の待ち受けから求める
        void BuildAddressMenu()
        {
            var items = miDns.MenuItems;
            items.Clear();
            int pid = lastStatus == ServiceControllerStatus.Running ? GetServicePid() : 0;
            if (pid <= 0)
            {
                AddInfo(items, "AdGuard Home が動いていません");
                return;
            }

            // AdGuard Home がポート 53 で待ち受けているアドレス
            var bound = new List<string>();
            bool dot = false;
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                string output;
                using (var p = Process.Start(psi)) { output = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
                foreach (Match m in Regex.Matches(output, @"(?m)^\s*(TCP|UDP)\s+(\S+):(\d+)\s+.*?\s(\d+)\s*$"))
                {
                    if (int.Parse(m.Groups[4].Value) != pid) continue;
                    string host = m.Groups[2].Value.Trim('[', ']');
                    int port = int.Parse(m.Groups[3].Value);
                    if (port == 853) dot = true;
                    if (port == 53 && m.Groups[1].Value == "UDP" && !bound.Contains(host)) bound.Add(host);
                }
            }
            catch { }
            if (bound.Count == 0)
            {
                AddInfo(items, "DNS の待ち受けが見つかりません\n（初期設定前かもしれません）");
                return;
            }
            bool any4 = bound.Contains("0.0.0.0"), any6 = bound.Contains("::");

            Func<System.Net.IPAddress, bool> isBound = delegate(System.Net.IPAddress a)
            {
                bool v6 = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                return (v6 ? any6 : any4) || bound.Contains(a.ToString());
            };

            // このPC
            AddHeader(items, "このPCで使う場合");
            if (isBound(System.Net.IPAddress.Loopback)) AddValue(items, "127.0.0.1", "127.0.0.1");
            if (isBound(System.Net.IPAddress.IPv6Loopback)) AddValue(items, "::1", "::1");

            // ネットワーク別
            var lan = new List<string[]>();
            var vpn = new List<string[]>();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                // WSL / Hyper-V の仮想スイッチは他の機器から使わないので除外
                if (nic.Name.StartsWith("vEthernet") || nic.Description.Contains("Hyper-V")) continue;
                bool isVpn = nic.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0
                          || nic.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0
                          || nic.Description.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.IsIPv6LinkLocal || a.IsIPv6Multicast) continue;
                    string s = a.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    if (!isBound(a)) continue;
                    // IPv6 は一時アドレスを除き、IPv4 を先に並べる
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        if (ua.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.Random) continue;
                    }
                    var entry = new[] { s, nic.Name, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "0" : "1" };
                    (isVpn ? vpn : lan).Add(entry);
                }
            }
            Comparison<string[]> v4First = delegate(string[] x, string[] y) { return string.CompareOrdinal(x[2], y[2]); };
            lan.Sort(v4First);
            vpn.Sort(v4First);

            if (lan.Count > 0)
            {
                AddHeader(items, "同じネットワークの機器から使う場合");
                foreach (var e in lan) AddValue(items, Shown(e[0], false) + "（" + e[1] + "）", e[0]);
            }
            if (vpn.Count > 0)
            {
                AddHeader(items, "Tailscale 経由で使う場合");
                foreach (var e in vpn) AddValue(items, Shown(e[0], true) + "（" + e[1] + "）", e[0]);
            }
            if (dot)
            {
                AddHeader(items, "暗号化 DNS");
                AddInfo(items, "    DNS-over-TLS（ポート 853）が有効です\n    サーバー名は暗号化設定で確認してください");
            }

            if ((lan.Count > 0 || vpn.Count > 0) && !HasFirewallAllowRule())
            {
                items.Add(new MenuItem("-"));
                AddInfo(items, "⚠ ファイアウォールで許可されていないため、\n    今は他の機器から接続できません");
            }
            items.Add(new MenuItem("-"));
            AddInfo(items, "項目をクリックするとアドレスをコピーします");
        }

        // 接続用アドレスの表示。スクリーンショット撮影用のビルド（build.ps1 -Screenshot、SCREENSHOT_BUILD）だけは、
        // 環境変数 ADGUARD_TRAY_DEMO=1 のとき表示を書式見本に置き換える。通常のビルド（配布物）にはこの処理は入らない。
#if SCREENSHOT_BUILD
        static int demoCount;
#endif
        static string Shown(string ip, bool vpn)
        {
#if SCREENSHOT_BUILD
            if (Environment.GetEnvironmentVariable("ADGUARD_TRAY_DEMO") == "1")
            {
                demoCount++;
                bool v6 = ip.Contains(":");
                if (vpn) return v6 ? "fd7a:115c:a1e0::" + demoCount.ToString("x") : "100.64.0." + (10 + demoCount);
                return v6 ? "2001:db8::" + demoCount.ToString("x") : "192.168.1." + (10 + demoCount);
            }
#endif
            return ip;
        }
        // Windows ファイアウォールに AdGuardHome.exe（またはポート 53）の受信許可規則があるか
        static bool HasFirewallAllowRule()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
                {
                    if (k == null) return true;
                    foreach (string name in k.GetValueNames())
                    {
                        string v = k.GetValue(name) as string;
                        if (v == null) continue;
                        if (v.IndexOf("|Action=Allow|", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (v.IndexOf("|Active=TRUE|", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (v.IndexOf("|Dir=In|", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (v.IndexOf("AdGuardHome.exe", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (Regex.IsMatch(v, @"\|LPort=53\|") && v.IndexOf("|App=", StringComparison.OrdinalIgnoreCase) < 0) return true;
                    }
                }
            }
            catch { return true; }
            return false;
        }
        static void AddHeader(Menu.MenuItemCollection items, string text)
        {
            var h = new MenuItem(text) { Enabled = false };
            items.Add(h);
        }

        static void AddInfo(Menu.MenuItemCollection items, string text)
        {
            // 標準メニューは改行を表示できないので、行ごとに項目を分ける
            foreach (string line in text.Split('\n'))
                items.Add(new MenuItem(line) { Enabled = false });
        }

        // クリックで値をクリップボードにコピーできる項目
        static void AddValue(Menu.MenuItemCollection items, string text, string copy)
        {
            var mi = new MenuItem("    " + text, delegate
            {
                try { Clipboard.SetText(copy); } catch { }
            });
            items.Add(mi);
        }

        static void AddList(Menu.MenuItemCollection items, List<string> values)
        {
            if (values.Count == 0) { AddInfo(items, "    （なし）"); return; }
            foreach (string v in values) AddValue(items, v, v);
        }

        void EditLogin()
        {
            string curUser, curPass;
            AghApi.LoadCredentials(out curUser, out curPass);
            using (var form = new LoginForm(curUser))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                if (form.UserName == "" || form.Password == "") return;

                // 保存前に接続を確かめる
                var test = new AghApi();
                test.SetCredentials(form.UserName, form.Password);
                string error = null;
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    test.BaseUrl = GetDashboardUrl();
                    test.Get("control/status");
                }
                catch (ApiException ex)
                {
                    error = (ex.Status == 401 || ex.Status == 403) ? "ユーザー名またはパスワードが違います。" : ex.Message;
                }
                catch (Exception ex) { error = ex.Message; }
                finally { Cursor.Current = Cursors.Default; }

                if (error != null)
                {
                    MessageBox.Show("管理画面に接続できませんでした。\n" + error, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                AghApi.SaveCredentials(form.UserName, form.Password);
                api.SetCredentials(form.UserName, form.Password);
                tray.ShowBalloonTip(3000, "AdGuard Home", "ログイン情報を保存しました。", ToolTipIcon.Info);
                RefreshMenuChecks();
                RefreshApiAsync();
            }
        }

        void DeleteLogin()
        {
            AghApi.DeleteCredentials();
            api.SetCredentials(null, null);
            RefreshMenuChecks();
            RefreshApiAsync();
        }
        // ---------- アイコン描画 ----------
        // 本物の AdGuard Home ロゴ（管理画面の apple-touch-icon、埋め込みリソース）に、
        // AdGuard for Windows と区別するための「家」バッジを右下に重ねる。
        // state: 0=実行中, 1=停止中, 2=更新あり, 3=未登録

        // AdGuard のロゴ（商標）はリポジトリに含めず、インストールした AdGuard Home の管理画面から
        // manage.ps1 が取得した画像（%ProgramData%\AdGuardHome\logo.png）を使う。ない間は独自の盾アイコンを描く。
        static readonly string LogoFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\logo.png");
        static Bitmap logo;
        static DateTime logoStamp = DateTime.MinValue;

        // ロゴ画像が現れた・変わったら true（アイコンを作り直すため）
        static bool RefreshLogo()
        {
            DateTime stamp = File.Exists(LogoFile) ? File.GetLastWriteTimeUtc(LogoFile) : DateTime.MinValue;
            if (stamp == logoStamp && logo != null) return false;
            bool changed = stamp != logoStamp;
            logoStamp = stamp;
            logo = null;
            return changed;
        }

        static Bitmap Logo()
        {
            if (logo != null) return logo;
            logoStamp = File.Exists(LogoFile) ? File.GetLastWriteTimeUtc(LogoFile) : DateTime.MinValue;
            byte[] data = null;
            try { if (File.Exists(LogoFile)) data = File.ReadAllBytes(LogoFile); } catch { }
            if (data == null) { logo = FallbackShield(); return logo; }
            using (var s = new MemoryStream(data))
            using (var raw = new Bitmap(s))
            using (var src = new Bitmap(raw.Width, raw.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g0 = Graphics.FromImage(src)) g0.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                // 元画像は白背景なので、外周から連続する白を透明にする（盾の中のチェックの白は残す）
                var seen = new bool[src.Width, src.Height];
                var stack = new System.Collections.Generic.Stack<Point>();
                for (int x = 0; x < src.Width; x++) { stack.Push(new Point(x, 0)); stack.Push(new Point(x, src.Height - 1)); }
                for (int y = 0; y < src.Height; y++) { stack.Push(new Point(0, y)); stack.Push(new Point(src.Width - 1, y)); }
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    if (p.X < 0 || p.Y < 0 || p.X >= src.Width || p.Y >= src.Height || seen[p.X, p.Y]) continue;
                    seen[p.X, p.Y] = true;
                    Color c = src.GetPixel(p.X, p.Y);
                    int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mn < 200) continue;
                    // 縁のにじみは白さに応じて半透明に
                    int a = mn >= 250 ? 0 : (int)(255 * (250 - mn) / 50.0);
                    src.SetPixel(p.X, p.Y, Color.FromArgb(a, 0x67, 0xB2, 0x79));
                    if (mn >= 235)
                    {
                        stack.Push(new Point(p.X + 1, p.Y)); stack.Push(new Point(p.X - 1, p.Y));
                        stack.Push(new Point(p.X, p.Y + 1)); stack.Push(new Point(p.X, p.Y - 1));
                    }
                }
                // 透明な余白を切り取り、盾をアイコンいっぱいに描けるようにする
                int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
                for (int y = 0; y < src.Height; y++)
                    for (int x = 0; x < src.Width; x++)
                        if (src.GetPixel(x, y).A > 16)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                if (maxX < 0) { logo = new Bitmap(src); return logo; }
                int w = maxX - minX + 1, h = maxY - minY + 1, side = Math.Max(w, h);
                logo = new Bitmap(side, side);
                using (var g = Graphics.FromImage(logo))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle((side - w) / 2, (side - h) / 2, w, h),
                        new Rectangle(minX, minY, w, h), GraphicsUnit.Pixel);
                }
            }
            return logo;
        }

        // AdGuard Home の管理画面から取得したロゴの図形（safari-pinned-tab.svg、16x16 の座標）。
        // 1 つ目の図形が盾、2 つ目がチェック。どの大きさでもくっきり描ける。ない間は null。
        static readonly string LogoSvgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\logo.svg");
        static GraphicsPath[] logoShapes;
        static DateTime logoSvgStamp = DateTime.MinValue;

        static GraphicsPath[] LogoShapes()
        {
            DateTime stamp = File.Exists(LogoSvgFile) ? File.GetLastWriteTimeUtc(LogoSvgFile) : DateTime.MinValue;
            if (stamp == logoSvgStamp) return logoShapes;
            logoSvgStamp = stamp;
            logoShapes = null;
            try
            {
                if (stamp == DateTime.MinValue) return null;
                var m = Regex.Match(File.ReadAllText(LogoSvgFile), "\\sd=\"([^\"]+)\"");
                if (!m.Success) return null;
                var shapes = ParseSvgPath(m.Groups[1].Value);
                if (shapes.Count >= 2) logoShapes = shapes.ToArray();
            }
            catch { logoShapes = null; }
            return logoShapes;
        }

        // M / L / C / Z（絶対座標）だけの簡単な SVG パスの読み取り。M ごとに別の図形にする
        static List<GraphicsPath> ParseSvgPath(string d)
        {
            var list = new List<GraphicsPath>();
            var tokens = Regex.Matches(d, @"[MLCZmlcz]|-?\d*\.?\d+(?:e-?\d+)?");
            GraphicsPath cur = null; PointF pos = PointF.Empty; char cmd = ' ';
            var nums = new List<float>();
            Action flush = null;
            flush = delegate
            {
                if (cur == null) return;
                if (cmd == 'L') for (int i = 0; i + 1 < nums.Count; i += 2) { var p = new PointF(nums[i], nums[i + 1]); cur.AddLine(pos, p); pos = p; }
                if (cmd == 'C') for (int i = 0; i + 5 < nums.Count; i += 6) { var p = new PointF(nums[i + 4], nums[i + 5]); cur.AddBezier(pos, new PointF(nums[i], nums[i + 1]), new PointF(nums[i + 2], nums[i + 3]), p); pos = p; }
                nums.Clear();
            };
            foreach (Match tk in tokens)
            {
                string s = tk.Value;
                if (char.IsLetter(s[0]))
                {
                    char c = char.ToUpperInvariant(s[0]);
                    if (cmd != 'M') flush();
                    if (c == 'M') { cmd = 'M'; nums.Clear(); }
                    else if (c == 'Z') { if (cur != null) cur.CloseFigure(); cmd = 'Z'; }
                    else cmd = c;
                    continue;
                }
                nums.Add(float.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                if (cmd == 'M' && nums.Count == 2)
                {
                    cur = new GraphicsPath(); list.Add(cur); pos = new PointF(nums[0], nums[1]); cur.StartFigure(); nums.Clear(); cmd = 'L';
                }
            }
            flush();
            return list;
        }

        // ロゴを取得できないときの、独自に描いた緑の盾とチェック
        static Bitmap FallbackShield()
        {
            const int n = 180;
            var bmp = new Bitmap(n, n);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = n / 32f;
                using (var path = new GraphicsPath())
                {
                    path.AddLine(16 * s, 1.5f * s, 29 * s, 6 * s);
                    path.AddBezier(29 * s, 6 * s, 29 * s, 19 * s, 24 * s, 26 * s, 16 * s, 30.5f * s);
                    path.AddBezier(16 * s, 30.5f * s, 8 * s, 26 * s, 3 * s, 19 * s, 3 * s, 6 * s);
                    path.CloseFigure();
                    using (var b = new SolidBrush(Color.FromArgb(0x67, 0xB2, 0x79))) g.FillPath(b, path);
                }
                using (var pen = new Pen(Color.White, 3.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(10 * s, 16 * s), new PointF(14.5f * s, 20.5f * s), new PointF(22 * s, 11 * s) });
            }
            return bmp;
        }

        internal static Bitmap RenderIcon(int size, int state)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 盾（停止中・未登録はグレースケール）
                var attr = new System.Drawing.Imaging.ImageAttributes();
                if (state == 1 || state == 3)
                {
                    var cm = new System.Drawing.Imaging.ColorMatrix(new float[][] {
                        new float[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                        new float[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                        new float[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 },
                        new float[] { 0, 0, 0, 0, 1 } });
                    attr.SetColorMatrix(cm);
                }
                // 本家 AdGuard のアイコンに合わせ、上下左右に約 4% の余白を取る（256px で 10px、16px で 1px）
                int pad = Math.Max(size >= 16 ? 1 : 0, (int)Math.Round(size * 0.04));
                int shield = size - pad * 2;
                var shapes = LogoShapes();
                if (shapes != null)
                {
                    bool gray = state == 1 || state == 3;
                    Color green = gray ? Color.FromArgb(0x8E, 0x8E, 0x8E) : Color.FromArgb(0x67, 0xB2, 0x79);
                    float k = shield / 16f;
                    using (var mtx = new Matrix(k, 0, 0, k, pad, pad))
                    using (var body = (GraphicsPath)shapes[0].Clone())
                    using (var check = (GraphicsPath)shapes[1].Clone())
                    {
                        body.Transform(mtx); check.Transform(mtx);
                        using (var b = new SolidBrush(green)) g.FillPath(b, body);
                        using (var w = new SolidBrush(Color.White)) g.FillPath(w, check);
                    }
                }
                else
                {
                    var lg = Logo();
                    g.DrawImage(lg, new Rectangle(pad, pad, shield, shield),
                        0, 0, lg.Width, lg.Height, GraphicsUnit.Pixel, attr);
                }
                attr.Dispose();

                // 家バッジ
                Color badge = state == 2 ? Color.FromArgb(0xF0, 0x8C, 0x1A)
                            : state == 3 ? Color.FromArgb(0xD6, 0x3C, 0x3C)
                            : Color.FromArgb(0x26, 0x32, 0x45);
                // 小さいサイズ（通知領域）は見分けやすさを優先して大きめ、大きいサイズ（スタートメニュー等）は控えめに
                float d = size <= 32 ? size * 0.50f : size * 0.40f;
                float bx = size - pad - d, by = size - pad - d;
                float ring = Math.Max(1f, size / 16f);
                using (var white = new SolidBrush(Color.White))
                    g.FillEllipse(white, bx - ring, by - ring, d + ring, d + ring);
                using (var b = new SolidBrush(badge))
                    g.FillEllipse(b, bx, by, d - ring * 0.2f, d - ring * 0.2f);

                float cx = bx + d / 2, cy = by + d / 2, u = d / 10f;
                using (var house = new GraphicsPath())
                {
                    house.AddPolygon(new[] {
                        new PointF(cx, cy - 3.6f * u),           // 屋根の頂点
                        new PointF(cx + 3.7f * u, cy - 0.2f * u),
                        new PointF(cx + 2.5f * u, cy - 0.2f * u),
                        new PointF(cx + 2.5f * u, cy + 3.2f * u),
                        new PointF(cx - 2.5f * u, cy + 3.2f * u),
                        new PointF(cx - 2.5f * u, cy - 0.2f * u),
                        new PointF(cx - 3.7f * u, cy - 0.2f * u) });
                    using (var w = new SolidBrush(Color.White)) g.FillPath(w, house);
                }
                if (size >= 24)
                {
                    using (var door = new SolidBrush(badge))
                        g.FillRectangle(door, cx - 0.8f * u, cy + 1.0f * u, 1.6f * u, 2.2f * u);
                }
            }
            return bmp;
        }

        void RebuildIcons()
        {
            icoRunning = MakeIcon(0); icoStopped = MakeIcon(1); icoUpdate = MakeIcon(2); icoUnknown = MakeIcon(3);
            tray.Icon = null;   // 次の UpdateVisuals で必ず差し替える
            UpdateVisuals();
        }

        static Icon MakeIcon(int state)
        {
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            using (var bmp = RenderIcon(size, state))
            {
                return Icon.FromHandle(bmp.GetHicon());
            }
        }    }
}
