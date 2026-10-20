// AdGuard Home を停止する前の確認画面。この PC を DNS に使っている機器（つなぎ方を問わず）が
// つながらなくなることと、一時的に元の DNS へ戻す方法を伝える。
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class StopDialog : ModernDialog
    {
        public StopDialog() : base("AdGuard Home", L.T("AdGuard Home を停止しますか？", "Stop AdGuard Home?"))
        {
            AddText(L.T("停止すると、この PC の AdGuard Home を DNS サーバーとして使っている機器は、" +
                        "再開するまでインターネットにつながらなくなります。たとえば次のような機器です。",
                        "While AdGuard Home is stopped, devices that use it on this PC as their DNS server " +
                        "can't connect to the internet. For example:"), true);
            AddBullets(L.T("この PC 自身（DNS を AdGuard Home に向けている場合）", "This PC itself (if it uses AdGuard Home for DNS)"),
                       L.T("家のネットワークで、DNS サーバーにこの PC のアドレスを設定した機器", "Devices on your home network that use this PC's address as their DNS server"),
                       L.T("Tailscale などの VPN でつないでいて、DNS がこの PC を向いている機器（外出先を含む）",
                           "Devices connected through a VPN such as Tailscale that use this PC for DNS (including when away from home)"),
                       L.T("DNS 構成プロファイルで AdGuard Home を使っている iPhone・iPad（外出先を含む）",
                           "iPhones and iPads that use AdGuard Home through a DNS profile (including when away from home)"));
            AddText(L.T("止めている間も使いたい機器は、一時的に元の DNS に戻してください。" +
                        "Tailscale は管理画面の DNS 設定、iPhone・iPad は「設定」→「一般」→「VPN とデバイス管理」→「DNS」で「自動」、" +
                        "そのほかの機器はネットワーク設定の DNS を「自動」にします。AdGuard Home を再開したら元に戻してください。",
                        "To keep using a device while AdGuard Home is stopped, change its DNS back for now. " +
                        "On Tailscale, use the DNS settings in the admin console. On iPhone and iPad, choose Automatic under " +
                        "Settings > General > VPN & Device Management > DNS. On other devices, set DNS to automatic in the network settings. " +
                        "Change these settings back after you start AdGuard Home again."));

            var cancel = AddButton(L.T("キャンセル", "Cancel"), ButtonKind.Normal);
            var ok = AddButton(L.T("停止する", "Stop"), ButtonKind.Primary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };   // 誤って Enter で止めないよう、最初はキャンセルにフォーカス
        }
    }
}
