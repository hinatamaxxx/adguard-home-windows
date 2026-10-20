// セットアップ画面（Setup.cmd → build\AdGuardHomeTray.exe --setup）。
// 入るものを説明 → install.ps1 を管理者で実行（UAC）→ 結果を表示する。自動起動のショートカットとトレイの起動は利用者の権限で行う。
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class SetupDialog : ModernDialog
    {
        static readonly string DefaultTrayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdGuardHome Tray");
        readonly string installPs1;
        readonly bool upgrade;
        ModernCheckBox autoStart, launch;

        SetupDialog(string installPs1)
            : base(L.T("AdGuard Home セットアップ", "AdGuard Home Setup"), "")
        {
            this.installPs1 = installPs1;
            upgrade = File.Exists(Path.Combine(TrayDirNow(), "AdGuardHomeTray.exe"));
            ShowConfirm();
        }

        static string TrayDirNow() { return TrayContext.ReadSetting("TrayDir") ?? DefaultTrayDir; }

        void ShowConfirm()
        {
            ResetPage(upgrade ? L.T("AdGuard Home を更新します", "Update AdGuard Home") : L.T("AdGuard Home をインストールします", "Install AdGuard Home"));
            if (upgrade)
                AddText(L.T("インストール済みのトレイアプリと管理スクリプトを新しいバージョンに入れ替えます。AdGuard Home の設定はそのまま引き継ぎます。",
                            "The new version replaces the installed tray app and management scripts. Your AdGuard Home settings are kept."), true);
            else
                AddText(L.T("広告やトラッカーをブロックする DNS サーバー「AdGuard Home」を、この PC に入れます。",
                            "This installs AdGuard Home on this PC. AdGuard Home is a DNS server that blocks ads and trackers."), true);
            AddBullets(L.T("AdGuard Home 本体: Windows のサービスとして、PC の起動時に自動で動きます",
                           "AdGuard Home: runs as a Windows service and starts automatically with your PC"),
                       L.T("トレイアプリ: 画面右下の通知領域から、状態の確認・保護の一時停止・更新ができます",
                           "Tray app: check the status, pause protection, and update from the notification area"),
                       L.T("自動更新: AdGuard Home とトレイアプリを毎日、自動で最新にします。トレイのメニューでオフにできます",
                           "Automatic updates: AdGuard Home and the tray app are updated every day. You can turn this off in the tray menu"));
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdGuardHome");
            AddText(L.T("インストール先: " + dir + " ほか", "Install location: " + dir + " and others"));
            AddText(L.T("「" + (upgrade ? "更新" : "インストール") + "」を押すと「このアプリがデバイスに変更を加えることを許可しますか？」と表示されるので、「はい」を選んでください。",
                        "After you click " + (upgrade ? "Update" : "Install") + ", Windows asks \"Do you want to allow this app to make changes to your device?\" Choose Yes."));
            autoStart = AddCheck(L.T("Windows の起動時にトレイアプリを自動で起動する", "Start the tray app with Windows"), upgrade && TrayContext.IsStartupLinkEnabled());
            launch = AddCheck(L.T("完了したらトレイアプリを起動する", "Open the tray app when finished"), true);

            var cancel = AddButton(L.T("キャンセル", "Cancel"), ButtonKind.Normal);
            var ok = AddButton(upgrade ? L.T("更新", "Update") : L.T("インストール", "Install"), ButtonKind.Primary);
            cancel.Click += delegate { Close(); };
            ok.Click += delegate { StartInstall(); };
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Select();
        }

        void StartInstall()
        {
            bool wantAutoStart = autoStart.Checked, wantLaunch = launch.Checked;
            ResetPage(upgrade ? L.T("更新しています…", "Updating…") : L.T("インストールしています…", "Installing…"));
            AddText(L.T("完了まで 1 分ほどかかることがあります。この画面を閉じずにお待ちください。", "This can take about a minute. Keep this window open."));
            AddControl(new ModernProgress(P) { Width = Px(440), Height = Px(16), Margin = new Padding(0, Px(4), 0, Px(14)) });
            ControlBox = false;   // 途中で閉じない

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + installPs1 + "\"")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    int code;
                    using (var proc = Process.Start(psi)) { proc.WaitForExit(); code = proc.ExitCode; }
                    if (code != 0) error = ReadInstallError() ?? L.T("処理が失敗しました（終了コード " + code + "）。", "Setup failed (exit code " + code + ").");
                }
                catch (Win32Exception ex)
                {
                    error = ex.NativeErrorCode == 1223 ? L.T("管理者の許可が得られなかったため、中止しました。", "Canceled because administrator permission was not given.") : ex.Message;
                }
                catch (Exception ex) { error = ex.Message; }

                string trayExe = Path.Combine(TrayDirNow(), "AdGuardHomeTray.exe");
                string note = null;
                if (error == null)
                {
                    try
                    {
                        if (wantAutoStart) TrayContext.CreateStartupLink(trayExe);
                        else TrayContext.RemoveStartupLink();
                    }
                    catch (Exception ex) { note = L.T("自動起動の設定に失敗しました: ", "Could not set up Windows startup: ") + ex.Message; }
                    if (wantLaunch)
                    {
                        try { Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(trayExe) }); }
                        catch (Exception ex) { note = L.T("トレイアプリを起動できませんでした: ", "Could not open the tray app: ") + ex.Message; }
                    }
                }
                BeginInvoke((MethodInvoker)delegate { if (error == null) ShowDone(wantLaunch, wantAutoStart, note); else ShowFailed(error); });
            });
        }

        void ShowDone(bool launched, bool autoStartOn, string note)
        {
            ControlBox = true;
            ResetPage(upgrade ? L.T("更新が完了しました", "Update complete") : L.T("インストールが完了しました", "Installation complete"));
            if (launched)
                AddText(L.T("画面右下の通知領域に AdGuard Home のアイコンが表示されます。ダブルクリックで管理画面、右クリックでメニューが開きます。",
                            "The AdGuard Home icon appears in the notification area. Double-click it to open the dashboard. Right-click it to open the menu."), true);
            else
                AddText(L.T("スタートメニューの「AdGuard Home」から、トレイアプリを起動できます。", "You can open the tray app from \"AdGuard Home\" in the Start menu."), true);
            if (!upgrade)
                AddText(L.T("初めて使うときは、管理画面の案内に沿って初期設定をしてください。", "The first time, follow the steps in the dashboard to finish setup."));
            AddText(autoStartOn ? L.T("Windows の起動時にトレイアプリを自動で起動します。", "The tray app starts with Windows.")
                                : L.T("トレイアプリの自動起動はオフです。トレイのメニュー「その他」からオンにできます。", "The tray app does not start with Windows. You can turn this on under More in the tray menu."));
            if (note != null) AddText(note, true);
            var close = AddButton(L.T("閉じる", "Close"), ButtonKind.Primary);
            close.Click += delegate { Close(); };
            AcceptButton = close;
            CancelButton = close;
            close.Select();
        }

        void ShowFailed(string error)
        {
            ControlBox = true;
            ResetPage(upgrade ? L.T("更新できませんでした", "Update failed") : L.T("インストールできませんでした", "Installation failed"));
            AddText(error, true);
            AddText(L.T("もう一度実行すると、途中まで入った分も含めて入れ直します。", "If you try again, everything is installed again, including parts already installed."));
            var close = AddButton(L.T("閉じる", "Close"), ButtonKind.Normal);
            var retry = AddButton(L.T("もう一度試す", "Try again"), ButtonKind.Primary);
            close.Click += delegate { Close(); };
            retry.Click += delegate { ShowConfirm(); };
            string log = InstallLogPath;
            if (File.Exists(log))
            {
                var open = AddButton(L.T("記録を開く", "Open log"), ButtonKind.Normal);
                open.Click += delegate { try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); } catch { } };
            }
            AcceptButton = retry;
            CancelButton = close;
        }

        string InstallLogPath { get { return Path.Combine(Path.GetDirectoryName(installPs1), "install.log"); } }

        // install.ps1 の記録（install.log）から失敗理由の 1 行を取り出す
        string ReadInstallError()
        {
            try
            {
                foreach (string line in File.ReadAllLines(InstallLogPath))
                {
                    int i = line.IndexOf("INSTALL FAILED: ", StringComparison.Ordinal);
                    if (i < 0) continue;
                    string msg = line.Substring(i + 16);
                    int at = msg.IndexOf(" @ ", StringComparison.Ordinal);
                    return at > 0 ? msg.Substring(0, at) : msg;
                }
            }
            catch { }
            return null;
        }

        public static int Run()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string ps1 = Path.GetFullPath(Path.Combine(dir, "..", "install.ps1"));
            if (!File.Exists(ps1)) ps1 = Path.Combine(dir, "install.ps1");
            if (!File.Exists(ps1))
            {
                MessageBox.Show(L.T("install.ps1 が見つかりません。セットアップ一式のフォルダーから実行してください。", "install.ps1 was not found. Run this from the setup folder."), L.T("AdGuard Home セットアップ", "AdGuard Home Setup"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            using (var d = new SetupDialog(ps1)) d.ShowDialog();
            return 0;
        }
    }
}
