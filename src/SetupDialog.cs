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
            : base("AdGuard Home セットアップ", "")
        {
            this.installPs1 = installPs1;
            upgrade = File.Exists(Path.Combine(TrayDirNow(), "AdGuardHomeTray.exe"));
            ShowConfirm();
        }

        static string TrayDirNow() { return TrayContext.ReadSetting("TrayDir") ?? DefaultTrayDir; }

        void ShowConfirm()
        {
            ResetPage(upgrade ? "AdGuard Home を更新します" : "AdGuard Home をインストールします");
            if (upgrade)
                AddText("インストール済みのトレイアプリと管理スクリプトを新しいバージョンに入れ替えます。AdGuard Home の設定はそのまま引き継ぎます。", true);
            else
                AddText("広告やトラッカーをブロックする DNS サーバー「AdGuard Home」を、この PC に入れます。", true);
            AddBullets("AdGuard Home 本体: Windows のサービスとして、PC の起動時に自動で動きます",
                       "トレイアプリ: 画面右下の通知領域から、状態の確認・保護の一時停止・更新ができます",
                       "自動更新: 毎日 4:30 ごろに新しいバージョンを確かめます。トレイのメニューでオフにできます");
            AddText("インストール先: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdGuardHome") + " ほか");
            AddText("「" + (upgrade ? "更新" : "インストール") + "」を押すと「このアプリがデバイスに変更を加えることを許可しますか？」と表示されるので、「はい」を選んでください。");
            autoStart = AddCheck("Windows の起動時にトレイアプリを自動で起動する", upgrade && TrayContext.IsStartupLinkEnabled());
            launch = AddCheck("完了したらトレイアプリを起動する", true);

            var cancel = AddButton("キャンセル", ButtonKind.Normal);
            var ok = AddButton(upgrade ? "更新" : "インストール", ButtonKind.Primary);
            cancel.Click += delegate { Close(); };
            ok.Click += delegate { StartInstall(); };
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Select();
        }

        void StartInstall()
        {
            bool wantAutoStart = autoStart.Checked, wantLaunch = launch.Checked;
            ResetPage(upgrade ? "更新しています…" : "インストールしています…");
            AddText("完了まで 1 分ほどかかることがあります。この画面を閉じずにお待ちください。");
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
                    if (code != 0) error = ReadInstallError() ?? ("処理が失敗しました（終了コード " + code + "）。");
                }
                catch (Win32Exception ex)
                {
                    error = ex.NativeErrorCode == 1223 ? "管理者の許可が得られなかったため、中止しました。" : ex.Message;
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
                    catch (Exception ex) { note = "自動起動の設定に失敗しました: " + ex.Message; }
                    if (wantLaunch)
                    {
                        try { Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(trayExe) }); }
                        catch (Exception ex) { note = "トレイアプリを起動できませんでした: " + ex.Message; }
                    }
                }
                BeginInvoke((MethodInvoker)delegate { if (error == null) ShowDone(wantLaunch, wantAutoStart, note); else ShowFailed(error); });
            });
        }

        void ShowDone(bool launched, bool autoStartOn, string note)
        {
            ControlBox = true;
            ResetPage(upgrade ? "更新が完了しました" : "インストールが完了しました");
            if (launched)
                AddText("画面右下の通知領域に AdGuard Home のアイコンが表示されます。ダブルクリックで管理画面、右クリックでメニューが開きます。", true);
            else
                AddText("スタートメニューの「AdGuard Home トレイ」から、トレイアプリを起動できます。", true);
            if (!upgrade)
                AddText("初めて使うときは、管理画面の案内に沿って初期設定をしてください。");
            AddText(autoStartOn ? "Windows の起動時にトレイアプリを自動で起動します。" : "トレイアプリの自動起動はオフです。トレイのメニュー「その他」からオンにできます。");
            if (note != null) AddText(note, true);
            var close = AddButton("閉じる", ButtonKind.Primary);
            close.Click += delegate { Close(); };
            AcceptButton = close;
            CancelButton = close;
            close.Select();
        }

        void ShowFailed(string error)
        {
            ControlBox = true;
            ResetPage(upgrade ? "更新できませんでした" : "インストールできませんでした");
            AddText(error, true);
            AddText("もう一度実行すると、途中まで入った分も含めて入れ直します。");
            var close = AddButton("閉じる", ButtonKind.Normal);
            var retry = AddButton("もう一度試す", ButtonKind.Primary);
            close.Click += delegate { Close(); };
            retry.Click += delegate { ShowConfirm(); };
            string log = InstallLogPath;
            if (File.Exists(log))
            {
                var open = AddButton("記録を開く", ButtonKind.Normal);
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
                MessageBox.Show("install.ps1 が見つかりません。セットアップ一式のフォルダーから実行してください。", "AdGuard Home セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            using (var d = new SetupDialog(ps1)) d.ShowDialog();
            return 0;
        }
    }
}
