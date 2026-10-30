using System;
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

            Point p = Cursor.Position;
            if (m.Bounds.Contains(p)) return;    // 点在菜单内部：交给菜单自己处理

            try { m.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
            Stop();
        }
    }
}
