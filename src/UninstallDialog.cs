// アンインストールの確認画面（uninstall.ps1 から AdGuardHomeTray.exe --uninstall-dialog で呼ばれる）。
// 終了コードで結果を返す: 0 = すべて削除する / 10 = 設定とデータを残す / 1 = キャンセル
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class UninstallDialog : ModernDialog
    {
        public const int ExitRemoveAll = 0, ExitKeepSettings = 10, ExitCancel = 1;
        readonly ModernCheckBox keep;

        public UninstallDialog(bool keepDefault)
            : base(L.T("AdGuard Home のアンインストール", "Uninstall AdGuard Home"), L.T("AdGuard Home をアンインストールしますか？", "Uninstall AdGuard Home?"))
        {
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
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };   // 誤って Enter で消さないよう、最初はキャンセルにフォーカス
        }

        public bool KeepSettings { get { return keep.Checked; } }

        public static int Run(bool keepDefault)
        {
            using (var d = new UninstallDialog(keepDefault))
            {
                if (d.ShowDialog() != DialogResult.OK) return ExitCancel;
                return d.KeepSettings ? ExitKeepSettings : ExitRemoveAll;
            }
        }
    }
}
