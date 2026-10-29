using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// Win+D 保护：前台是桌面时升 TOPMOST，前台是普通程序时落回 NOTOPMOST。
    /// 关键在于「只在状态需要变化时才调 SetWindowPos」——这既是节流，也保证一定能落回。
    ///
    /// 本机实测结论（重要）：Win+D 既不会最小化也不会隐藏窗口，而是把桌面图标层
    /// SysListView32 提到了所有普通顶层窗口之上，把盒子「压」下去。
    /// 所以判据是「前台是否桌面」，动作是把盒子抬到同层最前（HWND_TOP）。
    ///
    /// 三个盒子共用这一个协调器：同一个定时器、同一帧判断、同一帧处理，
    /// 避免各自 150ms 定时器相位不同导致某个盒子晚一拍才浮上来。
    /// </summary>
    public static class DeskGuard
    {
        private delegate void WinEventDelegate(IntPtr hook, int ev, IntPtr hwnd,
            int idObject, int idChild, int thread, int time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(int evMin, int evMax, IntPtr hmod,
            WinEventDelegate proc, int pid, int tid, int flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int n);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);

        private const int EVENT_SYSTEM_FOREGROUND = 3;
        private const int WINEVENT_OUTOFCONTEXT = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOZORDER = 0x0004;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private static List<Form> _forms = new List<Form>();
        private static Timer _timer;
        private static WinEventDelegate _proc;      // 必须留引用
        private static bool _busy;
        private static bool _lastDesktop;
        private static bool _hooked;

        /// <summary>注册一个要守护的窗口，注册即生效。</summary>
        public static void Register(Form form)
        {
            if (form == null) return;
            if (_forms.Contains(form)) return;
            _forms.Add(form);
            GuardLog("register " + form.Text + " hwnd=" + form.Handle);

            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Interval = 150;
                _timer.Tick += delegate { Apply(); };
                _timer.Start();
            }

            if (!_hooked)
            {
                _hooked = true;
                _proc = new WinEventDelegate(delegate { Apply(); });
                try
                {
                    SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
                }
                catch { }
            }
        }

        public static void Unregister(Form form)
        {
            if (form != null) _forms.Remove(form);
        }

        /// <summary>
        /// 把窗口抬到 TOPMOST 层的最前面。
        /// 菜单是「所属窗口」，会叠在 owner 之上——所以只要先把面板抬到所有盒子之上，
        /// 弹出的设置菜单就不会被 Hermes/余额等 TOPMOST 盒子盖住。
        /// </summary>
        public static void RaiseToFront(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            try
            {
                // 只有「桌面在前台」时其它盒子才是 TOPMOST，这时才需要把自己也升上去；
                // 平常的前台是别的应用，强行 TOPMOST 会让面板压在别人上面。
                if (IsDesktopClass(ClassOf(GetForegroundWindow())))
                    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// 把弹出菜单抬到 TOPMOST 层。
        /// 盒子本身在桌面前台时是 TOPMOST，普通菜单会被压在盒子下面（点设置却看不到菜单）。
        /// </summary>
        /// <summary>
        /// 统一的 z-order 调整。
        /// 注意：必须带上窗口当前的真实尺寸、**不能加 SWP_NOSIZE** ——
        /// 实测快捷键盒子带 SWP_NOSIZE 时 SetWindowPos 返回 True 但 TOPMOST 位不生效
        /// （Win+D 后就浮不上来），去掉 NOSIZE 立刻正常。
        /// </summary>
        private static bool ZOrder(IntPtr h, IntPtr after, bool show)
        {
            if (h == IntPtr.Zero) return false;
            try
            {
                RECT rc;
                if (!GetWindowRect(h, out rc)) return false;
                int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
                if (cw <= 0 || ch <= 0) return false;
                uint flags = SWP_NOMOVE | SWP_NOACTIVATE;
                if (show) flags |= SWP_SHOWWINDOW;
                return SetWindowPos(h, after, 0, 0, cw, ch, flags);
            }
            catch { return false; }
        }

        public static void RaiseMenu(IntPtr h)
        {
            ZOrder(h, HWND_TOPMOST, false);
        }


        private static void GuardLog(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(ConfigStore.Dir, "guard.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        private static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            return sb.ToString();
        }

        private static bool IsDesktopClass(string c)
        {
            return c == "Progman" || c == "WorkerW" ||
                   c == "SHELLDLL_DefView" || c == "SysListView32";
        }

        /// <summary>一帧内统一处理所有已注册窗口。</summary>
        private static void Apply()
        {
            if (_busy) return;
            try
            {
                _busy = true;

                bool desktopForeground = IsDesktopClass(ClassOf(GetForegroundWindow()));
                if (desktopForeground != _lastDesktop)
                {
                    _lastDesktop = desktopForeground;
                    string fg = ClassOf(GetForegroundWindow());
                    StringBuilder sb = new StringBuilder("desktop=" + desktopForeground + " fg=" + fg);
                    for (int k = 0; k < _forms.Count; k++)
                    {
                        Form g = _forms[k];
                        if (g == null) continue;
                        sb.Append(" | ").Append(g.Text)
                          .Append(" top=").Append((GetWindowLongW(g.Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                          .Append(" vis=").Append(IsWindowVisible(g.Handle))
                          .Append(" min=").Append(g.WindowState == FormWindowState.Minimized);
                    }
                    GuardLog(sb.ToString());
                }

                Form[] arr = _forms.ToArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    Form f = arr[i];
                    if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;

                    try
                    {
                        IntPtr h = f.Handle;

                        if (f.WindowState == FormWindowState.Minimized)
                            f.WindowState = FormWindowState.Normal;

                        bool topmost = (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
                        bool visible = IsWindowVisible(h);

                        if (desktopForeground)
                        {
                            if (!topmost)
                            {
                                // 直接置顶；对已置顶窗口是幂等的，不必靠「读完 exstyle 再判断」
                                // ——那种写法会读到尚未生效的值，把能置顶的窗口误判成拒绝。
                                bool ok = ZOrder(h, HWND_TOPMOST, false);
                                if (f.Text == "快捷键盒子")
                                    GuardLog("try-top ok=" + ok + " err=" + Marshal.GetLastWin32Error()
                                        + " hwnd=" + h + " ex=" + (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST));
                            }
                            if (!visible)
                            {
                                ZOrder(h, HWND_TOPMOST, true);
                            }
                        }
                        else if (topmost || !visible)
                        {
                            // 落回必须让 SetWindowPos 自己处理整个 Z 序迁移。
                            // 之前写成「手动清 exstyle 的 TOPMOST 位 + SWP_NOZORDER」，
                            // 结果标记位没了、窗口却还留在 TOPMOST 层 ——
                            // 下一帧检查认为已经落回，从此再也不处理，盒子就一直浮在微信上面。
                            ZOrder(h, HWND_NOTOPMOST, false);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally { _busy = false; }
        }
    }
}
