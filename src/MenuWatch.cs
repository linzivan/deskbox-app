using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 盒子菜单的「点外面自动关闭」。
    ///
    /// 为什么需要单独做：盒子的 owner 是不激活窗口（WS_EX_NOACTIVATE），
    /// ToolStripDropDown 拿不到激活与鼠标捕获，系统不会替我们判定「点了别处」，
    /// 菜单会一直挂在屏幕上。
    ///
    /// 做法：菜单打开期间每 60ms 查一次按键状态——有鼠标键按下、且位置在菜单矩形之外
    /// → 关闭。菜单内部的点击不干预（交给菜单自己处理）。
    /// </summary>
    public static class MenuWatch
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;
        private const int VK_MBUTTON = 0x04;

        private static Timer _timer;
        private static ToolStripDropDown _menu;

        public static void Watch(ToolStripDropDown m)
        {
            if (m == null) return;
            Stop();
            _menu = m;
            m.Closed += delegate { if (_menu == m) Stop(); };

            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Interval = 60;
                _timer.Tick += delegate { Check(); };
            }
            _timer.Start();
        }

        /// <summary>
        /// 判断点是否落在菜单体系内：主菜单 + 所有已展开的子菜单。
        /// 早期版本只判断主菜单矩形 —— 子菜单是独立弹出窗口，坐标在父菜单之外，
        /// 结果点击子菜单项时被判成"点了外面"，菜单先被关掉，菜单项永远收不到点击
        /// （表现：背景色/透明度/字号/布局等所有二级项"点了没反应"）。
        /// </summary>
        private static bool Inside(ToolStripDropDown m, Point p)
        {
            List<IntPtr> handles = new List<IntPtr>();
            Collect(m, handles);
            if (handles.Count == 0) return m.Bounds.Contains(p);

            // ① 鼠标位置命中的窗口，沿父链向上找，命中任一菜单窗口即算在里面
            POINT pt; pt.X = p.X; pt.Y = p.Y;
            IntPtr w = WindowFromPoint(pt);
            int guard = 0;
            while (w != IntPtr.Zero && guard++ < 16)
            {
                if (handles.Contains(w)) return true;
                w = GetParent(w);
            }

            // ② 兜底：逐个矩形判断（某些主题/合成下父子链不完整）
            for (int i = 0; i < handles.Count; i++)
            {
                RECT r;
                if (!GetWindowRect(handles[i], out r)) continue;
                if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return true;
            }
            return false;
        }

        /// <summary>仅供审计/自测：判断某点是否算在菜单体系内。</summary>
        internal static bool InsideForTest(ToolStripDropDown m, Point p) { return Inside(m, p); }

        private static void Collect(ToolStripDropDown m, List<IntPtr> handles)
        {
            if (m == null || m.IsDisposed) return;
            if (m.Visible && m.IsHandleCreated) handles.Add(m.Handle);
            foreach (ToolStripItem it in m.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.DropDown != null && mi.DropDown.Visible) Collect(mi.DropDown, handles);
            }
        }

        private static void Stop()
        {
            _menu = null;
            if (_timer != null) _timer.Stop();
        }

        private static void Check()
        {
            ToolStripDropDown m = _menu;
            if (m == null || m.IsDisposed || !m.Visible) { Stop(); return; }

            bool down = GetAsyncKeyState(VK_LBUTTON) < 0
                     || GetAsyncKeyState(VK_RBUTTON) < 0
                     || GetAsyncKeyState(VK_MBUTTON) < 0;
            if (!down) return;

            if (Inside(m, Cursor.Position)) return;   // 点在菜单体系内（含子菜单）：交给菜单处理

            try { m.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
            Stop();
        }
    }
}
