using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 原生 Shell_NotifyIcon 托盘图标。
    ///
    /// 为什么不用 WinForms 的 NotifyIcon：在本机上它注册返回成功（Visible=true、无异常），
    /// 但系统从未建档（HKCU\Control Panel\NotifyIconSettings 里没有本程序的项），图标不显示。
    /// 同一个 exe 用原生 Shell_NotifyIcon 就能建档（探针已验证），所以改走原生。
    /// 好处是能拿到 Shell_NotifyIcon 的真实返回值与 LastError。
    /// </summary>
    public class TrayNotify : NativeWindow, IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
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
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int NIM_ADD = 0;
        private const int NIM_MODIFY = 1;
        private const int NIM_DELETE = 2;

        private const int NIF_MESSAGE = 0x01;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;

        private const int WM_TRAY = 0x0400 + 0x201;
        private const int WM_NULL = 0x0000;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        private NOTIFYICONDATA _nid;
        private Icon _icon;
        private bool _ownsIcon;
        private bool _added;

        /// <summary>右键菜单，由外部创建后赋进来。</summary>
        public ContextMenuStrip Menu;

        /// <summary>双击图标时触发。</summary>
        public Action DoubleClick;

        public bool Visible { get { return _added; } }
        public string LastError { get; private set; }

        public TrayNotify(string tooltip, Icon icon, bool ownsIcon)
        {
            _icon = icon;
            _ownsIcon = ownsIcon;

            CreateParams cp = new CreateParams();
            cp.Caption = "DeskBoxesTrayHost";
            cp.ClassName = "STATIC";
            cp.Style = unchecked((int)0x80000000);   // WS_POPUP，不可见
            cp.ExStyle = 0x00000080;                 // WS_EX_TOOLWINDOW，不进 Alt+Tab
            cp.X = -2000;
            cp.Y = -2000;
            cp.Width = 1;
            cp.Height = 1;
            CreateHandle(cp);

            _nid = new NOTIFYICONDATA();
            _nid.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            _nid.hWnd = Handle;
            _nid.uID = 0x1001;
            _nid.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
            _nid.uCallbackMessage = WM_TRAY;
            _nid.hIcon = _icon != null ? _icon.Handle : IntPtr.Zero;
            _nid.szTip = tooltip ?? "";
        }

        public bool Show()
        {
            if (_added) return true;
            if (_icon == null) { LastError = "图标为空"; return false; }

            bool ok = Shell_NotifyIcon(NIM_ADD, ref _nid);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = err + " " + new Win32Exception(err).Message;
            }
            else LastError = null;

            _added = ok;
            return ok;
        }

        public void Hide()
        {
            if (!_added) return;
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }

        public bool UpdateTip(string tip)
        {
            if (!_added) return false;
            _nid.szTip = tip ?? "";
            return Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRAY)
            {
                int ev = (int)(m.LParam.ToInt64() & 0xFFFF);
                if (ev == WM_LBUTTONDBLCLK)
                {
                    if (DoubleClick != null) DoubleClick();
                }
                else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU)
                {
                    ShowMenu();
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void ShowMenu()
        {
            if (Menu == null) return;
            // 先抢前台，否则菜单点外面不消失（Win32 的经典坑）
            SetForegroundWindow(Handle);
            Menu.Show(Cursor.Position);
            PostMessage(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            Hide();
            if (_ownsIcon && _icon != null)
            {
                try { _icon.Dispose(); } catch { }
            }
            _icon = null;
            try { DestroyHandle(); } catch { }
        }
    }
}
