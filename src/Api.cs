// AdGuard Home の管理 API クライアントと、ログイン情報の保存（DPAPI で暗号化して %APPDATA%\AdGuardHomeTray\ へ）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AdGuardHomeTray
{
    class ApiException : Exception
    {
        public readonly int Status;
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    class AghApi
    {
        // 旧版はレジストリ（HKCU）に保存していた。初回の読み込み時にファイルへ移してキーを削除する
        const string LegacyCredKey = @"Software\AdGuardHomeTray";
        static readonly string CredDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdGuardHomeTray");
        static readonly string CredFile = Path.Combine(CredDir, "credentials.dat");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AdGuardHomeTray.v1");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        readonly CookieContainer cookies = new CookieContainer();
        public string BaseUrl;   // 例: http://127.0.0.1/
        string user, pass;

        public AghApi() { LoadCredentials(out user, out pass); }

        public bool HasCredentials { get { return !string.IsNullOrEmpty(user); } }

        public void SetCredentials(string u, string p) { user = u; pass = p; }

        public Dictionary<string, object> Get(string path)
        {
            return Request("GET", path, null) as Dictionary<string, object>;
        }

        public void Post(string path, object body)
        {
            Request("POST", path, body);
        }

        object Request(string method, string path, object body)
        {
            try
            {
                return Send(method, path, body, true);
            }
            catch (ApiException ex)
            {
                // Basic 認証が通らない場合はセッション（Cookie）でログインして再試行
                if (ex.Status != 401 && ex.Status != 403) throw;
                Login();
                return Send(method, path, body, false);
            }
        }

        void Login()
        {
            var body = new Dictionary<string, object> { { "name", user }, { "password", pass } };
            Send("POST", "control/login", body, false);
        }

        object Send(string method, string path, object body, bool basic)
        {
            if (string.IsNullOrEmpty(BaseUrl)) throw new ApiException(0, L.T("管理画面のアドレスが見つかりません", "Dashboard address not found"));
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl.TrimEnd('/') + "/" + path);
            req.Method = method;
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            req.CookieContainer = cookies;
            req.UserAgent = "AdGuardHomeTray/1.1";
            req.Proxy = null;
            if (basic && HasCredentials)
                req.Headers[HttpRequestHeader.Authorization] = "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body));
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = r.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    try { return Json.DeserializeObject(text); } catch { return text; }
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new ApiException(0, ex.Message);
                string msg;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) msg = r.ReadToEnd().Trim();
                int code = (int)resp.StatusCode;
                resp.Close();
                throw new ApiException(code, code + " " + (msg.Length > 120 ? msg.Substring(0, 120) : msg));
            }
        }

        // ---------- ログイン情報の保存 ----------

        // credentials.dat は {"user":…, "password":…} の JSON 全体を DPAPI（CurrentUser）で暗号化したもの
        public static void LoadCredentials(out string u, out string p)
        {
            u = null; p = null;
            MigrateLegacyCredentials();
            try
            {
                if (!File.Exists(CredFile)) return;
                byte[] raw = ProtectedData.Unprotect(File.ReadAllBytes(CredFile), Entropy, DataProtectionScope.CurrentUser);
                var d = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
                string name = Str(d, "user");
                string pw = Str(d, "password");
                if (string.IsNullOrEmpty(name) || pw == null) return;
                u = name;
                p = pw;
            }
            catch { u = null; p = null; }
        }

        public static void SaveCredentials(string u, string p)
        {
            var d = new Dictionary<string, object>();
            d["user"] = u;
            d["password"] = p;
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(d)), Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(CredDir);
            string tmp = CredFile + ".tmp";
            File.WriteAllBytes(tmp, enc);
            if (File.Exists(CredFile)) File.Replace(tmp, CredFile, null);
            else File.Move(tmp, CredFile);
        }

        public static void DeleteCredentials()
        {
            if (File.Exists(CredFile)) File.Delete(CredFile);
            try { if (Directory.Exists(CredDir) && Directory.GetFileSystemEntries(CredDir).Length == 0) Directory.Delete(CredDir); }
            catch { }
        }

        // 旧版が HKCU\Software\AdGuardHomeTray に保存した値をファイルへ移し、キーを削除する
        static void MigrateLegacyCredentials()
        {
            try
            {
                string name = null, pw = null;
                using (var k = Registry.CurrentUser.OpenSubKey(LegacyCredKey))
                {
                    if (k == null) return;
                    name = k.GetValue("ApiUser") as string;
                    string enc = k.GetValue("ApiPassword") as string;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(enc))
                        pw = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), Entropy, DataProtectionScope.CurrentUser));
                }
                if (pw != null && !File.Exists(CredFile)) SaveCredentials(name, pw);
                Registry.CurrentUser.DeleteSubKeyTree(LegacyCredKey, false);
            }
            catch { }   // 移行できなかったときはキーを残し、次回の起動で再試行する
        }

        // ---------- JSON 値の取り出し ----------

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static long Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }

        public static List<string> List(Dictionary<string, object> d, string key)
        {
            var result = new List<string>();
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return result;
            var e = v as IEnumerable;
            if (e == null || v is string) return result;
            foreach (object o in e)
            {
                string s = Convert.ToString(o);
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
            return result;
        }
    }

    // ログイン情報の入力ダイアログ
    class LoginForm : Form
    {
        readonly TextBox txtUser, txtPass;
        public string UserName { get { return txtUser.Text.Trim(); } }
        public string Password { get { return txtPass.Text; } }

        public LoginForm(string currentUser)
        {
            Text = L.T("AdGuard Home - ログイン情報", "AdGuard Home - Login info");
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var info = new Label
            {
                Text = L.T("管理画面のユーザー名とパスワードを入力してください。\n" +
                           "このPCのユーザーだけが復号できる形（Windows DPAPI）で保存します。",
                           "Enter the dashboard user name and password.\n" +
                           "They are saved so only your account on this PC can decrypt them (Windows DPAPI)."),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            table.Controls.Add(info, 0, 0);
            table.SetColumnSpan(info, 2);

            table.Controls.Add(new Label { Text = L.T("ユーザー名", "User name"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 1);
            txtUser = new TextBox { Width = 260, Text = currentUser ?? "" };
            table.Controls.Add(txtUser, 1, 1);
            table.Controls.Add(new Label { Text = L.T("パスワード", "Password"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 2);
            txtPass = new TextBox { Width = 260, UseSystemPasswordChar = true };
            table.Controls.Add(txtPass, 1, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            var ok = new Button { Text = L.T("保存", "Save"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = L.T("キャンセル", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 0, 3);
            table.SetColumnSpan(buttons, 2);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { if (txtUser.Text.Length > 0) txtPass.Focus(); else txtUser.Focus(); };
        }
    }
}
