// トレイアイコンを終了する前の確認画面。失われる機能と、もう一度表示する方法を伝えてから「終了する」で終了する。
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class ExitDialog : ModernDialog
    {
        public ExitDialog() : base("AdGuard Home", L.T("トレイアイコンを終了しますか？", "Exit the tray app?"))
        {
            AddText(L.T("終了すると、次の機能が使えなくなります。", "These features stop working when you exit:"), true);
            AddBullets(L.T("通知領域での状態表示（停止・保護オフ・新しいバージョンのお知らせ）", "Status in the notification area (stopped, protection off, new versions)"),
                       L.T("メニューからの開始・停止、保護の一時停止、更新", "Starting and stopping, pausing protection, and updating from the menu"),
                       L.T("管理画面ウィンドウ（開いていれば閉じます）", "The dashboard window (it closes if open)"));
            AddText(L.T("もう一度表示するには、スタートメニューの「AdGuard Home」を開きます。" +
                        "Windows 起動時の自動起動がオンなら、次回のサインイン時にも表示されます。",
                        "To show it again, open \"AdGuard Home\" from the Start menu. " +
                        "If it starts with Windows, it also appears the next time you sign in."));

            var cancel = AddButton(L.T("キャンセル", "Cancel"), ButtonKind.Normal);
            var ok = AddButton(L.T("終了する", "Exit"), ButtonKind.Primary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };
        }
    }
}
