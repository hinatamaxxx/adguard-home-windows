// AdGuard Home の管理画面を表示する専用ウィンドウ（WebView2）。
// ブラウザーらしい部品（ツールバー、Edge の右クリックメニュー、パスワード保存の吹き出しなど）は出さず、
// Windows のライト/ダーク設定に追従する。保存済みのログイン情報があれば自動でログインする。
// WebView2 のデータ（ログイン状態など）は %LOCALAPPDATA%\AdGuardHomeTray\WebView2 に保存する（レジストリは使わない）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace AdGuardHomeTray
{
    class DashboardForm : Form
    {
        static DashboardForm current;

        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdGuardHomeTray");
        static readonly string BoundsFile = Path.Combine(DataDir, "window.txt");

        readonly WebView2 web;
        readonly Action<string> openExternal;
        readonly Action<string, string> onCredentialsSaved;
        string typedUser, typedPass;   // ログイン画面で入力された値（ログイン成功後にトレイへ保存するか確認する）
        readonly UserPreferenceChangedEventHandler themeHandler;
        string pendingUrl;
        Uri home;
        bool ready;
        bool autoLoginTried;

        // 管理画面を専用ウィンドウで開く。既に開いていれば、そのウィンドウで移動して前面に出す。
        // WebView2 が使えない場合は openExternal（既定のブラウザー）で開く。
        public static bool IsOpen { get { return current != null && !current.IsDisposed; } }

        public static void ShowPage(string url, Action<string> openExternal, Action<string, string> onCredentialsSaved)
        {
            if (current != null && !current.IsDisposed)
            {
                current.Navigate(url);
                if (current.WindowState == FormWindowState.Minimized) current.WindowState = FormWindowState.Normal;
                current.Activate();
                return;
            }
            current = new DashboardForm(url, openExternal, onCredentialsSaved);
            current.Show();
            current.Activate();
        }

        DashboardForm(string url, Action<string> openExternal, Action<string, string> onCredentialsSaved)
        {
            this.openExternal = openExternal;
            this.onCredentialsSaved = onCredentialsSaved;
            pendingUrl = url;
            home = new Uri(url);

            Text = "AdGuard Home";
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = Theme.TitleBarIcon();
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(640, 480);
            RestoreBounds_();

            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);
            ApplyTheme();

            // Windows のモード切り替えに追従する
            themeHandler = delegate(object s, UserPreferenceChangedEventArgs e)
            {
                if (e.Category == UserPreferenceCategory.General) BeginInvoke((MethodInvoker)ApplyTheme);
            };
            SystemEvents.UserPreferenceChanged += themeHandler;

            HandleCreated += delegate { Theme.ApplyTitleBar(this); };
            FormClosing += delegate { SaveBounds(); };
            FormClosed += delegate
            {
                SystemEvents.UserPreferenceChanged -= themeHandler;
                current = null;
            };
            Shown += async delegate { await InitAsync(); };
        }

        void ApplyTheme()
        {
            bool dark = Theme.IsDark;
            BackColor = dark ? Theme.DarkBack : Color.White;
            // 読み込み中に白く光らないよう、ページの背景色も合わせる
            web.DefaultBackgroundColor = BackColor;
            Theme.ApplyTitleBar(this);
        }

        async Task InitAsync()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                // WebView2 ランタイムがない・起動できない場合は既定のブラウザーで開く
                string url = pendingUrl;
                Close();
                MessageBox.Show(L.T("専用ウィンドウを開けなかったため、ブラウザーで開きます。\n", "Could not open the dashboard window. It opens in your browser instead.\n") + ex.Message,
                    "AdGuard Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
                openExternal(url);
                return;
            }

            var core = web.CoreWebView2;
            // アプリらしくするため、ブラウザー由来の機能を止める
            var st = core.Settings;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.IsPasswordAutosaveEnabled = false;     // Edge の「パスワードを保存しますか」を出さない
            st.IsGeneralAutofillEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+P（印刷）、Ctrl+F（検索）などを無効に
            // ページの配色（AdGuard Home のテーマ「自動」）を Windows のモードに合わせる
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            // AdGuard Home の初期設定画面とログイン画面は、ブラウザーに保存したテーマ（account_theme）を使い、
            // 保存がなければライトにする。開くたびにその時点の判定結果で上書きするので、ページのスクリプトより先に
            // 毎回「自動」を入れ、Windows のライト/ダークに合わせる（ログイン後の画面は AdGuard Home の設定に従う）
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "try { localStorage.setItem('account_theme', JSON.stringify('auto')); } catch (e) { }");

            // 右クリックメニューは、文字入力・選択時のコピー/切り取り/貼り付け/すべて選択だけにする
            core.ContextMenuRequested += delegate(object s, CoreWebView2ContextMenuRequestedEventArgs e)
            {
                var keep = new HashSet<string> { "copy", "cut", "paste", "selectAll", "undo", "redo" };
                var items = e.MenuItems;
                for (int i = items.Count - 1; i >= 0; i--)
                    if (!keep.Contains(items[i].Name)) items.RemoveAt(i);
                // 区切り線だけ残った場合などは表示しない
                if (items.Count == 0) e.Handled = true;
            };

            // 管理画面以外へのリンクや新しいウィンドウは、既定のブラウザーで開く
            core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                openExternal(e.Uri);
            };
            core.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs e)
            {
                Uri u;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u)) return;
                if (u.Scheme == "about" || u.Scheme == "data") return;
                if (!IsDashboard(u)) { e.Cancel = true; openExternal(e.Uri); }
            };
            core.NavigationCompleted += async delegate { await TryAutoLoginAsync(); OfferSaveTypedLogin(); };
            // ログイン画面で入力されたユーザー名・パスワードを受け取る（管理画面のページからだけ）
            core.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                Uri src;
                if (!Uri.TryCreate(e.Source, UriKind.Absolute, out src) || !IsDashboard(src)) return;
                try
                {
                    var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(e.TryGetWebMessageAsString());
                    if (m != null && (string)m["type"] == "login")
                    {
                        typedUser = Convert.ToString(m["name"]);
                        typedPass = Convert.ToString(m["password"]);
                    }
                }
                catch { }
            };

            ready = true;
            web.Source = new Uri(pendingUrl);
        }

        // ログイン画面が出たら、トレイに保存されたログイン情報でログインして元のページへ戻る
        async Task TryAutoLoginAsync()
        {
            if (!ready || web.Source == null) return;
            if (!web.Source.AbsolutePath.EndsWith("/login.html", StringComparison.OrdinalIgnoreCase)) { autoLoginTried = false; return; }
            // ログイン画面では、手で入力されたログイン情報をアプリ側へ渡す仕掛けを入れる
            try { await web.CoreWebView2.ExecuteScriptAsync(CaptureLoginScript); } catch { }
            if (autoLoginTried) return;   // 失敗したら繰り返さず、ログイン画面のままにする
            autoLoginTried = true;

            string user, pass;
            AghApi.LoadCredentials(out user, out pass);
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return;

            var json = new JavaScriptSerializer();
            string body = json.Serialize(json.Serialize(new Dictionary<string, object> { { "name", user }, { "password", pass } }));
            string script = "fetch('/control/login',{method:'POST',headers:{'Content-Type':'application/json'},body:" + body +
                            ",credentials:'same-origin'}).then(function(r){return r.ok;}).catch(function(){return false;})";
            string result;
            try { result = await web.CoreWebView2.ExecuteScriptAsync(script); } catch { return; }
            // ExecuteScriptAsync は Promise を待たないため、少し待ってから元のページへ移動する
            await Task.Delay(400);
            web.Source = new Uri(pendingUrl);
        }

        const string CaptureLoginScript =
            "(function(){if(window.__aghCapture)return;window.__aghCapture=true;" +
            "function send(){var p=document.querySelector('input[type=password]');if(!p||!p.value)return;" +
            "var u=document.querySelector('input[name=username],input[type=text],input[type=email]');" +
            "window.chrome.webview.postMessage(JSON.stringify({type:'login',name:u?u.value:'',password:p.value}));}" +
            "document.addEventListener('submit',send,true);" +
            "document.addEventListener('keydown',function(e){if(e.key==='Enter')send();},true);" +
            "document.addEventListener('click',function(e){if(e.target&&e.target.closest&&e.target.closest('button'))send();},true);})()";

        // 手でログインして管理画面に入れたら、その情報をトレイにも保存するか確認する
        void OfferSaveTypedLogin()
        {
            if (typedUser == null || web.Source == null) return;
            if (web.Source.AbsolutePath.EndsWith("/login.html", StringComparison.OrdinalIgnoreCase)) return;   // まだログインできていない
            string u = typedUser, p = typedPass;
            typedUser = typedPass = null;
            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p)) return;

            string curU, curP;
            AghApi.LoadCredentials(out curU, out curP);
            if (u == curU && p == curP) return;
            string q = string.IsNullOrEmpty(curU)
                ? L.T("このログイン情報をトレイにも保存しますか？\n保護の切り替えや統計の表示、管理画面の自動ログインに使います。", "Save this login info in the tray app too?\nThe app uses it to switch protection, show statistics, and sign in to the dashboard automatically.")
                : L.T("保存済みのログイン情報と違います。新しい情報で更新しますか？", "This login info is different from the saved info. Replace the saved info with it?");
            if (MessageBox.Show(this, q, "AdGuard Home", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AghApi.SaveCredentials(u, p);
            if (onCredentialsSaved != null) onCredentialsSaved(u, p);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // ブラウザーのショートカットを止めたので、再読み込みと戻るだけ自前で受ける
            if (ready && (keyData == Keys.F5 || keyData == (Keys.Control | Keys.R))) { web.Reload(); return true; }
            if (ready && keyData == (Keys.Alt | Keys.Left) && web.CanGoBack) { web.GoBack(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool IsDashboard(Uri u)
        {
            return string.Equals(u.Host, home.Host, StringComparison.OrdinalIgnoreCase) && u.Port == home.Port;
        }

        void Navigate(string url)
        {
            pendingUrl = url;
            home = new Uri(url);
            autoLoginTried = false;
            if (ready) web.Source = new Uri(url);
        }

        // ---------- ウィンドウ位置の保存（ファイル） ----------

        void RestoreBounds_()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(LogicalToDeviceUnits(1280), area.Width), h = Math.Min(LogicalToDeviceUnits(860), area.Height);
            Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
            try
            {
                if (!File.Exists(BoundsFile)) return;
                string[] p = File.ReadAllText(BoundsFile).Trim().Split(',');
                var r = new Rectangle(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture),
                                      int.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture));
                // 画面外（モニター構成の変更など）に出ないときだけ復元する
                foreach (var s in Screen.AllScreens)
                {
                    if (s.WorkingArea.IntersectsWith(r) && r.Width >= 400 && r.Height >= 300)
                    {
                        Bounds = r;
                        if (p.Length > 4 && p[4] == "max") WindowState = FormWindowState.Maximized;
                        break;
                    }
                }
            }
            catch { }
        }

        void SaveBounds()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(BoundsFile, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}",
                    r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? "max" : "normal"));
            }
            catch { }
        }
    }
}
