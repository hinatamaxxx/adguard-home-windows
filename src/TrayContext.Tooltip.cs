// 通知領域アイコンのツールチップ（AdGuard アプリと同じ Windows 標準の表示）。
//   AdGuard Home v0.107.79
//   保護は有効です
//   フィルタは 15 分前に更新されました
// Windows 標準の NotifyIcon.Text（63 文字まで）だけを使う。収まらないときは下の行から省く。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.ServiceProcess;
using System.Threading;

namespace AdGuardHomeTray
{
    partial class TrayContext
    {
        DateTime? filtersUpdatedUtc;
        DateTime filtersCheckedAt = DateTime.MinValue;
        bool filtersBusy;

        string BuildTooltip()
        {
            var lines = new List<string>();
            lines.Add("AdGuard Home" + (currentVersion == "" ? "" : " " + currentVersion));

            bool running = lastStatus == ServiceControllerStatus.Running;
            if (lastStatus == null) lines.Add("サービスが登録されていません");
            else if (!running) lines.Add(lastStatus == ServiceControllerStatus.Stopped ? "停止しています" : StatusText(lastStatus.Value));
            else if (protectionEnabled == false) lines.Add("保護は無効です");
            else lines.Add("保護は有効です");

            if (IsUpdateAvailable()) lines.Add("新しいバージョン " + latestVersion + " があります");
            if (running && filtersUpdatedUtc.HasValue) lines.Add("フィルタは" + Ago(filtersUpdatedUtc.Value) + "に更新されました");

            // 63 文字に収まるまで、後ろの行から省く
            string text = string.Join("\n", lines.ToArray());
            while (text.Length > 63 && lines.Count > 2)
            {
                lines.RemoveAt(lines.Count - 1);
                text = string.Join("\n", lines.ToArray());
            }
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "たった今";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " 分前";
            if (d.TotalDays < 1) return (int)d.TotalHours + " 時間前";
            return (int)d.TotalDays + " 日前";
        }

        void UpdateTooltip()
        {
            string text = BuildTooltip();
            if (tray.Text != text) tray.Text = text;
        }

        // ブロックリストの最終更新時刻を取り直す（数分ごと。ログイン情報がある場合だけ）
        void FetchFilterStatus()
        {
            if (filtersBusy || lastStatus != ServiceControllerStatus.Running || !api.HasCredentials) return;
            if ((DateTime.UtcNow - filtersCheckedAt).TotalMinutes < 5) { UpdateTooltip(); return; }
            filtersBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DateTime? latest = null;
                bool ok = false;
                try
                {
                    api.BaseUrl = GetDashboardUrl();
                    var st = api.Get("control/filtering/status");
                    object fl;
                    if (st != null && st.TryGetValue("filters", out fl) && fl is IEnumerable)
                    {
                        foreach (object o in (IEnumerable)fl)
                        {
                            var f = o as Dictionary<string, object>;
                            if (f == null || !AghApi.Bool(f, "enabled")) continue;
                            DateTime t;
                            if (DateTime.TryParse(AghApi.Str(f, "last_updated"), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal, out t) && t.Year > 2000 && (latest == null || t > latest)) latest = t;
                        }
                    }
                    ok = true;
                }
                catch { }
                Post(delegate
                {
                    filtersBusy = false;
                    if (ok) { filtersUpdatedUtc = latest; filtersCheckedAt = DateTime.UtcNow; }
                    UpdateTooltip();
                });
            });
        }
    }
}
