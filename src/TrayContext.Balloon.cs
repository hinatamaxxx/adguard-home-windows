// 通知（バルーン）の表示。アイコンはトレイと同じ状態のものを大きいサイズで描いて使う。
// 操作を付けた通知だけ、クリックでその操作を行う。
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    partial class TrayContext
    {
        MethodInvoker balloonAction;
        string balloonActionText;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const int NIM_MODIFY = 1, NIF_INFO = 0x10;
        const int NIIF_USER = 4, NIIF_LARGE_ICON = 0x20;

        IntPtr balloonIcon = IntPtr.Zero;

        void ShowBalloon(int ms, string title, string text, ToolTipIcon icon)
        {
            ShowBalloon(ms, title, text, icon, null);
        }

        void ShowBalloon(int ms, string title, string text, ToolTipIcon icon, MethodInvoker onClick)
        {
            ShowBalloon(ms, title, text, icon, onClick, -1);
        }

        // state: アイコンの状態（0=通常, 1=停止, 2=更新あり, 3=不明）。-1 ならトレイの今のアイコンと同じ
        void ShowBalloon(int ms, string title, string text, ToolTipIcon icon, MethodInvoker onClick, int state)
        {
            balloonAction = onClick;
            balloonActionText = text;
            // クリック時の照合用に、WinForms 側の値もそろえておく
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = text;
            tray.BalloonTipIcon = icon;
            if (!ShowStateBalloon(ms, title, text, state < 0 ? CurrentIconState() : state)) tray.ShowBalloonTip(ms, title, text, icon);
        }

        int CurrentIconState()
        {
            if (tray.Icon == icoRunning) return 0;
            if (tray.Icon == icoStopped) return 1;
            if (tray.Icon == icoUpdate) return 2;
            return 3;
        }

        // 現在の状態のアイコンを付けて通知する。NotifyIcon の内部（ウィンドウと ID）が取れなければ false
        bool ShowStateBalloon(int ms, string title, string text, int state)
        {
            try
            {
                var f = BindingFlags.Instance | BindingFlags.NonPublic;
                var window = typeof(NotifyIcon).GetField("window", f).GetValue(tray) as NativeWindow;
                int id = (int)typeof(NotifyIcon).GetField("id", f).GetValue(tray);
                if (window == null || window.Handle == IntPtr.Zero) return false;

                int size = SystemInformation.IconSize.Width;   // 大きいアイコン（DPI に応じて 32px 以上）
                if (size < 32) size = 32;
                IntPtr newIcon;
                using (var bmp = RenderIcon(size, state)) newIcon = bmp.GetHicon();

                var data = new NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                data.hWnd = window.Handle;
                data.uID = id;
                data.uFlags = NIF_INFO;
                data.szInfo = text;
                data.szInfoTitle = title;
                data.uTimeoutOrVersion = ms;
                data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
                data.hBalloonIcon = newIcon;
                if (!Shell_NotifyIcon(NIM_MODIFY, ref data)) { DestroyIcon(newIcon); return false; }

                // 前の通知のアイコンは、表示が差し替わった後に破棄する
                if (balloonIcon != IntPtr.Zero) DestroyIcon(balloonIcon);
                balloonIcon = newIcon;
                return true;
            }
            catch { return false; }
        }
    }
}
