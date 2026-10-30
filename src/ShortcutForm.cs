using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 快捷键盒子：显示 shortcuts.txt 里的「按键 = 功能」速查表，可增删改。
    /// </summary>
    public class ShortcutForm : Form
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
        private const int TitleH = 34;
        private const int RowH = 26;
        private const int Radius = 14;

        private AppState _st;
        private List<KeyValuePair<string, string>> _items = new List<KeyValuePair<string, string>>();
        private int _scroll;
        private int _hover = -1;

        private bool _moving;
        private Point _moveStart, _moveOrigin;
        private bool _resizing;
        private Point _resizeStart;
        private Size _resizeOrigin;
        private Rectangle _btnAdd;

        public event EventHandler Changed;

        public static string FilePath { get { return Path.Combine(ConfigStore.Dir, "shortcuts.txt"); } }

        public ShortcutForm(AppState st)
        {
            _st = st;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "快捷键盒子";

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Min(_st.KeyX, wa.Right - _st.KeyW - 20);
            int y = Math.Min(_st.KeyY, wa.Bottom - _st.KeyH - 20);
            if (x < 0) x = 40;
            if (y < 0) y = 120;
            Bounds = new Rectangle(x, y, _st.KeyW, _st.KeyH);

            LoadShortcuts();

            // Win+D 时不跟着隐藏
            System.Windows.Forms.Timer guard = new System.Windows.Forms.Timer();
            guard.Interval = 1500;
            guard.Tick += delegate
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized)
                        WindowState = FormWindowState.Normal;
                }
                catch { }
            };
            guard.Start();
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_guardOn) { _guardOn = true; DeskGuard.Register(this); }   // Win+D 后自动回到桌面
            Render();
        }

        // ---------------- 数据 ----------------

        public void LoadShortcuts()
        {
            _items.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim().Trim('\uFEFF');
                        if (line.Length == 0 || line.StartsWith("#")) continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Length > 0) _items.Add(new KeyValuePair<string, string>(k, v));
                    }
                }
            }
            catch { }
            Render();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _items.Count; i++)
                    sb.Append(_items[i].Key).Append(" = ").Append(_items[i].Value).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "快捷键盒子");
            }
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // ---------------- 绘制 ----------------

        private void Render()
        {
            try { RenderCore(); }
            catch (Exception ex) { PanelForm.LogError("ShortcutRender", ex); }
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

                DumpBitmap(bmp, "shortcut");

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
            Color bg = Hex(_st.KeyColor, Color.FromArgb(30, 30, 46));
            int alpha = Math.Max(40, Math.Min(255, _st.KeyAlpha));

            using (GraphicsPath p = RoundRect(new Rectangle(0, 0, w - 1, h - 1), Radius))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, bg)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(Math.Min(255, alpha / 2 + 40), 120, 150, 190), 1f))
                    g.DrawPath(pen, p);
            }

            // 标题 + 添加按钮
            using (Font ft = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 235, 242, 252)))
                g.DrawString("快捷键", ft, b, 12, (TitleH - ft.Height) / 2f + 2);

            _btnAdd = new Rectangle(w - 34, 6, 24, 22);
            using (GraphicsPath bp = RoundRect(_btnAdd, 6))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                g.FillPath(b, bp);
            using (Pen pen = new Pen(Color.FromArgb(220, 220, 232, 245), 1.6f))
            {
                float cx = _btnAdd.X + _btnAdd.Width / 2f;
                float cy = _btnAdd.Y + _btnAdd.Height / 2f;
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
            }

            // 分隔线
            using (Pen pen = new Pen(Color.FromArgb(50, 150, 175, 205), 1f))
                g.DrawLine(pen, 8, TitleH, w - 8, TitleH);

            // 列表
            int top = TitleH + 4;
            int viewH = h - top - 6;
            int visible = viewH / RowH + 1;

            using (Font fk = new Font("Consolas", _st.KeyFont - 0.5f))
            using (Font fv = new Font("Microsoft YaHei UI", _st.KeyFont - 0.5f))
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    int y = top + i * RowH - _scroll;
                    if (y + RowH < top || y > h) continue;

                    if (i == _hover)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                            g.FillRectangle(b, 6, y, w - 12, RowH - 2);
                    }

                    // 按键（等宽字体，右对齐固定宽度）
                    string key = _items[i].Key;
                    SizeF ks = g.MeasureString(key, fk);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 140, 200, 255)))
                        g.DrawString(key, fk, b, 12, y + (RowH - ks.Height) / 2f);

                    // 功能描述
                    string desc = _items[i].Value;
                    int descX = (int)(12 + Math.Max(110, ks.Width + 18));
                    string clipped = Ellipsis(g, desc, fv, w - descX - 14);
                    SizeF ds = g.MeasureString(clipped, fv);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 226, 234, 246)))
                        g.DrawString(clipped, fv, b, descX, y + (RowH - ds.Height) / 2f);
                }
            }

            if (_items.Count == 0)
            {
                using (Font fe = new Font("Microsoft YaHei UI", 8.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(130, 180, 195, 215)))
                    g.DrawString("点右上角 + 添加快捷键", fe, b, 14, TitleH + 12);
            }

            // 滚动条
            int totalH = _items.Count * RowH;
            if (totalH > viewH)
            {
                int barH = Math.Max(20, viewH * viewH / totalH);
                int maxScroll = Math.Max(1, totalH - viewH);
                int y = top + _scroll * (viewH - barH) / maxScroll;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 220, 235, 250)))
                    g.FillRectangle(b, w - 5, y, 3, barH);
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

        private static string Ellipsis(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (g.MeasureString(s, f).Width <= maxW) return s;
            for (int n = s.Length - 1; n > 0; n--)
            {
                string t = s.Substring(0, n) + "…";
                if (g.MeasureString(t, f).Width <= maxW) return t;
            }
            return "…";
        }

        // ---------------- 交互 ----------------

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020) return;
            base.WndProc(ref m);
        }

        private int RowAt(Point p)
        {
            if (p.Y < TitleH + 4) return -1;
            int idx = (p.Y - TitleH - 4 + _scroll) / RowH;
            return (idx >= 0 && idx < _items.Count) ? idx : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) return;

            if (_btnAdd.Contains(e.Location)) { AddItem(); return; }

            if (e.X >= Width - 12 && e.Y >= Height - 12)
            {
                _resizing = true;
                _resizeStart = Cursor.Position;
                _resizeOrigin = Size;
                return;
            }
            if (e.Y < TitleH)
            {
                _moving = true;
                _moveStart = Cursor.Position;
                _moveOrigin = new Point(Left, Top);
            }
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
                int nw = Math.Max(180, _resizeOrigin.Width + (cur.X - _resizeStart.X));
                int nh = Math.Max(120, _resizeOrigin.Height + (cur.Y - _resizeStart.Y));
                Size = new Size(nw, nh);
                Render();
                return;
            }

            int r = RowAt(e.Location);
            if (r != _hover) { _hover = r; Render(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_moving)
            {
                _moving = false;
                _st.KeyX = Left; _st.KeyY = Top;
                Raise();
                return;
            }
            if (_resizing)
            {
                _resizing = false;
                _st.KeyW = Width; _st.KeyH = Height;
                Raise();
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                ShowMenu(e.Location);
                return;
            }

            // 左键点条目：可能是拖动或查看，这里不处理（速查表）
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int viewH = Height - TitleH - 10;
            int totalH = _items.Count * RowH;
            int maxScroll = Math.Max(0, totalH - viewH);
            if (maxScroll <= 0) return;

            _scroll -= Math.Sign(e.Delta) * RowH;
            if (_scroll < 0) _scroll = 0;
            if (_scroll > maxScroll) _scroll = maxScroll;
            Render();
        }

        private void ShowMenu(Point at)
        {
            int idx = RowAt(at);

            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);

            m.Items.Add("添加快捷键…", null, delegate { AddItem(); });

            if (idx >= 0)
            {
                m.Items.Add("编辑这一条…", null, delegate { EditItem(idx); });
                m.Items.Add("删除这一条", null, delegate
                {
                    _items.RemoveAt(idx);
                    Save();
                    Render();
                });
                m.Items.Add(new ToolStripSeparator());
            }

            ToolStripMenuItem fs = new ToolStripMenuItem("字号");
            int[] sizes = { 9, 10, 11, 12, 14 };
            foreach (int s0 in sizes)
            {
                int sv = s0;
                ToolStripMenuItem si = new ToolStripMenuItem(sv + " pt");
                si.Checked = _st.KeyFont == sv;
                si.Click += delegate { _st.KeyFont = sv; Render(); Raise(); };
                fs.DropDownItems.Add(si);
            }
            m.Items.Add(fs);

            ToolStripMenuItem al = new ToolStripMenuItem("透明度");
            int[] alphas = { 120, 160, 200, 235, 255 };
            foreach (int a0 in alphas)
            {
                int av = a0;
                ToolStripMenuItem ai = new ToolStripMenuItem((av * 100 / 255) + "%");
                ai.Checked = _st.KeyAlpha == av;
                ai.Click += delegate { _st.KeyAlpha = av; Render(); Raise(); };
                al.DropDownItems.Add(ai);
            }
            m.Items.Add(al);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("重新载入 shortcuts.txt", null, delegate { LoadShortcuts(); });
            m.Items.Add("隐藏快捷键盒子", null, delegate
            {
                _st.KeyVisible = false;
                Hide();
                Raise();
            });

            DeskGuard.RaiseToFront(Handle);
            m.Show(this, at);

            DeskGuard.RaiseMenu(m.Handle);
        }

        private void AddItem()
        {
            KeyValuePair<string, string>? kv = KeyCaptureDialog(null, null);
            if (kv == null) return;
            _items.Add(kv.Value);
            Save();
            Render();
        }

        private void EditItem(int idx)
        {
            if (idx < 0 || idx >= _items.Count) return;
            KeyValuePair<string, string>? kv = KeyCaptureDialog(_items[idx].Key, _items[idx].Value);
            if (kv == null) return;
            _items[idx] = kv.Value;
            Save();
            Render();
        }

        /// <summary>弹窗：按键框里直接按组合键就会捕捉。</summary>
        public static KeyValuePair<string, string>? KeyCaptureDialog(string key, string desc)
        {
            using (Form f = new Form())
            {
                f.Text = key == null ? "添加快捷键" : "编辑快捷键";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(420, 150);
                f.MinimizeBox = false; f.MaximizeBox = false;
                f.Font = new Font("Microsoft YaHei UI", 9f);
                f.TopMost = true;

                Label l1 = new Label { Text = "按键（焦点在这个框里直接按组合键）", AutoSize = true, Location = new Point(14, 14) };
                f.Controls.Add(l1);

                TextBox tk = new TextBox { Text = key ?? "", Location = new Point(14, 38), Width = 390, Font = new Font("Consolas", 11f) };
                f.Controls.Add(tk);

                Label l2 = new Label { Text = "功能说明", AutoSize = true, Location = new Point(14, 70) };
                f.Controls.Add(l2);

                TextBox td = new TextBox { Text = desc ?? "", Location = new Point(14, 92), Width = 250 };
                f.Controls.Add(td);

                tk.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Back) { tk.Text = ""; e.SuppressKeyPress = true; return; }
                    if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.ControlKey ||
                        e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                        return;

                    List<string> parts = new List<string>();
                    if (e.Control) parts.Add("Ctrl");
                    if (e.Alt) parts.Add("Alt");
                    if (e.Shift) parts.Add("Shift");

                    string k;
                    if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                        k = ((char)('A' + (e.KeyCode - Keys.A))).ToString();
                    else if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
                        k = ((char)('0' + (e.KeyCode - Keys.D0))).ToString();
                    else
                        k = e.KeyCode.ToString();

                    parts.Add(k);
                    tk.Text = string.Join("+", parts.ToArray());
                    e.SuppressKeyPress = true;
                };

                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(248, 116), Size = new Size(74, 26) };
                f.Controls.Add(ok);
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(330, 116), Size = new Size(74, 26) };
                f.Controls.Add(cancel);

                f.AcceptButton = ok; f.CancelButton = cancel;
                f.ActiveControl = tk;

                if (f.ShowDialog() != DialogResult.OK) return null;
                if (tk.Text.Trim().Length == 0) return null;

                return new KeyValuePair<string, string>(tk.Text.Trim(), td.Text.Trim());
            }
        }

        private void Raise()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_st != null) { _st.KeyX = Left; _st.KeyY = Top; }
            Render();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DeskGuard.Unregister(this);
            base.OnFormClosed(e);
        }

    }
}
