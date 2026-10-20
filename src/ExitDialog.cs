// トレイアイコンを終了する前の確認画面。失われる機能と、もう一度表示する方法を伝えてから「終了する」で終了する。
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class ExitDialog : ModernDialog
    {
        public ExitDialog() : base("AdGuard Home トレイ", "トレイアイコンを終了しますか？")
        {
            AddText("終了すると、次の機能が使えなくなります。", true);
            AddBullets("通知領域での状態表示（停止・保護オフ・更新ありのお知らせ）",
                       "メニューからの開始・停止、保護の一時停止、更新",
                       "管理画面ウィンドウ（開いていれば閉じます）");
            AddText("もう一度表示するには、スタートメニューの「AdGuard Home トレイ」を開きます。" +
                    "Windows 起動時の自動起動がオンなら、次回のサインイン時にも表示されます。");

            var cancel = AddButton("キャンセル", ButtonKind.Normal);
            var ok = AddButton("終了する", ButtonKind.Primary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };
        }
    }
}
