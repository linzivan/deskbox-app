using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// Hermes 盒子：显示最近 3 个会话的执行状态与今日统计。
    ///
    /// 数据来自 Hermes 本地网关 API（见 HermesData）。Hermes 没在运行时**整个盒子隐藏**，
    /// 不显示「未连接」之类的空壳 —— 用户明确要求「关闭时不显示盒子」。
    /// 分层窗口自绘，可拖动、右下角缩放、右键改外观，交互与余额盒子一致。
    /// </summary>
    public class HermesBox : Form
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        private bool _guardOn;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ResizeBorder = 10;
        private const int Radius = 14;
        private const int RowH = 42;

        private AppState _st;
        private List<HSession> _sessions = new List<HSession>();
        private HSummary _summary;
        private string _error;
        private bool _loading;
        private DateTime _lastFetch = DateTime.MinValue;

        private bool _moving;
        private Point _moveStart, _moveOrigin;
        private bool _resizing;
        private Point _resizeStart;
        private Size _resizeOrigin;

        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _guard;

        public event EventHandler Changed;

        public HermesBox(AppState st)
        {
            _st = st;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "Hermes 盒子";

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Max(200, _st.HermesW);
            int h = Math.Max(120, _st.HermesH);
            int x = Math.Min(_st.HermesX, wa.Right - w - 20);
            int y = Math.Min(_st.HermesY, wa.Bottom - h - 20);
            if (x < 0) x = wa.Right - w - 40;
            if (y < 0) y = wa.Bottom - h - 40;
            Bounds = new Rectangle(x, y, w, h);

            int mins = _st.HermesRefreshMin;
            if (mins < 1) mins = 2;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = mins * 60 * 1000;
            _timer.Tick += delegate { Fetch(); };
            _timer.Start();

            _guard = new System.Windows.Forms.Timer();
            _guard.Interval = 1500;
            _guard.Tick += delegate
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized)
                        WindowState = FormWindowState.Normal;
                }
                catch { }
            };
            _guard.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) DeskGuard.BringToBottom(this);   // 新显示的窗口会被排到最上，压回底层
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_guardOn) { _guardOn = true; DeskGuard.Register(this); }
            Render();
            Fetch();
        }

        // ---------------- 取数 ----------------

        public void Fetch()
        {
            if (_loading) return;
            _loading = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                HSummary sum;
                string err;
                List<HSession> list = HermesData.FetchRecent(3, out sum, out err);

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _loading = false;
                        _lastFetch = DateTime.Now;
                        _sessions = list ?? new List<HSession>();
                        _summary = sum;
                        _error = err;

                        if (list == null)
                        {
                            // Hermes 没在跑：整盒隐藏（用户要求），定时器仍在跑，
                            // 一旦 Hermes 起来就会自动显示回来
                            if (Visible) Hide();
                        }
                        else
                        {
                            if (!Visible)
                            {
                                // 每次 Show 前显式恢复配置里的位置。
                                // 不这么做的话，Hide→Show 之后 WinForms 会按自己的
                                // 规则重新放置窗体，用户拖好的位置就丢了。
                                Location = new Point(_st.HermesX, _st.HermesY);
                                Show();
                            }
                            Render();
                        }
                    }));
                }
                catch { }
            });
        }

        // ---------------- 绘制 ----------------

        private void Render()
        {
            try { RenderCore(); }
            catch (Exception ex) { PanelForm.LogError("HermesRender", ex); }
        }

        private void RenderCore()
        {
            if (!IsHandleCreated) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);
                    DrawAll(g, w, h);
                }

                if (Environment.GetEnvironmentVariable("DESKBOXES_DUMP_UI") != null)
                {
                    try { bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hermes_ui.png"), ImageFormat.Png); }
                    catch { }
                }

                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr old = SelectObject(memDc, hBitmap);
                try
                {
                    SIZE size = new SIZE { CX = w, CY = h };
                    POINT src = new POINT { X = 0, Y = 0 };
                    POINT dst = new POINT { X = Left, Y = Top };
                    BLENDFUNCTION bf = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA
                    };
                    UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBitmap);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private static Color Hex(string s, Color dflt)
        {
            try
            {
                if (!string.IsNullOrEmpty(s) && s.StartsWith("#") && s.Length >= 7)
                {
                    return Color.FromArgb(
                        Convert.ToInt32(s.Substring(1, 2), 16),
                        Convert.ToInt32(s.Substring(3, 2), 16),
                        Convert.ToInt32(s.Substring(5, 2), 16));
                }
            }
            catch { }
            return dflt;
        }

        private void DrawAll(Graphics g, int w, int h)
        {
            int alpha = _st.HermesAlpha;
            if (alpha < 40) alpha = 190;
            Color baseColor = Hex(_st.HermesColor, Color.FromArgb(30, 30, 46));
            Color bg = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

            using (GraphicsPath path = RoundRect(new Rectangle(0, 0, w - 1, h - 1), Radius))
            using (SolidBrush b = new SolidBrush(bg))
                g.FillPath(b, path);

            using (GraphicsPath path = RoundRect(new Rectangle(0, 0, w - 1, h - 1), Radius))
            using (Pen p = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
                g.DrawPath(p, path);

            int pad = 12;

            // 标题
            using (Font f = new Font("Microsoft YaHei UI", _st.HermesFont + 0.5f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 240, 246, 255)))
                g.DrawString("Hermes", f, b, pad, 6);

            // 统计行
            string stat;
            if (_error != null) stat = _error;
            else if (_summary == null) stat = "读取中…";
            else
                stat = "今日 " + _summary.Sessions + " 会话 · " + _summary.Running + " 执行中";

            using (Font f = new Font("Microsoft YaHei UI", 8f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(205, 178, 198, 228)))
                g.DrawString(stat, f, b, pad, 27);

            // 分隔线
            using (Pen p = new Pen(Color.FromArgb(45, 255, 255, 255), 1f))
                g.DrawLine(p, pad, 47, w - pad, 47);

            // 会话列表
            int y = 54;
            int maxRows = Math.Max(1, (h - y - 8) / RowH);
            int rows = Math.Min(_sessions.Count, maxRows);

            Font titleFont = new Font("Microsoft YaHei UI", 9.2f);
            Font subFont = new Font("Microsoft YaHei UI", 7.8f);
            Color titleColor = Color.FromArgb(238, 240, 246, 255);
            Color subColor = Color.FromArgb(180, 168, 188, 218);

            try
            {
                if (rows == 0)
                {
                    using (SolidBrush b = new SolidBrush(subColor))
                        g.DrawString(_loading ? "读取中…" : "暂无会话", subFont, b, pad + 2, y + 6);
                }

                for (int i = 0; i < rows; i++)
                {
                    HSession s = _sessions[i];
                    Color dot = Hex(s.Dot, Color.Gray);

                    // 「需查看」整行加一层淡橙底，便于一眼扫到
                    if (s.State == HState.NeedCheck)
                    {
                        using (GraphicsPath rp = RoundRect(new Rectangle(pad - 4, y - 2, w - pad * 2 + 8, RowH - 6), 6))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 255, 152, 0)))
                            g.FillPath(b, rp);
                    }

                    using (SolidBrush b = new SolidBrush(dot))
                        g.FillEllipse(b, pad + 1, y + 5, 8, 8);

                    // 标题（超长省略）
                    Rectangle rt = new Rectangle(pad + 16, y, w - pad * 2 - 16, 19);
                    TextRenderer.DrawText(g, s.Title, titleFont, rt, titleColor,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                    // 副标题：状态 · 时间 · 条数
                    string sub = s.StateText + " · " + HermesData.AgoText(s.LastActive)
                               + " · " + s.MessageCount + " 条";
                    Rectangle rs = new Rectangle(pad + 16, y + 19, w - pad * 2 - 16, 16);
                    TextRenderer.DrawText(g, sub, subFont, rs, subColor,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                    y += RowH;
                }
            }
            finally
            {
                titleFont.Dispose();
                subFont.Dispose();
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020) return;
            base.WndProc(ref m);
        }

        // ---------------- 拖动 / 缩放 ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) return;

            if (e.X >= Width - ResizeBorder && e.Y >= Height - ResizeBorder)
            {
                _resizing = true;
                _resizeStart = Cursor.Position;
                _resizeOrigin = Size;
                return;
            }
            _moving = true;
            _moveStart = Cursor.Position;
            _moveOrigin = new Point(Left, Top);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_moving)
            {
                Point cur = Cursor.Position;
                Location = new Point(_moveOrigin.X + (cur.X - _moveStart.X),
                                     _moveOrigin.Y + (cur.Y - _moveStart.Y));
                Render();
                return;
            }
            if (_resizing)
            {
                Point cur = Cursor.Position;
                int nw = Math.Max(200, _resizeOrigin.Width + (cur.X - _resizeStart.X));
                int nh = Math.Max(120, _resizeOrigin.Height + (cur.Y - _resizeStart.Y));
                Size = new Size(nw, nh);
                Render();
                return;
            }

            Cursor = (e.X >= Width - ResizeBorder && e.Y >= Height - ResizeBorder)
                ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_moving)
            {
                _moving = false;
                _st.HermesX = Left; _st.HermesY = Top;
                Raise();
                return;
            }
            if (_resizing)
            {
                _resizing = false;
                _st.HermesW = Width; _st.HermesH = Height;
                Raise();
                return;
            }

            if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
        }

        private void ShowMenu(Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);

            m.Items.Add("立即刷新", null, delegate { Fetch(); });

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem color = new ToolStripMenuItem("背景颜色");
            string[] colors = { "#1E1E2E", "#16202C", "#0F2027", "#202A1E", "#2A1E1E", "#1A1A1A" };
            for (int i = 0; i < colors.Length; i++)
            {
                string ci = colors[i];
                ToolStripMenuItem it = new ToolStripMenuItem(ci);
                it.Click += delegate { _st.HermesColor = ci; Render(); Raise(); };
                color.DropDownItems.Add(it);
            }
            color.DropDownItems.Add(new ToolStripSeparator());
            color.DropDownItems.Add("自定义…", null, delegate
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        _st.HermesColor = "#" + cd.Color.R.ToString("X2") + cd.Color.G.ToString("X2") + cd.Color.B.ToString("X2");
                        Render(); Raise();
                    }
                }
            });
            m.Items.Add(color);

            ToolStripMenuItem alphaMenu = new ToolStripMenuItem("不透明度");
            int[] avs = { 255, 230, 210, 190, 160, 130, 100 };
            for (int i = 0; i < avs.Length; i++)
            {
                int av = avs[i];
                ToolStripMenuItem it = new ToolStripMenuItem(av + "%");
                it.Click += delegate { _st.HermesAlpha = (int)(av * 255 / 100); Render(); Raise(); };
                alphaMenu.DropDownItems.Add(it);
            }
            m.Items.Add(alphaMenu);

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新间隔");
            int[] mins = { 1, 2, 5, 10, 30 };
            for (int i = 0; i < mins.Length; i++)
            {
                int mv = mins[i];
                ToolStripMenuItem it = new ToolStripMenuItem(mv + " 分钟");
                it.Click += delegate
                {
                    _st.HermesRefreshMin = mv;
                    _timer.Interval = mv * 60 * 1000;
                    Raise();
                };
                refresh.DropDownItems.Add(it);
            }
            m.Items.Add(refresh);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("隐藏这个盒子", null, delegate
            {
                _st.HermesVisible = false;
                Hide();
                Raise();
            });

            DeskGuard.RaiseToFront(Handle);
            m.Show(this, at);
            MenuWatch.Watch(m);

            DeskGuard.RaiseMenu(m.Handle);
        }

        private void Raise()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_st != null && !_moving) { _st.HermesX = Left; _st.HermesY = Top; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_guard != null) _guard.Stop(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
