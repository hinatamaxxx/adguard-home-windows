// AdGuardHomeSetup.exe: インストーラーの入口。毎回ネットから最新のリリースを取得し、SHA-256 を照合してから、
// 一式に含まれるセットアップ画面（AdGuardHomeTray.exe --setup）を開く。終わったら一時フォルダーを消す。
// 本体のデータは持たないので、この exe 自体は古くなっても、いつも最新版が入る。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AdGuard Home for Windows Setup")]
[assembly: AssemblyDescription("Downloads the latest AdGuard Home for Windows and opens the setup")]
[assembly: AssemblyCompany("AdGuard Home for Windows (unofficial)")]
[assembly: AssemblyProduct("AdGuard Home for Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AdGuardHomeSetup
{
    static class Program
    {
        const string Repo = "hinatamaxxx/adguard-home-windows";
        static readonly bool Ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
        static string T(string ja, string en) { return Ja ? ja : en; }

        [STAThread]
        static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new ProgressForm(T("AdGuard Home for Windows の最新版を確認しています…", "Checking the latest version of AdGuard Home for Windows…"));
            string work = Path.Combine(Path.GetTempPath(), "AdGuardHomeSetup-" + Guid.NewGuid().ToString("N"));
            int result = 0;
            form.Shown += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error = null, setupExe = null;
                    try { setupExe = Download(work, s => form.BeginInvoke((MethodInvoker)delegate { form.Status = s; })); }
                    catch (Exception ex) { error = ex.Message; }
                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null)
                        {
                            form.Hide();
                            var r = MessageBox.Show(T("ダウンロードできませんでした。\n", "The download failed.\n") + error + "\n\n" +
                                T("リリースのページを開きますか？", "Open the releases page?"),
                                "AdGuard Home for Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (r == DialogResult.Yes) Open("https://github.com/" + Repo + "/releases/latest");
                            result = 1;
                            form.Close();
                            return;
                        }
                        form.Hide();
                        try
                        {
                            var p = Process.Start(new ProcessStartInfo(setupExe, "--setup") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setupExe) });
                            // 待つ間も画面が固まらないよう、別スレッドで終了を待つ
                            ThreadPool.QueueUserWorkItem(delegate { p.WaitForExit(); p.Dispose(); form.BeginInvoke((MethodInvoker)delegate { form.Close(); }); });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(T("セットアップ画面を開けませんでした。\n", "Could not open the setup window.\n") + ex.Message,
                                "AdGuard Home for Windows", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            result = 1;
                            form.Close();
                        }
                    });
                });
            };
            Application.Run(form);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            return result;
        }

        // 最新のリリースの ZIP を取得して照合・展開し、セットアップ画面の exe のパスを返す
        static string Download(string work, Action<string> status)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            string zipUrl = null, sumUrl = null, tag = null;
            using (var wc = NewClient())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                var rel = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"));
                tag = rel.ContainsKey("tag_name") ? Convert.ToString(rel["tag_name"]) : "";
                foreach (Dictionary<string, object> a in (IEnumerable)rel["assets"])
                {
                    string name = Convert.ToString(a["name"]), url = Convert.ToString(a["browser_download_url"]);
                    if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (name == "AdGuardHomeTray.zip") zipUrl = url;
                    if (name == "AdGuardHomeTray.zip.sha256") sumUrl = url;
                }
            }
            if (zipUrl == null || sumUrl == null) throw new Exception(T("リリースに AdGuardHomeTray.zip が見つかりません。", "AdGuardHomeTray.zip was not found in the release."));

            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "AdGuardHomeTray.zip");
            status(T(tag + " をダウンロードしています…", "Downloading " + tag + "…"));
            string expected;
            using (var wc = NewClient())
            {
                wc.DownloadFile(zipUrl, zip);
                expected = wc.DownloadString(sumUrl).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            }
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zip))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception(T("ダウンロードしたファイルが一致しません（SHA-256）。もう一度実行してください。", "The downloaded file does not match (SHA-256). Please try again."));

            status(T("準備しています…", "Preparing…"));
            string dir = Path.Combine(work, "x");
            ZipFile.ExtractToDirectory(zip, dir);
            string exe = Path.Combine(dir, "AdGuardHomeTray", "build", "AdGuardHomeTray.exe");
            if (!File.Exists(exe)) throw new Exception(T("ZIP の中身が想定と違います。", "The ZIP file does not contain the expected files."));
            return exe;
        }

        static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "AdGuardHomeSetup";
            return wc;
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    // 進み具合を出す小さな画面（Windows のライト/ダークに合わせる）
    class ProgressForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Label label;
        readonly bool dark;

        public ProgressForm(string text)
        {
            dark = IsDark();
            Text = "AdGuard Home for Windows";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;   // 大きさは画面の拡大率から自分で計算する（PerMonitorV2）
            Font = new Font(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "Yu Gothic UI" : "Segoe UI", 9.75f);
            BackColor = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
            ForeColor = dark ? Color.White : Color.Black;
            float k;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
            Func<int, int> px = v => (int)Math.Round(v * k);
            ClientSize = new Size(px(440), px(118));
            Padding = new Padding(px(24), px(26), px(24), px(24));

            label = new Label { Text = text, AutoSize = false, Dock = DockStyle.Top, Height = px(40), UseMnemonic = false };
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Top, Height = px(6) };
            Controls.Add(bar);
            Controls.Add(label);
        }

        public string Status { set { label.Text = value; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4); } catch { }
        }

        static bool IsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return false; }
        }
    }
}
