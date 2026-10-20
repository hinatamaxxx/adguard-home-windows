// AdGuard Home を停止する前の確認画面。この PC を DNS に使っている機器（つなぎ方を問わず）が
// つながらなくなることと、一時的に元の DNS へ戻す方法を伝える。
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class StopDialog : ModernDialog
    {
        public StopDialog() : base("AdGuard Home", "AdGuard Home を停止しますか？")
        {
            AddText("停止すると、この PC の AdGuard Home を DNS サーバーとして使っている機器は、" +
                    "再開するまでインターネットにつながらなくなります。たとえば次のような機器です。", true);
            AddBullets("この PC 自身（DNS を AdGuard Home に向けている場合）",
                       "家のネットワークで、DNS サーバーにこの PC のアドレスを設定した機器",
                       "Tailscale などの VPN でつないでいて、DNS がこの PC を向いている機器（外出先を含む）",
                       "DNS 構成プロファイルで AdGuard Home を使っている iPhone・iPad（外出先を含む）");
            AddText("止めている間も使いたい機器は、一時的に元の DNS に戻してください。" +
                    "Tailscale は管理画面の DNS 設定、iPhone・iPad は「設定」→「一般」→「VPN とデバイス管理」→「DNS」で「自動」、" +
                    "そのほかの機器はネットワーク設定の DNS を「自動」にします。AdGuard Home を再開したら元に戻してください。");

            var cancel = AddButton("キャンセル", ButtonKind.Normal);
            var ok = AddButton("停止する", ButtonKind.Primary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };   // 誤って Enter で止めないよう、最初はキャンセルにフォーカス
        }
    }
}
