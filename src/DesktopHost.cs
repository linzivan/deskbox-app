using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 把盒子窗口挂成「桌面的子窗口」。
    ///
    /// 目的（用户定的形态）：盒子永远属于桌面层 ——
    ///   · 桌面可见时它就在壁纸/图标之上
    ///   · 任何应用都在它上面，点它也不会把它提到应用之上
    ///   · Win+D 天然免疫（它就是桌面的一部分），不需要任何置顶状态机
    ///
    /// 代价（用户已确认接受）：
    ///   · 应用最大化时盒子被正常遮挡
    ///   · explorer 重启时桌面重建 → 盒子会闪一下再挂回来
    ///   · 盒子不激活（WS_EX_NOACTIVATE）→ 键盘输入一律走独立小弹窗
    /// </summary>
    public static class DesktopHost
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002,
            SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
        private const uint GW_HWNDPREV = 3;

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr h, int idx);
        [DllImport("user32.dll")]
        private static extern int SetWindowLongW(IntPtr h, int idx, int val);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [DllImport("user32.dll")]
        private static extern int MapWindowPoints(IntPtr hFrom, IntPtr hTo, ref POINT p, uint count);

        /// <summary>是否启用桌面层模式（原型阶段用环境变量开关，便于一键回退）。</summary>
        public static bool Enabled
        {
            get { return Environment.GetEnvironmentVariable("DESKBOXES_LAYER") == "desktop"; }
        }

        /// <summary>找桌面的图标视图：Progman\SHELLDLL_DefView，找不到再遍历 WorkerW。</summary>
        public static IntPtr FindDefView()
        {
            IntPtr progman = FindWindowW("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr d = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) return d;
            }
            IntPtr w = IntPtr.Zero;
            for (int i = 0; i < 64; i++)
            {
                w = FindWindowExW(IntPtr.Zero, w, "WorkerW", null);
                if (w == IntPtr.Zero) break;
                IntPtr d = FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) return d;
            }
            return IntPtr.Zero;
        }

        /// <summary>把已经创建出来的窗体挂进桌面。失败返回 false，窗体保持原样。</summary>
        public static bool Attach(Form f)
        {
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return false;
            try
            {
                IntPtr h = f.Handle;
                IntPtr defview = FindDefView();
                if (defview == IntPtr.Zero) return false;

                Rectangle r = f.Bounds;

                // 变成子窗口：去掉 WS_POPUP、加 WS_CHILD；不激活，避免点击抢占其他应用的焦点
                int style = GetWindowLongW(h, GWL_STYLE);
                SetWindowLongW(h, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
                int ex = GetWindowLongW(h, GWL_EXSTYLE);
                SetWindowLongW(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);

                SetParent(h, defview);

                // 屏幕坐标 → 父窗口客户区坐标（多显示器时 DefView 原点可能是负的，必须换算）
                POINT pt = new POINT { X = r.X, Y = r.Y };
                MapWindowPoints(IntPtr.Zero, defview, ref pt, 1);
                SetWindowPos(h, HWND_TOP, pt.X, pt.Y, r.Width, r.Height,
                    SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                return GetParent(h) == defview;
            }
            catch { return false; }
        }

        /// <summary>
        /// 看门狗：explorer 会周期性把桌面图标层（SysListView32）提到我们上面；
        /// explorer 重启还会整棵重建 → 需要重新挂。调用频率建议 300~500ms。
        /// </summary>
        public static void Keep(Form f)
        {
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            try
            {
                IntPtr h = f.Handle;
                if (!IsWindow(h)) return;

                IntPtr defview = FindDefView();
                if (defview == IntPtr.Zero) return;

                if (GetParent(h) != defview) { Attach(f); return; }

                // 有兄弟压在我们上面（图标层）→ 重新提到兄弟最前
                if (GetWindow(h, GW_HWNDPREV) != IntPtr.Zero)
                    SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }
    }
}
