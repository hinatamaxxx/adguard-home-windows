// 通知（バルーン）の表示。操作を付けた通知だけ、クリックでその操作を行う。
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    partial class TrayContext
    {
        MethodInvoker balloonAction;
        string balloonActionText;

        void ShowBalloon(int ms, string title, string text, ToolTipIcon icon, MethodInvoker onClick)
        {
            balloonAction = onClick;
            balloonActionText = text;
            tray.ShowBalloonTip(ms, title, text, icon);
        }
    }
}
