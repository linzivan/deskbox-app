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
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buf, int n);
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

        /// <summary>刚回到桌面时的连续重刷帧数（桌面激活后图标层会被抬到最上面）。</summary>
        private static int _burst;

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
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

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

        /// <summary>供 Trace 采样用（只读）。</summary>
        internal static List<Form> Forms { get { return _forms; } }

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

                bool wantTop = after == HWND_TOPMOST;
                bool wantNotTop = after == HWND_NOTOPMOST;
                uint showFlag = show ? SWP_SHOWWINDOW : 0u;

                // ① NOMOVE | NOSIZE（实测这条对 Hermes / 余额 / 面板都有效）
                SetWindowPos(h, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | showFlag);
                if (ZOrderOk(h, wantTop, wantNotTop)) return true;

                // ② 带真实尺寸 + NOMOVE（快捷键盒子只认这一组）
                SetWindowPos(h, after, 0, 0, cw, ch, SWP_NOMOVE | SWP_NOACTIVATE | showFlag);
                if (ZOrderOk(h, wantTop, wantNotTop)) return true;

                // ③ 交给系统做整段 Z 序迁移（不带 NOMOVE）
                SetWindowPos(h, after, rc.Left, rc.Top, cw, ch, SWP_NOACTIVATE | showFlag);
                return ZOrderOk(h, wantTop, wantNotTop);
            }
            catch { return false; }
        }

        /// <summary>SetWindowPos 会「返回成功但不生效」，所以必须回读 exstyle 确认。</summary>
        private static bool ZOrderOk(IntPtr h, bool wantTop, bool wantNotTop)
        {
            bool isTop = (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            if (wantTop) return isTop;
            if (wantNotTop) return !isTop;
            return true;
        }

        /// <summary>
        /// 窗口显示后调用：新显示的窗口会被排到 z 序最上。
        /// 桌面在前台时交给状态机（下一帧会置顶）；否则直接压到最底，避免短暂盖住应用。
        /// </summary>
        public static void BringToBottom(Form f)
        {
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            try
            {
                if (!IsWindowVisible(f.Handle)) return;
                if (IsDesktopClass(ClassOf(GetForegroundWindow()))) return;   // 桌面在前台：交给状态机置顶
                SetWindowPos(f.Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
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

        private static string SafeTitle()
        {
            try
            {
                StringBuilder sb2 = new StringBuilder(128);
                GetWindowTextW(GetForegroundWindow(), sb2, 128);
                return sb2.ToString();
            }
            catch { return ""; }
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

                    // 刚回到桌面：后面几帧强制重新置顶，压过刚被抬上来的桌面图标层
                    _burst = desktopForeground ? 10 : 0;
                }

                Form[] arr = _forms.ToArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    Form f = arr[i];
                    if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;

                    try
                    {
                        IntPtr h = f.Handle;

                        // 用户主动隐藏的盒子（托盘 / 盒子右键「隐藏这个盒子」）不要碰：
                        // 既不要置顶，更不要在这里把它重新显示出来 —— 否则一开设置菜单
                        // （桌面回到前台）隐藏的盒子就自己弹回来了。
                        if (!IsWindowVisible(h)) continue;

                        if (f.WindowState == FormWindowState.Minimized)
                            f.WindowState = FormWindowState.Normal;

                        bool topmost = (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

                        if (desktopForeground)
                        {
                            // burst 期间即使已经置顶也要重刷：桌面激活后图标层会被抬到
                            // TOPMOST 层最上面，把我们压在底下（"点了桌面盒子就没了"）。
                            if (!topmost)
                                Trace.Line("RAISE " + f.Text + " (fg=desktop burst=" + _burst + ")");
                            if (!topmost || _burst > 0)
                            {
                                // 必须同时把 WinForms 的 TopMost 属性置 true：
                                // 只用 SetWindowPos 改 exstyle，属性仍是 false，之后盒子一旦
                                // 自己 SetBounds（Hermes 刷新时重设 Location、快捷键盒子拖动），
                                // WinForms 会按属性把窗口打回非置顶 —— 表现就是"Win+D 后它浮不上来"。
                                if (!f.TopMost) f.TopMost = true;
                                if ((GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
                                {
                                    // 属性没生效（有些窗口只认特定 flag 组合）→ 原始 API 兜底
                                    ZOrder(h, HWND_TOPMOST, false);
                                }

                            }
                        }
                        else if (topmost)
                        {
                            Trace.Line("DROP " + f.Text + " (fg=" + ClassOf(GetForegroundWindow()) + ", title=" + SafeTitle() + ")");
                            // 只要不是桌面在前台（哪怕是自己的盒子在前台），就必须落回：
                            // 用户明确要求盒子不能压住正常应用。
                            if (f.TopMost) f.TopMost = false;
                            if ((GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                            {
                                if (!ZOrder(h, HWND_NOTOPMOST, false))
                                    GuardLog("DROP-FAIL " + f.Text + " fg=" + ClassOf(GetForegroundWindow()));
                            }
                        }
                    }
                    catch (Exception ex) { GuardLog("ERR " + f.Text + " " + ex.GetType().Name + ": " + ex.Message); }
                }
                if (_burst > 0) _burst--;
            }
            catch { }
            finally { _busy = false; }
        }
    }
}
