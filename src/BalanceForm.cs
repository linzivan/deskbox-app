using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 余额盒子：DeepSeek 余额 + 今日/本月消耗 + 最近 24 小时趋势。
    /// 分层窗口自绘，可拖动、右下角缩放、右键改外观。
    /// </summary>
    public class BalanceForm : Form
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        private bool _guardOn;         // Win+D 保护是否已注册

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
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ResizeBorder = 10;
        private const int Radius = 14;

        private AppState _st;
        private double _balance = -1;
        private string _error = "";
        private List<BalanceData.Point> _history = new List<BalanceData.Point>();
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

        public BalanceForm(AppState st)
        {
            _st = st;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "余额盒子";

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Min(_st.BalX, wa.Right - _st.BalW - 20);
            int y = Math.Min(_st.BalY, wa.Bottom - _st.BalH - 20);
            if (x < 0) x = wa.Right - _st.BalW - 40;
            if (y < 0) y = wa.Bottom - _st.BalH - 40;
            Bounds = new Rectangle(x, y, _st.BalW, _st.BalH);

            _history = BalanceData.LoadHistory();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(1, _st.BalRefreshMin) * 60 * 1000;
            _timer.Tick += delegate { Fetch(); };
            _timer.Start();

            // Win+D 时不跟着隐藏
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
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | 0x08000000;   // WS_EX_NOACTIVATE：点击不抢焦点
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
            _error = "";

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                double v = BalanceData.FetchBalance(out err);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _loading = false;
                        _lastFetch = DateTime.Now;
                        if (v >= 0)
                        {
                            _balance = v;
                            _error = "";
                            BalanceData.AppendHistory(v);
                            _history = BalanceData.LoadHistory();
                        }
                        else _error = err ?? "获取失败";
                        Render();
                    }));
                }
                catch { }
            });
        }

        // ---------------- 绘制 ----------------

        private void Render()
        {
            try { RenderCore(); }
            catch (Exception ex) { PanelForm.LogError("BalanceRender", ex); }
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

                DumpBitmap(bmp, "balance");

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
                        BlendOp = AC_SRC_OVER, BlendFlags = 0,
                        SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA
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


        /// <summary>调试用：设 DESKBOXES_DUMP_UI=目录 时，把每帧渲染结果存成 PNG。</summary>
        private static void DumpBitmap(Bitmap bmp, string tag)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("DESKBOXES_DUMP_UI");
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                bmp.Save(Path.Combine(dir, "ui_" + tag + ".png"), ImageFormat.Png);
            }
            catch { }
        }

        private static Color Hex(string s, Color dflt)
        {
            try { return ColorTranslator.FromHtml(s); }
            catch { return dflt; }
        }

        private void DrawAll(Graphics g, int w, int h)
        {
            Color bg = Hex(_st.BalColor, Color.FromArgb(30, 30, 46));
            int alpha = Math.Max(40, Math.Min(255, _st.BalAlpha));

            using (GraphicsPath p = RoundRect(new Rectangle(0, 0, w - 1, h - 1), Radius))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, bg)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(Math.Min(255, alpha / 2 + 40), 120, 150, 190), 1f))
                    g.DrawPath(pen, p);
            }

            int pad = 12;
            float y = pad;

            // 标题
            using (Font ft = new Font("Microsoft YaHei UI", 8.5f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 190, 205, 225)))
                g.DrawString("DeepSeek 余额", ft, b, pad, y);
            y += 18;

            // 余额大字
            string balText = _balance >= 0 ? "\u00A5" + _balance.ToString("0.00") : "--.--";
            using (Font fb = new Font("Microsoft YaHei UI", _st.BalFont, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(_error.Length > 0 && _balance < 0
                       ? Color.FromArgb(235, 235, 150, 150)
                       : Color.FromArgb(245, 240, 246, 255)))
            {
                g.DrawString(balText, fb, b, pad - 2, y);
                y += fb.Height + 2;
            }

            // 今日 / 本月消耗 + 刷新时间
            double todaySpent = BalanceData.SpentSince(_history, BalanceData.TodayStartTs());
            double monthSpent = BalanceData.SpentSince(_history, BalanceData.MonthStartTs());

            using (Font fs = new Font("Microsoft YaHei UI", 8f))
            using (SolidBrush b1 = new SolidBrush(Color.FromArgb(210, 240, 180, 150)))
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(190, 190, 205, 225)))
            {
                string s1 = "今日 -" + todaySpent.ToString("0.00");
                string s2 = "本月 -" + monthSpent.ToString("0.00");
                g.DrawString(s1, fs, b1, pad, y);
                SizeF w1 = g.MeasureString(s1, fs);
                g.DrawString(s2, fs, b2, pad + w1.Width + 10, y);

                if (_error.Length > 0)
                {
                    SizeF w2 = g.MeasureString(s2, fs);
                    using (SolidBrush be = new SolidBrush(Color.FromArgb(200, 235, 140, 140)))
                        g.DrawString("· " + _error, fs, be, pad + w1.Width + w2.Width + 20, y);
                }
                else if (_loading)
                {
                    SizeF w2 = g.MeasureString(s2, fs);
                    g.DrawString("· 更新中", fs, b2, pad + w1.Width + w2.Width + 20, y);
                }
                y += fs.Height + 4;
            }

            // 24h 趋势
            int chartTop = (int)y;
            int chartH = h - chartTop - pad;
            if (chartH > 24) DrawChart(g, new Rectangle(pad, chartTop, w - pad * 2, chartH));
        }

        private void DrawChart(Graphics g, Rectangle r)
        {
            List<BalanceData.Point> pts = BalanceData.Last24h(_history);
            if (pts.Count < 2)
            {
                using (Font f = new Font("Microsoft YaHei UI", 7.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 180, 195, 215)))
                    g.DrawString("积累数据中…", f, b, r.X, r.Y + 4);
                return;
            }

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].Balance < min) min = pts[i].Balance;
                if (pts[i].Balance > max) max = pts[i].Balance;
            }
            if (max - min < 0.01) { max = min + 0.01; }

            long t0 = pts[0].Ts, t1 = pts[pts.Count - 1].Ts;
            if (t1 <= t0) t1 = t0 + 1;

            PointF[] line = new PointF[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                float px = r.X + (float)((pts[i].Ts - t0) / (double)(t1 - t0)) * r.Width;
                float py = r.Bottom - (float)((pts[i].Balance - min) / (max - min)) * r.Height;
                line[i] = new PointF(px, py);
            }

            // 面积
            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLines(line);
                area.AddLine(line[line.Length - 1].X, r.Bottom, line[0].X, r.Bottom);
                area.CloseFigure();
                using (LinearGradientBrush lg = new LinearGradientBrush(
                           new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)),
                           Color.FromArgb(70, 120, 190, 255),
                           Color.FromArgb(0, 120, 190, 255), 90f))
                    g.FillPath(lg, area);
            }

            using (Pen pen = new Pen(Color.FromArgb(220, 140, 200, 255), 1.4f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, line);
            }

            // 最高/最低标注
            using (Font f = new Font("Microsoft YaHei UI", 7f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 190, 205, 225)))
            {
                g.DrawString(max.ToString("0.0"), f, b, r.X, r.Y - 1);
                g.DrawString(min.ToString("0.0"), f, b, r.X, r.Bottom - f.Height + 1);
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

        // ---------------- 交互 ----------------

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020) return;
            base.WndProc(ref m);
        }

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
                int nw = Math.Max(140, _resizeOrigin.Width + (cur.X - _resizeStart.X));
                int nh = Math.Max(90, _resizeOrigin.Height + (cur.Y - _resizeStart.Y));
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
                _st.BalX = Left; _st.BalY = Top;
                Raise();
                return;
            }
            if (_resizing)
            {
                _resizing = false;
                _st.BalW = Width; _st.BalH = Height;
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

            ToolStripMenuItem fs = new ToolStripMenuItem("字号");
            int[] sizes = { 18, 22, 26, 30, 34 };
            foreach (int s0 in sizes)
            {
                int sv = s0;
                ToolStripMenuItem si = new ToolStripMenuItem(sv + " pt");
                si.Checked = _st.BalFont == sv;
                si.Click += delegate { _st.BalFont = sv; Render(); Raise(); };
                fs.DropDownItems.Add(si);
            }
            m.Items.Add(fs);

            ToolStripMenuItem al = new ToolStripMenuItem("透明度");
            int[] alphas = { 120, 160, 200, 235, 255 };
            foreach (int a0 in alphas)
            {
                int av = a0;
                ToolStripMenuItem ai = new ToolStripMenuItem((av * 100 / 255) + "%");
                ai.Checked = _st.BalAlpha == av;
                ai.Click += delegate { _st.BalAlpha = av; Render(); Raise(); };
                al.DropDownItems.Add(ai);
            }
            m.Items.Add(al);

            ToolStripMenuItem rs = new ToolStripMenuItem("刷新间隔");
            int[] mins = { 1, 5, 15, 30, 60 };
            foreach (int m0 in mins)
            {
                int mv = m0;
                ToolStripMenuItem mi = new ToolStripMenuItem(mv + " 分钟");
                mi.Checked = _st.BalRefreshMin == mv;
                mi.Click += delegate
                {
                    _st.BalRefreshMin = mv;
                    _timer.Interval = mv * 60 * 1000;
                    Raise();
                };
                rs.DropDownItems.Add(mi);
            }
            m.Items.Add(rs);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("设置 API Key…", null, delegate
            {
                string v = Prompt("DeepSeek API Key", "Key（sk-...）：", "");
                if (v == null || v.Trim().Length == 0) return;
                try
                {
                    Directory.CreateDirectory(ConfigStore.Dir);
                    File.WriteAllText(BalanceData.ApiKeyPath, v.Trim(), new System.Text.UTF8Encoding(false));
                    Fetch();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "保存失败"); }
            });
            m.Items.Add("隐藏余额盒子", null, delegate
            {
                _st.BalVisible = false;
                Hide();
                Raise();
            });

            if (MenuAudit.On) { MenuAudit.Capture("余额盒子-菜单", m); return; }
            DeskGuard.RaiseToFront(Handle);
            m.Show(this, at);
            MenuWatch.Watch(m);

            DeskGuard.RaiseMenu(m.Handle);
        }

        private void Raise()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private static string Prompt(string title, string label, string initial)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(400, 108);
                f.MinimizeBox = false; f.MaximizeBox = false;
                f.Font = new Font("Microsoft YaHei UI", 9f);
                f.TopMost = true;

                Label l = new Label { Text = label, AutoSize = true, Location = new Point(14, 18) };
                f.Controls.Add(l);
                TextBox t = new TextBox { Text = initial, Location = new Point(14, 42), Width = 370, UseSystemPasswordChar = false };
                f.Controls.Add(t);
                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(228, 72), Size = new Size(74, 26) };
                f.Controls.Add(ok);
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(310, 72), Size = new Size(74, 26) };
                f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_st != null) { _st.BalX = Left; _st.BalY = Top; }
            Render();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DeskGuard.Unregister(this);
            base.OnFormClosed(e);
        }

    }
}
