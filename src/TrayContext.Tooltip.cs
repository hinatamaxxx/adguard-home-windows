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
            if (lastStatus == null) lines.Add(L.T("サービスが登録されていません", "The service is not installed"));
            else if (!running) lines.Add(lastStatus == ServiceControllerStatus.Stopped ? L.T("停止しています", "Stopped") : StatusText(lastStatus.Value));
            else if (protectionEnabled == false) lines.Add(L.T("保護は無効です", "Protection is off"));
            else lines.Add(L.T("保護は有効です", "Protection is on"));

            string pending = PendingUpdateText();   // 自動更新オフで新しい版があるときだけ
            if (pending != null) lines.Add(pending);
            if (running && filtersUpdatedUtc.HasValue) lines.Add(L.T("フィルタは" + Ago(filtersUpdatedUtc.Value) + "に更新されました", "Filters: " + Ago(filtersUpdatedUtc.Value)));

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
            if (d.TotalMinutes < 1) return L.T("たった今", "just now");
            if (d.TotalHours < 1) return Count((int)d.TotalMinutes, " 分前", "minute") ;
            if (d.TotalDays < 1) return Count((int)d.TotalHours, " 時間前", "hour");
            return Count((int)d.TotalDays, " 日前", "day");
        }

        // 「3 分前」/「3 minutes ago」
        static string Count(int n, string jaSuffix, string enUnit)
        {
            return L.Ja ? n + jaSuffix : n + " " + enUnit + (n == 1 ? "" : "s") + " ago";
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
