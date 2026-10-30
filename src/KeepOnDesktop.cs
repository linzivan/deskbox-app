using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 盒子层级：坐在「桌面层」正上方 —— 桌面 &lt; 桌面图标 &lt; 盒子 &lt; 所有应用。
    ///
    /// 这是桌面挂件类工具（桌面时钟、便签、壁纸引擎一类）的常规做法：
    ///   · 不置顶（TOPMOST），所以永远不会盖住任何应用；
    ///   · 也不压到最底（HWND_BOTTOM 会掉到壁纸下面，看得见却点不到）；
    ///   · 而是把自己插在**桌面承载窗口（Progman）的正上方** —— 这个位置天然满足
    ///     "在桌面之上、在所有应用之下"，点桌面/点应用都不会改变它。
    ///   · 盒子统一 WS_EX_NOACTIVATE：点击不激活，系统不会把它提到前台应用之上。
    ///
    /// 唯一需要修正的时机：Win+D（显示桌面）会把桌面窗口抬到普通窗口层的最上面，
    /// 这时把盒子重新归位到 Progman 正上方即可（前台变化 + 1 秒巡检）。
    ///
    /// 已废弃（都试过，均不可行，别捡回来）：
    ///   · TOPMOST/NOTOPMOST 状态机：会盖住应用，且切换时闪；
    ///   · 常驻 HWND_BOTTOM：落到壁纸下面，可见但点不到；
    ///   · 桌面子窗口 SetParent：分层子窗口不支持 UpdateLayeredWindow，画面空白；
    ///   · 置顶 + 按应用矩形挖洞：点桌面时盒子被切掉，体验很差。
    /// </summary>
    public static class DeskGuard
    {
        private delegate void WinEventDelegate(IntPtr hook, int ev, IntPtr hwnd,
            int idObject, int idChild, int thread, int time);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(int evMin, int evMax, IntPtr hmod,
            WinEventDelegate proc, int pid, int tid, int flags);

        private const int EVENT_SYSTEM_FOREGROUND = 3;
        private const int WINEVENT_OUTOFCONTEXT = 0;
        private const uint GW_HWNDNEXT = 2;      // z 序里下一个（更靠下）
        private const uint GW_HWNDPREV = 3;      // z 序里上一个（更靠上）
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        private static readonly List<Form> _forms = new List<Form>();
        private static Timer _timer;
        private static WinEventDelegate _proc;      // 必须留引用，否则会被回收
        private static bool _hooked;

        /// <summary>供 Trace 采样用（只读）。</summary>
        internal static List<Form> Forms { get { return _forms; } }

        public static void Register(Form form)
        {
            if (form == null) return;
            if (!_forms.Contains(form)) _forms.Add(form);

            GuardLog("register " + SafeText(form) + " hwnd=" + form.Handle);
            Place(form);

            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Interval = 1000;          // 只做"是否被顶上去"的巡检，正常情况不发任何 SetWindowPos
                _timer.Tick += delegate { PlaceAll(); };
                _timer.Start();
            }

            if (!_hooked)
            {
                _hooked = true;
                _proc = new WinEventDelegate(delegate { PlaceAll(); });
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

        /// <summary>窗口显示后调用：新窗口会被系统排到最上，必须重新归位到桌面层。</summary>
        public static void BringToBottom(Form f)
        {
            Place(f);
        }

        public static void PlaceAll()
        {
            Form[] arr = _forms.ToArray();
            for (int i = 0; i < arr.Length; i++) Place(arr[i]);
        }

        /// <summary>把窗口归位到桌面承载窗口的正上方（已在上面就不动，避免无谓重绘）。</summary>
        private static void Place(Form f)
        {
            try
            {
                if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
                if (!IsWindowVisible(f.Handle)) return;

                IntPtr desk = DesktopWindow();
                if (desk == IntPtr.Zero) return;
                if (IsAbove(f.Handle, desk)) return;        // 已经就位

                IntPtr referent = GetWindow(desk, GW_HWNDPREV);   // 桌面正上方那个窗口
                SetWindowPos(f.Handle, referent, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Trace.Line("PLACE " + f.Text + " ref=" + ClassOf(referent));
            }
            catch { }
        }

        /// <summary>从 h 往下走，能走到桌面窗口 = 盒子在桌面之上。</summary>
        private static bool IsAbove(IntPtr h, IntPtr desk)
        {
            for (IntPtr w = GetWindow(h, GW_HWNDNEXT); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDNEXT))
                if (w == desk) return true;
            return false;
        }

        private static IntPtr DesktopWindow()
        {
            IntPtr p = FindWindowW("Progman", null);
            if (p != IntPtr.Zero) return p;
            return FindWindowW("WorkerW", null);
        }

        // ---- 兼容旧调用点 ----
        // 菜单是独立 popup，天然浮在所有盒子之上，不需要抬高 owner。
        public static void RaiseToFront(IntPtr h) { }
        public static void RaiseMenu(IntPtr h) { }

        private static string SafeText(Form f)
        {
            try { return f.Text; } catch { return "?"; }
        }

        private static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "-";
            StringBuilder sb = new StringBuilder(96);
            GetClassNameW(h, sb, 96);
            return sb.ToString();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder buf, int n);

        private static void GuardLog(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(ConfigStore.Dir, "guard.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
