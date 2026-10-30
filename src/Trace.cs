using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 层级问题追踪日志（trace.log）。
    ///
    /// 为什么要有：盒子"偶发跑到应用上面"依赖操作顺序，靠猜修不好。
    /// 这里每 250ms 采样一次全局状态，只在**发生变化**时写一行，包含：
    ///   · 当前前台窗口（类名 + 标题）—— 守卫就是靠它判断"是不是桌面"
    ///   · 每个盒子的 topmost / 可见 / z 序里紧挨在它上面的是哪个窗口
    ///   · 守卫每次 z 序操作的参数与结果（ZORDER 行）
    ///
    /// 排查方法：复现后停手，打开 %APPDATA%\DeskBoxes\trace.log 看最后几十行，
    /// 找 "top=1" 但 fg 不是桌面的行 —— 那一行之前的行为触发源。
    /// 日志超过 2MB 自动滚动到 trace.1.log，不会吃满磁盘。
    /// </summary>
    public static class Trace
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder buf, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr h, StringBuilder buf, int n);
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr h, int index);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint GW_HWNDPREV = 3;      // z 序里排在它上面的窗口

        private static readonly object _lock = new object();
        private static Timer _timer;
        private static string _last = "";

        public static bool Enabled { get { return _timer != null; } }

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Timer();
            _timer.Interval = 250;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
            Line("=== trace start ===");
        }

        public static void Line(string s)
        {
            try
            {
                lock (_lock)
                {
                    string dir = ConfigStore.Dir;
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    string file = System.IO.Path.Combine(dir, "trace.log");

                    System.IO.FileInfo fi = new System.IO.FileInfo(file);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        try { System.IO.File.Copy(file, System.IO.Path.Combine(dir, "trace.1.log"), true); } catch { }
                        System.IO.File.WriteAllText(file, "");
                    }

                    System.IO.File.AppendAllText(file,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
                }
            }
            catch { }
        }

        private static void Tick()
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                IntPtr fg = GetForegroundWindow();
                sb.Append("fg=").Append(ClassOf(fg)).Append(" \"").Append(TitleOf(fg)).Append('"');

                List<Form> forms = DeskGuard.Forms;
                for (int i = 0; i < forms.Count; i++)
                {
                    Form f = forms[i];
                    if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;
                    IntPtr h = f.Handle;
                    bool top = (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
                    IntPtr prev = GetWindow(h, GW_HWNDPREV);
                    sb.Append(" | ").Append(f.Text)
                      .Append(":top=").Append(top ? 1 : 0)
                      .Append(",vis=").Append(IsWindowVisible(h) ? 1 : 0)
                      .Append(",above=").Append(prev == IntPtr.Zero ? "-" : ClassOf(prev));
                }

                string s = sb.ToString();
                if (s != _last) { _last = s; Line("SNAP " + s); }
            }
            catch { }
        }

        private static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "-";
            StringBuilder sb = new StringBuilder(96);
            GetClassNameW(h, sb, 96);
            return sb.ToString();
        }

        private static string TitleOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            int n = GetWindowTextLengthW(h);
            if (n <= 0) return "";
            StringBuilder sb = new StringBuilder(n + 1);
            GetWindowTextW(h, sb, n + 1);
            return sb.ToString();
        }
    }
}
