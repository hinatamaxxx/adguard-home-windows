// 進み具合の表示と完了の画面。
// ・アンインストール: 確認 → 進み具合（uninstall.ps1 が書く progress.log を読む）→ 完了
// ・インストールの完了: get.ps1 が最後に開く。「AdGuard Home を起動して、初期設定の画面を開く」（既定でオン）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    // 段階の一覧。終わった段階に ✓、いま進めている段階に … を付ける
    class StepsLabel : Label
    {
        public StepsLabel(DialogPalette p, int width)
        {
            AutoSize = true;
            UseMnemonic = false;
            MaximumSize = new System.Drawing.Size(width, 0);
            ForeColor = p.Fore;
            Margin = new Padding(0, ModernDialog.Px(4), 0, ModernDialog.Px(12));
        }

        public void SetSteps(IList<string> steps, bool allDone)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                bool current = !allDone && i == steps.Count - 1;
                sb.Append(current ? "…  " : "✓  ").Append(steps[i]);
                if (i < steps.Count - 1) sb.AppendLine();
            }
            Text = sb.ToString();
        }
    }

    // アンインストール（uninstall.ps1 が一時フォルダーにコピーした exe で --uninstall-ui <フォルダー> として起動する）。
    // 選んだ結果を choice.txt（all / keep / cancel）に書き、uninstall.ps1 が書く progress.log を読んで進み具合を出す。
    // progress.log の「#done」で完了、「#error 理由」で失敗の画面にする
    class UninstallFlow : ModernDialog
    {
        readonly string dir;
        readonly ModernCheckBox keep;
        readonly Timer timer = new Timer { Interval = 300 };
        StepsLabel stepsLabel;
        bool keepChosen;

        UninstallFlow(string dir, bool keepDefault)
            : base(L.T("AdGuard Home のアンインストール", "Uninstall AdGuard Home"), L.T("AdGuard Home をアンインストールしますか？", "Uninstall AdGuard Home?"))
        {
            this.dir = dir;
            AddText(L.T("AdGuard Home とトレイアプリをこの PC から削除します。", "AdGuard Home and the tray app will be removed from this PC."), true);
            AddText(L.T("この PC やほかの端末の DNS サーバーに AdGuard Home を指定している場合は、先に元の設定へ戻してください。" +
                        "戻さずに削除すると、インターネットにつながりにくくなります。",
                        "If this PC or other devices use AdGuard Home as their DNS server, change their DNS settings back first. " +
                        "Otherwise, they may have trouble connecting to the internet."));
            AddText(L.T("既定では次のデータも一緒に削除します。", "By default, this data is also deleted:"));
            AddBullets(L.T("AdGuard Home の設定（フィルター・ブロックリスト・ログイン情報など）とクエリログ",
                           "AdGuard Home settings (filters, blocklists, login info, and so on) and the query log"),
                       L.T("トレイアプリに保存したログイン情報と、管理画面ウィンドウのデータ",
                           "Login info saved in the tray app, and the dashboard window data"));
            keep = AddCheck(L.T("設定とデータを残す（再インストールしたときに引き継ぐ）", "Keep settings and data (to use them if you reinstall)"), keepDefault);

            var cancel = AddButton(L.T("キャンセル", "Cancel"), ButtonKind.Normal);
            var ok = AddButton(L.T("アンインストール", "Uninstall"), ButtonKind.Primary);
            cancel.Click += delegate { WriteChoice("cancel"); Close(); };
            ok.Click += delegate { Start(); };
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };   // 誤って Enter で消さないよう、最初はキャンセルにフォーカス
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (timer.Enabled) { e.Cancel = true; return; }   // 削除の途中では閉じない
                if (!File.Exists(ChoiceFile)) WriteChoice("cancel");
            };
            timer.Tick += delegate { Poll(); };
        }

        string ChoiceFile { get { return Path.Combine(dir, "choice.txt"); } }
        string ProgressFile { get { return Path.Combine(dir, "progress.log"); } }

        void WriteChoice(string choice)
        {
            try { File.WriteAllText(ChoiceFile, choice); } catch { }
        }

        void Start()
        {
            keepChosen = keep.Checked;
            WriteChoice(keepChosen ? "keep" : "all");
            ResetPage(L.T("アンインストールしています…", "Uninstalling…"));
            AddText(L.T("完了まで少しかかることがあります。この画面を閉じずにお待ちください。", "This can take a moment. Please keep this window open."));
            stepsLabel = new StepsLabel(P, Px(440));
            AddControl(stepsLabel);
            timer.Start();
        }

        void Poll()
        {
            string[] lines;
            try
            {
                if (!File.Exists(ProgressFile)) return;
                using (var fs = new FileStream(ProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    lines = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return; }
            var steps = new List<string>();
            string error = null;
            bool done = false;
            foreach (var l in lines)
            {
                if (l == "#done") done = true;
                else if (l.StartsWith("#error")) error = l.Length > 7 ? l.Substring(7) : "";
                else steps.Add(l);
            }
            if (done || error != null) { timer.Stop(); ShowFinished(steps, error); return; }
            stepsLabel.SetSteps(steps, false);
        }

        void ShowFinished(List<string> steps, string error)
        {
            if (error == null)
            {
                ResetPage(L.T("アンインストールが完了しました", "Uninstall complete"));
                AddText(keepChosen
                    ? L.T("AdGuard Home とトレイアプリを削除しました。設定とデータは残しています。",
                          "AdGuard Home and the tray app have been removed. Your settings and data were kept.")
                    : L.T("AdGuard Home とトレイアプリを、設定とデータも含めて削除しました。",
                          "AdGuard Home and the tray app have been removed, including settings and data."), true);
            }
            else
            {
                ResetPage(L.T("アンインストールを完了できませんでした", "Uninstall did not finish"));
                AddText(error, true);
            }
            var sl = new StepsLabel(P, Px(440));
            sl.SetSteps(steps, true);
            AddControl(sl);
            var close = AddButton(L.T("閉じる", "Close"), ButtonKind.Primary);
            close.Click += delegate { Close(); };
            AcceptButton = close;
            CancelButton = close;
            close.Select();
        }

        public static int Run(string dir, bool keepDefault)
        {
            using (var d = new UninstallFlow(dir, keepDefault)) d.ShowDialog();
            return 0;
        }
    }

    // インストールの完了（get.ps1 がインストールしたあとに開く）
    class InstallFinishedDialog : ModernDialog
    {
        InstallFinishedDialog()
            : base("AdGuard Home for Windows", L.T("インストールが完了しました", "Installation complete"))
        {
            AddText(L.T("AdGuard Home をインストールしました。画面右下の通知領域のアイコンから管理できます。",
                        "AdGuard Home has been installed. You can manage it from the icon in the notification area."), true);
            AddText(L.T("Windows の起動時に自動で起動します（トレイのメニュー「その他」でオフにできます）。初めて使うときは、管理画面の案内に沿って初期設定をしてください。",
                        "It starts with Windows (you can turn this off under More in the tray menu). The first time, follow the steps in the dashboard to finish the initial setup."));
            var launch = AddCheck(L.T("AdGuard Home を起動して、初期設定の画面を開く", "Start AdGuard Home and open the initial setup"), true);
            var done = AddButton(L.T("完了", "Finish"), ButtonKind.Primary);
            done.Click += delegate
            {
                if (launch.Checked)
                {
                    // 管理画面を開く（トレイが動いていなければ起動してから開く）
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--dashboard") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) }); } catch { }
                }
                Close();
            };
            AcceptButton = done;
            CancelButton = done;
            TopMost = true;
            Shown += delegate { Activate(); done.Focus(); };
        }

        public static int Run()
        {
            using (var d = new InstallFinishedDialog()) d.ShowDialog();
            return 0;
        }
    }
}
