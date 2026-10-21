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
[assembly: AssemblyTitle("AdGuard Home")]   // Windows のスタートアップ設定・通知に出る名前
[assembly: AssemblyDescription("Manage AdGuard Home from the notification area")]
[assembly: AssemblyCompany("AdGuard Home for Windows (unofficial)")]
[assembly: AssemblyProduct("AdGuard Home for Windows")]
[assembly: AssemblyVersion("1.8.6.0")]
[assembly: AssemblyFileVersion("1.8.6.0")]

namespace AdGuardHomeTray
{
    static class Program
    {
        // 起動済みのトレイに「管理画面を開いて」と伝えるイベント（--dashboard で起動したとき用）
        internal const string ShowDashboardEvent = "Local\\AdGuardHomeTray_ShowDashboard";

        // アプリの ID（AppUserModelID）。スタートメニューのショートカットにも同じ ID を付ける（install.ps1）。
        // 通知の見出しやタスクバーは、この ID でショートカットの名前とアイコンを使う
        internal const string AppUserModelId = "AdGuardHomeTray";

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [STAThread]
        static int Main(string[] args)
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);   // ウィンドウやトレイアイコンを作る前に
            // アンインストールの画面（確認・進み具合・完了）だけを出して終了する（uninstall.ps1 から --uninstall-ui <フォルダー> で呼ばれる）
            int ui = Array.FindIndex(args, a => string.Equals(a, "--uninstall-ui", StringComparison.OrdinalIgnoreCase));
            if (ui >= 0 && ui + 1 < args.Length)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return UninstallFlow.Run(args[ui + 1], Array.Exists(args, a => string.Equals(a, "--keep", StringComparison.OrdinalIgnoreCase)));
            }
            // インストールの完了画面だけを出して終了する（get.ps1 から呼ばれる）
            if (Array.Exists(args, a => string.Equals(a, "--install-finished", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return InstallFinishedDialog.Run();
            }
            // アイコンファイル（.ico）を書き出して終了する（manage.ps1 から呼ばれる）。手元の AdGuard Home から取得した
            // ロゴで描くので、スタートメニューや「インストールされているアプリ」に本物のロゴを、配布物に入れずに出せる
            int ii = Array.FindIndex(args, a => string.Equals(a, "--write-icon", StringComparison.OrdinalIgnoreCase));
            if (ii >= 0 && ii + 1 < args.Length) return TrayContext.WriteIconFile(args[ii + 1]);
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
        const string StartupLinkName = "AdGuard Home.lnk";
        // Windows の「設定 → アプリ → スタートアップ」とタスク マネージャーのオン/オフを Windows が保存する場所（読むだけ）
        const string StartupApprovedFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        // manage.ps1（管理者・SYSTEM）が書き、トレイは読むだけの共有設定（InstallDir, TrayDir, AutoUpdate, Version, WebUrl）
        static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\settings.json");

        readonly string baseDir;
        readonly string aghDir;
        readonly string logDir;
        readonly string managePs1;

        readonly NotifyIcon tray;
        readonly ContextMenu menu;
        readonly MenuItem miStatus, miState, miNewVersion, miOpen, miStart, miStop, miRestart;
        readonly MenuItem miAghVersion, miAppVersion, miUpdateNow, miAutoUpdate, miAutoStart;
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

        public TrayContext(bool openDashboardOnStart)
        {
            // AdGuard Home の作業フォルダーは本体の権限チェックで管理者専用になるため、
            // トレイアプリは別フォルダーに置き、バージョン等は manage.ps1 が settings.json に書いた値を読む。
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            aghDir = ReadSetting("InstallDir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdGuardHome");
            logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AdGuardHome\logs");
            managePs1 = Path.Combine(baseDir, "manage.ps1");

            icoRunning = MakeIcon(0);
            icoStopped = MakeIcon(1);
            icoUpdate = MakeIcon(2);
            icoUnknown = MakeIcon(3);

            // Windows 標準（Win32）のメニュー。見た目・ダークモード・角丸は OS に任せる
            menu = new ContextMenu();
            // 先頭の状態表示（標準メニューは 1 項目 1 行なので、行ごとに項目を分ける）
            miStatus = new MenuItem("AdGuard Home") { Enabled = false };
            miState = new MenuItem(L.T("確認中…", "Checking…")) { Enabled = false };
            miNewVersion = new MenuItem("") { Enabled = false, Visible = false };
            miOpen = new MenuItem(L.T("管理画面を開く", "Open dashboard"), delegate { OpenDashboard(); });
            miOpen.DefaultItem = true;   // 太字（ダブルクリックと同じ操作）
            miStart = new MenuItem(L.T("AdGuard Home を開始", "Start AdGuard Home"), delegate { RunManage("Start"); });
            miStop = new MenuItem(L.T("AdGuard Home を停止…", "Stop AdGuard Home…"), delegate { ConfirmStop(); });
            miRestart = new MenuItem(L.T("AdGuard Home を再起動", "Restart AdGuard Home"), delegate { RunManage("Restart"); });

            // アップデート: 何の版かを名前で示す（AdGuard Home 本体とこのアプリ）。自動更新は両方に効く 1 つのスイッチ
            miAghVersion = new MenuItem("AdGuard Home") { Enabled = false };
            miAppVersion = new MenuItem(L.T("このアプリ", "This app")) { Enabled = false };
            miUpdateNow = new MenuItem(L.T("今すぐ確認して更新", "Check and update now"), delegate { UpdateNow(); });
            miAutoUpdate = new MenuItem(L.T("自動で更新する（毎日）", "Update automatically (daily)"), delegate { ToggleAutoUpdate(); });
            var miUpdate = new MenuItem(L.T("アップデート", "Updates"));
            miUpdate.MenuItems.AddRange(new MenuItem[] { miAghVersion, miAppVersion, new MenuItem("-"), miUpdateNow, miAutoUpdate });

            miAutoStart = new MenuItem(L.T("Windows 起動時に自動起動", "Start with Windows"), delegate { ToggleAutoStart(); });
            // 終了はメインメニューの最後に常に表示する（見つからないと不安になるため）。
            // AdGuard Home の実行中は誤操作を防ぐため押せなくし、停止中だけ確認画面を経て終了できる
            miExit = new MenuItem(L.T("アプリを終了する", "Exit"), delegate { ConfirmExit(); });
            var miBrowser = new MenuItem(L.T("管理画面をブラウザーで開く", "Open dashboard in browser"), delegate { Shell(GetDashboardUrl(), null); });

            // 保護の一時停止とサービスの開始・停止は「保護とサービス」にまとめる
            miProtection = new MenuItem(L.T("保護とサービス", "Protection and service"));
            miDns = new MenuItem(L.T("接続用アドレス（DNS サーバー）", "DNS server addresses"));
            miPages = new MenuItem(L.T("設定ページを開く", "Open settings page"));
            foreach (var page in new[] {
                new[] { L.T("ダッシュボード", "Dashboard"), "" }, new[] { L.T("一般設定", "General settings"), "settings" }, new[] { L.T("DNS 設定", "DNS settings"), "dns" },
                new[] { L.T("暗号化設定", "Encryption settings"), "encryption" }, new[] { L.T("クライアント設定", "Client settings"), "clients" }, new[] { L.T("DHCP 設定", "DHCP settings"), "dhcp" },
                new[] { "-", "" },
                new[] { L.T("DNS ブロックリスト", "DNS blocklists"), "filters" }, new[] { L.T("DNS 許可リスト", "DNS allowlists"), "allowlists" },
                new[] { L.T("DNS 書き換え", "DNS rewrites"), "dns_rewrites" }, new[] { L.T("ブロックするサービス", "Blocked services"), "blocked_services" },
                new[] { L.T("カスタムフィルタリングルール", "Custom filtering rules"), "custom_rules" },
                new[] { "-", "" }, new[] { L.T("クエリログ", "Query log"), "logs" } })
            {
                if (page[0] == "-") { miPages.MenuItems.Add(new MenuItem("-")); continue; }
                string hash = page[1];
                miPages.MenuItems.Add(new MenuItem(page[0], delegate { OpenPage(hash); }));
            }
            miLogin = new MenuItem(L.T("ログイン情報を設定…", "Set login info…"), delegate { EditLogin(); });
            miLogout = new MenuItem(L.T("保存したログイン情報を削除", "Delete saved login info"), delegate { DeleteLogin(); });
            BuildProtectionMenu();

            // よく使う操作だけを上に並べ、たまに使うものは「その他」にまとめる（縦に長くしない）
            var miMore = new MenuItem(L.T("その他", "More"));
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
            // Windows は登録したときのアイコンを通知の見出しに使うので、アプリ本来の（通常の）アイコンで登録する。
            // 状態別のアイコンには直後の PollStatus で切り替わる
            tray.Icon = icoRunning;
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
            InitSubmenuUp();

            statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            statusTimer.Tick += delegate { PollStatus(); };
            statusTimer.Start();

            updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
            updateTimer.Tick += delegate { CheckUpdateAsync(); };
            updateTimer.Start();

            apiTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            apiTimer.Tick += delegate { bool svgChanged = File.Exists(LogoSvgFile) ? File.GetLastWriteTimeUtc(LogoSvgFile) != logoSvgStamp : logoSvgStamp != DateTime.MinValue; if (RefreshLogo() | svgChanged) RebuildIcons(); RefreshApiAsync(); FetchFilterStatus(); RefreshCurrentVersionAsync(); CheckSelfUpdated(); CheckUpdateFailures(); };
            apiTimer.Start();

            PollStatus();
            RefreshCurrentVersionAsync();
            RefreshApiAsync();
            CheckUpdateAsync();

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
            ShowBalloon(5000, "AdGuard Home", L.T("AdGuard Home が停止しました。", "AdGuard Home has stopped."), ToolTipIcon.Warning);
        }

        void UpdateVisuals()
        {
            string text;
            Icon icon;
            bool running = lastStatus == ServiceControllerStatus.Running;
            if (lastStatus == null) { text = L.T("未登録", "Not installed"); icon = icoUnknown; }
            else if (running) { text = L.T("実行中", "Running"); icon = icoRunning; }
            else if (lastStatus == ServiceControllerStatus.Stopped) { text = L.T("停止中", "Stopped"); icon = icoStopped; }
            else { text = StatusText(lastStatus.Value); icon = icoStopped; }

            string pending = PendingUpdateText();   // 自動更新オフで新しい版があるときだけ
            bool upd = pending != null;
            if (running && protectionEnabled == false) { icon = icoStopped; text = L.T("実行中（保護オフ）", "Running (protection off)"); }
            else if (upd && running) icon = icoUpdate;

            // 先頭の状態表示: ツールチップと同じく「名前とバージョン」「保護・動作の状態」
            string state;
            if (lastStatus == null) state = L.T("サービスが登録されていません", "The service is not installed");
            else if (!running) state = lastStatus == ServiceControllerStatus.Stopped ? L.T("停止しています", "Stopped") : StatusText(lastStatus.Value);
            else if (protectionEnabled == false) state = L.T("保護は無効です", "Protection is off") + protectionNote;
            else if (protectionEnabled == true) state = L.T("保護は有効です", "Protection is on");
            else state = L.T("動作中", "Running");
            miStatus.Text = "AdGuard Home" + (currentVersion == "" ? "" : " " + currentVersion);
            miState.Text = state;
            miNewVersion.Text = pending ?? "";
            miNewVersion.Visible = upd;
            miStart.Enabled = lastStatus != null && !running;
            miStop.Enabled = running;
            miRestart.Enabled = running;
            UpdateVersionItems();
            miExit.Enabled = CanExit;
            UpdateTooltip();
            if (tray.Icon != icon) tray.Icon = icon;
        }

        static string StatusText(ServiceControllerStatus s)
        {
            switch (s)
            {
                case ServiceControllerStatus.StartPending: return L.T("開始中", "Starting");
                case ServiceControllerStatus.StopPending: return L.T("停止中…", "Stopping…");
                case ServiceControllerStatus.Paused: return L.T("一時停止", "Paused");
                default: return s.ToString();
            }
        }

        void RefreshMenuChecks()
        {
            miAutoStart.Checked = IsAutoStartEnabled();
            miAutoUpdate.Checked = IsAutoUpdateEnabled();
            miLogin.Text = api.HasCredentials ? L.T("ログイン情報を変更…", "Change login info…") : L.T("ログイン情報を設定…", "Set login info…");
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
                MessageBox.Show(L.T("manage.ps1 が見つかりません: ", "manage.ps1 was not found: ") + managePs1, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                if (code == 0) ShowBalloon(5000, "AdGuard Home", L.T("更新処理が完了しました。", "Update finished."), ToolTipIcon.Info, null, 0);   // 版の再取得前でも「更新あり」にしない
                else if (code == 2) ShowBalloon(5000, "AdGuard Home", L.T("すでに最新バージョンです。", "Already up to date."), ToolTipIcon.Info);
                else ShowBalloon(5000, "AdGuard Home", L.T("更新に失敗しました。ログ（manage.log）を確認してください。", "Update failed. Check the log (manage.log)."), ToolTipIcon.Error);
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
        internal static bool IsApprovedByWindows() { return IsApprovedByWindows(StartupLinkName); }

        static bool IsApprovedByWindows(string linkName)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(StartupApprovedFolderKey))
                {
                    var data = k == null ? null : k.GetValue(linkName) as byte[];
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
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { L.T("AdGuard Home を通知領域から管理", "Manage AdGuard Home from the notification area") });
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
                        MessageBox.Show(L.T("Windows のスタートアップ設定でオフになっています。\n開いた設定画面で「AdGuard Home」をオンにしてください。", "Startup is turned off in Windows Settings.\nTurn on \"AdGuard Home\" on the settings page that opens."),
                            "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Shell("ms-settings:startupapps", null);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.T("自動起動を切り替えられませんでした: ", "Could not change the Windows startup setting: ") + ex.Message, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshMenuChecks();
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
                ShowApiUnavailable(L.T("サービスが停止しています", "The service is stopped"));
                return;
            }
            if (!api.HasCredentials)
            {
                ShowApiUnavailable(L.T("ログイン情報を設定すると表示されます", "Set login info to see this"));
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
                    error = (ex.Status == 401 || ex.Status == 403) ? L.T("ログイン情報が正しくありません", "The login info is incorrect") : L.T("取得できません（" + ex.Message + "）", "Unavailable (" + ex.Message + ")");
                }
                catch (Exception ex) { error = L.T("取得できません（" + ex.Message + "）", "Unavailable (" + ex.Message + ")"); }

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
            protectionNote = ms > 0 ? L.T("（あと " + FormatDuration(ms) + "）", " (" + FormatDuration(ms) + " left)") : "";
            apiReason = null;

            string ver = AghApi.Str(status, "version");
            if (!string.IsNullOrEmpty(ver)) currentVersion = ver;

            BuildProtectionMenu();
            UpdateVisuals();
        }

        static string FormatDuration(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalDays >= 1 && ts.TotalHours % 24 == 0) return Units((int)ts.TotalDays, " 日間", "day");
            if (ts.TotalHours >= 1) return Units((int)Math.Round(ts.TotalHours), " 時間", "hour");
            if (ts.TotalMinutes >= 1) return Units((int)Math.Ceiling(ts.TotalMinutes), " 分", "minute");
            return Units((int)Math.Ceiling(ts.TotalSeconds), " 秒", "second");
        }

        // 「10 分」/「10 minutes」
        static string Units(int n, string jaSuffix, string enUnit)
        {
            return L.Ja ? n + jaSuffix : n + " " + enUnit + (n == 1 ? "" : "s");
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
                foreach (var opt in new[] { new object[] { L.T("1 分間 無効にする", "Pause for 1 minute"), 60000L }, new object[] { L.T("10 分間 無効にする", "Pause for 10 minutes"), 600000L },
                                            new object[] { L.T("1 時間 無効にする", "Pause for 1 hour"), 3600000L }, new object[] { L.T("8 時間 無効にする", "Pause for 8 hours"), 28800000L },
                                            new object[] { L.T("再開するまで無効にする", "Pause until resumed"), 0L } })
                {
                    long ms = (long)opt[1];
                    items.Add(new MenuItem((string)opt[0], delegate { SetProtection(false, ms); }));
                }
            }
            else if (protectionEnabled == false)
            {
                items.Add(new MenuItem(L.T("保護を再開する", "Resume protection"), delegate { SetProtection(true, 0); }));
            }
            else
            {
                if (!string.IsNullOrEmpty(apiReason)) items.Add(new MenuItem(L.T("保護の状態: ", "Protection status: ") + apiReason) { Enabled = false });
                items.Add(new MenuItem(L.T("ログイン情報を設定…", "Set login info…"), delegate { EditLogin(); }));
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
                        ShowBalloon(5000, "AdGuard Home", L.T("保護の切り替えに失敗しました: ", "Could not change protection: ") + error, ToolTipIcon.Error);
                    else
                    {
                        protectionEnabled = enabled;   // 通知とトレイのアイコンを新しい状態にそろえる
                        UpdateVisuals();
                        ShowBalloon(3000, "AdGuard Home", enabled ? L.T("保護を再開しました。", "Protection resumed.")
                            : durationMs > 0 ? L.T("保護を無効にしました（" + FormatDuration(durationMs) + "後に再開）。", "Protection is off (back on in " + FormatDuration(durationMs) + ").")
                            : L.T("保護を無効にしました。", "Protection is off."), ToolTipIcon.Info);
                    }
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
                AddInfo(items, L.T("AdGuard Home が動いていません", "AdGuard Home is not running"));
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
                AddInfo(items, L.T("DNS の待ち受けが見つかりません\n（初期設定前かもしれません）", "No DNS listener found\n(initial setup may not be finished yet)"));
                return;
            }
            bool any4 = bound.Contains("0.0.0.0"), any6 = bound.Contains("::");

            Func<System.Net.IPAddress, bool> isBound = delegate(System.Net.IPAddress a)
            {
                bool v6 = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                return (v6 ? any6 : any4) || bound.Contains(a.ToString());
            };

            // このPC
            AddHeader(items, L.T("このPCで使う場合", "On this PC"));
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
                AddHeader(items, L.T("同じネットワークの機器から使う場合", "From devices on the same network"));
                foreach (var e in lan) AddValue(items, Shown(e[0], false) + L.T("（" + ShownName(e[1]) + "）", " (" + ShownName(e[1]) + ")"), e[0]);
            }
            if (vpn.Count > 0)
            {
                AddHeader(items, L.T("Tailscale 経由で使う場合", "Through Tailscale"));
                foreach (var e in vpn) AddValue(items, Shown(e[0], true) + L.T("（" + ShownName(e[1]) + "）", " (" + ShownName(e[1]) + ")"), e[0]);
            }
            if (dot)
            {
                AddHeader(items, L.T("暗号化 DNS", "Encrypted DNS"));
                AddInfo(items, L.T("    DNS-over-TLS（ポート 853）が有効です\n    サーバー名は暗号化設定で確認してください", "    DNS-over-TLS (port 853) is on\n    Check the server name in Encryption settings"));
            }

            if ((lan.Count > 0 || vpn.Count > 0) && !HasFirewallAllowRule())
            {
                items.Add(new MenuItem("-"));
                AddInfo(items, L.T("⚠ ファイアウォールで許可されていないため、\n    今は他の機器から接続できません", "⚠ The firewall is blocking access, so other\n    devices can't connect right now"));
            }
            items.Add(new MenuItem("-"));
            AddInfo(items, L.T("項目をクリックするとアドレスをコピーします", "Click an item to copy the address"));
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

        // 接続名（例: イーサネット）。撮影用ビルドの見本表示では、英語のときに英語の名前にする
        static string ShownName(string name)
        {
#if SCREENSHOT_BUILD
            if (Environment.GetEnvironmentVariable("ADGUARD_TRAY_DEMO") == "1" && !L.Ja && name == "イーサネット") return "Ethernet";
#endif
            return name;
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
            if (values.Count == 0) { AddInfo(items, L.T("    （なし）", "    (none)")); return; }
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
                    error = (ex.Status == 401 || ex.Status == 403) ? L.T("ユーザー名またはパスワードが違います。", "The user name or password is incorrect.") : ex.Message;
                }
                catch (Exception ex) { error = ex.Message; }
                finally { Cursor.Current = Cursors.Default; }

                if (error != null)
                {
                    MessageBox.Show(L.T("管理画面に接続できませんでした。\n", "Could not connect to the dashboard.\n") + error, "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                AghApi.SaveCredentials(form.UserName, form.Password);
                api.SetCredentials(form.UserName, form.Password);
                ShowBalloon(3000, "AdGuard Home", L.T("ログイン情報を保存しました。", "Login info saved."), ToolTipIcon.Info);
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

        // 通常の状態のアイコンを、複数の大きさ（PNG 形式）を入れた .ico に書き出す。0=書き出した, 1=失敗
        internal static int WriteIconFile(string path)
        {
            try
            {
                int[] sizes = { 16, 24, 32, 48, 64, 256 };
                var pngs = new List<byte[]>();
                foreach (int s in sizes)
                    using (var bmp = RenderIcon(s, 0))
                    using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
                string tmp = path + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
                        w.Write((ushort)1); w.Write((ushort)32); w.Write(pngs[i].Length); w.Write(offset);
                        offset += pngs[i].Length;
                    }
                    foreach (var d in pngs) w.Write(d);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return 0;
            }
            catch { return 1; }
        }

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
