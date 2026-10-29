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
    /// 桌面面板：分区固定 3×3 方形格子，超出分页；分区可拖动排序，图标可跨分区拖；
    /// 右上角设置按钮；解散分区的应用落到面板上的「未分组」区。
    /// </summary>
    public class PanelForm : Form
    {
        // ---------- Win32 ----------
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
        [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int WM_DROPFILES = 0x0233;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;

        /// <summary>调用 Windows shell 的标准动词（properties / runas / taskbarpin …），
        /// 让右键菜单保留系统的原生能力。</summary>
        private void ShellVerb(string verb, string path)
        {
            try
            {
                SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
                sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                sei.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI;
                sei.lpVerb = verb;
                sei.lpFile = path;
                sei.nShow = 1;
                if (!ShellExecuteEx(ref sei))
                {
                    int err = Marshal.GetLastWin32Error();
                    MessageBox.Show("系统拒绝了该操作（错误码 " + err + "）。", "桌面盒子",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "桌面盒子");
            }
        }

        // ---------- 布局 ----------
        private const int Pad = 11;
        private const int Gap = 12;
        private const int CellPadX = 13;
        private const int CellPadY = 13;
        private const int NameH = 24;
        private const int IconGapY = 9;
        private const int ItemLabelH = 14;
        private const int PerPage = 9;             // 小盒子固定 3×3 = 9 格，超出翻页

        /// <summary>大盒子里一行放几个小盒子（可配 2~6）。</summary>
        private int SectionCols
        {
            get { return Math.Max(2, Math.Min(6, _st.SectionCols)); }
        }

        /// <summary>大盒子显示几行小盒子（可配 2~6），超出滚动。</summary>
        private int SectionRows
        {
            get { return Math.Max(2, Math.Min(6, _st.SectionRows)); }
        }
        /// <summary>一行最多几个小盒子 = 用户设定的分区列数（受屏幕宽度限制）。</summary>
        private int MaxPerRow
        {
            get
            {
                int usable = Screen.PrimaryScreen.WorkingArea.Width - Pad * 2 - 40;
                int fits = Math.Max(1, (CellW() + Gap) > 0 ? usable / (CellW() + Gap) : 3);
                return Math.Min(SectionCols, Math.Max(2, fits));
            }
        }

        /// <summary>面板显示几行小盒子 = 用户设定的分区行数（受屏幕高度限制）。</summary>
        private int MaxRows
        {
            get
            {
                int usable = Screen.PrimaryScreen.WorkingArea.Height - 80 - TitleH - Pad * 2;
                int fits = Math.Max(1, (CellH() + Gap) > 0 ? usable / (CellH() + Gap) : 3);
                return Math.Min(SectionRows, Math.Max(2, fits));
            }
        }
        private const int TitleH = 32;             // 面板顶部标题栏
        private const int DragThreshold = 5;

        private AppState _st;
        private List<CellLayout> _layout = new List<CellLayout>();
        private Dictionary<string, int> _pageOf = new Dictionary<string, int>();
        private int _scrollY;
        private int _contentH;

        private int _hoverSec = -1, _hoverItem = -1, _hoverPageArrow = 0;
        private int _selSec = -1, _selItem = -1;

        private Rectangle _btnSettings;
        private Rectangle _btnLock;
        private bool _hoverLock;
        private const int ResizeBorder = 18;        // 右下角缩放热区
        private const int MinIconSize = 20;         // 缩放到最小：再小文字就看不清了
        private const int MaxIconSize = 96;
        private bool _resizing;
        private Point _resizeStart;
        private int _resizeStartIcon;
        private bool _hoverSettings;

        private bool _moving;
        private Point _moveStart, _moveOrigin;

        private int _dragKind;             // 0=无 1=图标 2=分区
        private bool _dragActive;
        private Point _dragOrigin;
        private int _dragSec = -1, _dragItem = -1;
        private int _dropSec = -1;         // 图标拖动时的目标分区
        private int _dropIndex = -1;       // 分区拖动时的目标位置

        private DateTime _lastClick = DateTime.MinValue;
        private int _lastSec = -1, _lastItem = -1;

        public event EventHandler Changed;
        public event EventHandler RequestAutoOrganize;

        private class CellLayout
        {
            public SectionDef Def;
            public Rectangle Box;       // 3×3 图标区
            public Rectangle Whole;     // 含名称行
            public bool IsLoose;        // 未分组区
        }

        public PanelForm(AppState st)
        {
            _st = st;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "桌面盒子";

            Relayout();
            StartKeepOnDesktop();
        }

        /// <summary>兜底：万一被最小化/隐藏，1.5 秒内自己回来。</summary>
        private void StartKeepOnDesktop()
        {
            Timer guard = new Timer();
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
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_guardOn) { _guardOn = true; DeskGuard.Register(this); }   // Win+D 后自动回到桌面
            DragAcceptFiles(Handle, true);
            Render();
        }

        public static void LogError(string where, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                File.AppendAllText(Path.Combine(ConfigStore.Dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                    ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace + "\r\n\r\n");
            }
            catch { }
        }

        // ================= 布局 =================

        private int ItemH()
        {
            return _st.IconSize + 4 + (_st.ShowLabels ? ItemLabelH : 0);
        }

        private int SlotW()
        {
            return Math.Max(62, _st.IconSize + 26);
        }

        private int CellW()
        {
            return SlotW() * 3 + CellPadX * 2;      // 小盒子固定 3 列
        }

        private int CellH()
        {
            return CellPadY * 2 + ItemH() * 3 + IconGapY * 2 + NameH;   // 小盒子固定 3 行
        }

        public void Relayout()
        {
            int cellW = CellW();
            int cellH = CellH();

            // 分区数（+1 表示未分组区）
            int total = _st.Sections.Count + (LooseCount() > 0 ? 1 : 0);
            if (total == 0) total = 1;

            int cols = Math.Min(MaxPerRow, total);
            if (cols < 1) cols = 1;

            int panelW = cols * (cellW + Gap) - Gap + Pad * 2;
            int rows = (total + cols - 1) / cols;
            int shownRows = Math.Min(rows, MaxRows);

            // 上下各留 Pad，顶部再加标题栏
            int panelH = TitleH + Pad + shownRows * (cellH + Gap) - Gap + Pad;
            _contentH = TitleH + Pad + rows * (cellH + Gap) - Gap + Pad;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (panelH > wa.Height - 80) panelH = wa.Height - 80;

            Bounds = new Rectangle(_st.PanelX, _st.PanelY, panelW, panelH);
            _btnSettings = new Rectangle(panelW - 38, (TitleH - 24) / 2, 24, 24);
            _btnLock = new Rectangle(panelW - 68, (TitleH - 24) / 2, 24, 24);

            _layout.Clear();
            int idx = 0;
            for (int i = 0; i < _st.Sections.Count; i++)
            {
                int col = idx % cols;
                int row = idx / cols;
                Rectangle box = new Rectangle(Pad + col * (cellW + Gap), TitleH + Pad + row * (cellH + Gap),
                                              cellW, cellH - NameH);
                CellLayout cl = new CellLayout();
                cl.Def = _st.Sections[i];
                cl.Box = box;
                cl.Whole = new Rectangle(box.X, box.Y, cellW, cellH);
                cl.IsLoose = false;
                _layout.Add(cl);
                idx++;
            }

            if (total > _st.Sections.Count)
            {
                int col = idx % cols;
                int row = idx / cols;
                Rectangle box = new Rectangle(Pad + col * (cellW + Gap), TitleH + Pad + row * (cellH + Gap),
                                              cellW, cellH - NameH);
                CellLayout cl = new CellLayout();
                cl.Def = null;
                cl.Box = box;
                cl.Whole = new Rectangle(box.X, box.Y, cellW, cellH);
                cl.IsLoose = true;
                _layout.Add(cl);
            }

            int maxScroll = Math.Max(0, _contentH - Height);
            if (_scrollY > maxScroll) _scrollY = maxScroll;
            if (_scrollY < 0) _scrollY = 0;
        }

        private int LooseCount()
        {
            return _st.LooseItems == null ? 0 : _st.LooseItems.Count;
        }

        /// <summary>
        /// 按「有效分」降序排好的显示列表：衰减分 × 0.5^(距上次使用天数/半衰期)，
        /// 已按 0.5 分桶做滞回；同分保持原顺序（稳定）。不改动 d.Items 本身。
        /// </summary>
        private List<BoxItem> ViewItems(SectionDef d)
        {
            List<BoxItem> result = new List<BoxItem>();
            if (d == null || d.Items == null || d.Items.Count == 0) return result;

            int n = d.Items.Count;
            int[] rank = new int[n];
            for (int i = 0; i < n; i++) rank[i] = _st.RankOf(d.Items[i].Path);

            List<int> order = new List<int>();
            for (int i = 0; i < n; i++) order.Add(i);

            order.Sort(delegate(int a, int b)
            {
                if (rank[a] != rank[b]) return rank[b].CompareTo(rank[a]); // 档位高的在前
                return a.CompareTo(b);                                     // 同档按原序，稳定
            });

            for (int i = 0; i < order.Count; i++) result.Add(d.Items[order[i]]);
            return result;
        }

        private int PageOf(SectionDef d)
        {
            if (d == null) return 0;
            int p;
            return _pageOf.TryGetValue(d.Id, out p) ? p : 0;
        }

        private int PageCount(int itemCount)
        {
            return Math.Max(1, (itemCount + PerPage - 1) / PerPage);
        }

        // ================= 绘制 =================


        /// <summary>调试用：设 DESKBOXES_DUMP_UI=目录 时，把每帧渲染结果存成 PNG。</summary>
        public static void DumpBitmap(Bitmap bmp, string tag)
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

        /// <summary>右下角的缩放提示（三道斜线），让用户知道这里能拖。</summary>
        private void DrawResizeGrip(Graphics g, int w, int h)
        {
            using (Pen pen = new Pen(Color.FromArgb(70, 240, 246, 255), 1f))
            {
                for (int i = 0; i < 3; i++)
                {
                    int o = 6 + i * 4;
                    g.DrawLine(pen, w - o, h - 6, w - 6, h - o);
                }
            }
        }

        private void Render()
        {
            try { RenderCore(); }
            catch (Exception ex) { LogError("Render", ex); }
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
                    DrawResizeGrip(g, w, h);
                }

                DumpBitmap(bmp, "panel");

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

        private void DrawAll(Graphics g, int w, int h)
        {
            // 面板底：颜色与透明度都来自配置
            Color bgColor = ParseColor(_st.PanelColor);
            int bgAlpha = Math.Max(30, Math.Min(255, _st.PanelAlpha));

            using (GraphicsPath p = RoundRect(new Rectangle(0, 0, w - 1, h - 1), 20))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(bgAlpha, bgColor)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(
                    Math.Min(255, bgAlpha / 3 + 40), 150, 180, 215), 1f))
                    g.DrawPath(pen, p);
            }

            // 顶部标题
            using (Font fTitle = new Font("Microsoft YaHei UI", _st.FontSize + 1.5f, FontStyle.Bold))
            {
                string title = string.IsNullOrEmpty(_st.PanelTitle) ? "桌面应用" : _st.PanelTitle;
                SizeF ts = g.MeasureString(title, fTitle);
                DrawOutlinedText(g, title, fTitle, Pad,
                    (TitleH - ts.Height) / 2f + 1, Color.FromArgb(232, 238, 246, 254));
            }

            DrawSettingsButton(g);
            DrawLockButton(g);

            g.TranslateTransform(0, -_scrollY);

            using (Font fSec = new Font("Microsoft YaHei UI", _st.FontSize + 1.5f, FontStyle.Regular))
            using (Font fItem = new Font("Microsoft YaHei UI", Math.Max(7f, _st.FontSize - 1f)))
            using (Font fPage = new Font("Microsoft YaHei UI", Math.Max(6.5f, _st.FontSize - 2f)))
            {
                for (int si = 0; si < _layout.Count; si++)
                {
                    CellLayout c = _layout[si];
                    if (c.Whole.Bottom - _scrollY < 0 || c.Whole.Top - _scrollY > h) continue;

                    bool isDropTarget = _dragActive && _dropSec == si;
                    bool isDragSource = _dragActive && _dragKind == 2 && _dragSec == si;

                    if (c.IsLoose) DrawLoose(g, c, si, fItem, fSec, isDropTarget);
                    else DrawSection(g, c, si, fItem, fSec, fPage, isDropTarget, isDragSource);
                }
            }

            g.ResetTransform();

            // 滚动条
            if (_contentH > h)
            {
                int barH = Math.Max(24, h * h / _contentH);
                int maxScroll = Math.Max(1, _contentH - h);
                int y = _scrollY * (h - barH) / maxScroll;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 220, 235, 250)))
                    g.FillRectangle(b, w - 6, y, 3, barH);
            }
        }

        private void DrawSettingsButton(Graphics g)
        {
            int a = _hoverSettings ? 90 : 40;
            using (GraphicsPath bp = RoundRect(_btnSettings, 7))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillPath(b, bp);

            // 三条横线（汉堡）
            float cx = _btnSettings.X + _btnSettings.Width / 2f;
            float cy = _btnSettings.Y + _btnSettings.Height / 2f;
            using (Pen pen = new Pen(Color.FromArgb(230, 214, 228, 244), 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - 6.5f, cy - 4.5f, cx + 6.5f, cy - 4.5f);
                g.DrawLine(pen, cx - 6.5f, cy, cx + 6.5f, cy);
                g.DrawLine(pen, cx - 6.5f, cy + 4.5f, cx + 6.5f, cy + 4.5f);
            }
        }

        /// <summary>标题栏「固定」按钮：图钉形状，锁定后变琥珀色实心。</summary>
        private void DrawLockButton(Graphics g)
        {
            int a = _hoverLock ? 90 : 40;
            using (GraphicsPath bp = RoundRect(_btnLock, 7))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillPath(b, bp);

            float cx = _btnLock.X + _btnLock.Width / 2f;
            float cy = _btnLock.Y + _btnLock.Height / 2f;
            bool locked = _st.PanelLocked;

            Color c = locked ? Color.FromArgb(240, 240, 190, 90) : Color.FromArgb(210, 214, 228, 244);

            using (Pen pen = new Pen(c, 1.6f))
            using (SolidBrush fill = new SolidBrush(c))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                // 锁体
                RectangleF body = new RectangleF(cx - 4.5f, cy - 0.5f, 9f, 7.5f);
                using (GraphicsPath p2 = RoundRect(Rectangle.Round(body), 2))
                {
                    if (locked) g.FillPath(fill, p2);
                    g.DrawPath(pen, p2);
                }

                // 锁环：锁定时闭合，未锁定时右侧留口
                if (locked)
                    g.DrawArc(pen, cx - 4f, cy - 7f, 8f, 9f, 180, 180);
                else
                    g.DrawArc(pen, cx - 3.5f, cy - 7f, 7f, 9f, 190, 150);
            }
        }

        private void DrawSection(Graphics g, CellLayout c, int si, Font fItem, Font fSec, Font fPage,
                                 bool isDropTarget, bool isDragSource)
        {
            Color accent = ParseColor(c.Def.Accent);
            bool hover = (si == _hoverSec);
            bool sel = (si == _selSec);

            using (GraphicsPath bp = RoundRect(c.Box, 14))
            {
                int fillA = isDropTarget ? 70 : (hover ? 40 : 26);
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255)))
                    g.FillPath(fb, bp);

                int borderA = isDropTarget ? 220 : (sel ? 150 : (hover ? 110 : 78));
                float bw = isDropTarget ? 2f : 1f;
                using (Pen pen = new Pen(Color.FromArgb(borderA, accent), bw))
                    g.DrawPath(pen, bp);
            }

            List<BoxItem> items = ViewItems(c.Def);
            int page = PageOf(c.Def);
            int pages = PageCount(items.Count);
            if (page >= pages) page = pages - 1;

            int start = page * PerPage;
            int slotW = SlotW();
            int itemH = ItemH();

            if (items.Count == 0)
            {
                using (Font fe = new Font("Microsoft YaHei UI", Math.Max(7f, _st.FontSize - 1.5f)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 220, 232, 244)))
                {
                    string tip = "把应用拖到这里";
                    SizeF ts = g.MeasureString(tip, fe);
                    g.DrawString(tip, fe, b,
                        c.Box.X + (c.Box.Width - ts.Width) / 2f,
                        c.Box.Y + (c.Box.Height - ts.Height) / 2f);
                }
            }
            else
            {
                int n = Math.Min(PerPage, items.Count - start);
                for (int k = 0; k < n; k++)
                {
                    int col = k % 3;
                    int row = k / 3;
                    int ix = c.Box.X + CellPadX + col * slotW;
                    int iy = c.Box.Y + CellPadY + row * (itemH + IconGapY);

                    int itemIndex = start + k;
                    bool itemSel = (si == _selSec && itemIndex == _selItem);
                    bool itemHover = (si == _hoverSec && itemIndex == _hoverItem);
                    bool dragging = _dragActive && _dragKind == 1 && _dragSec == si && _dragItem == itemIndex;

                    if ((itemSel || itemHover) && !dragging)
                    {
                        using (GraphicsPath hp = RoundRect(new Rectangle(ix + 1, iy - 1, slotW - 2, itemH + 2), 9))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(itemSel ? 52 : 26, 255, 255, 255)))
                            g.FillPath(b, hp);
                    }
                    if (dragging)
                    {
                        using (GraphicsPath hp = RoundRect(new Rectangle(ix + 1, iy - 1, slotW - 2, itemH + 2), 9))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 120, 190, 255)))
                            g.FillPath(b, hp);
                    }

                    BoxItem it = items[itemIndex];
                    Bitmap ic = IconLoader.Get(
                        string.IsNullOrEmpty(it.IconPath) ? it.Path : it.IconPath, _st.IconSize);
                    if (ic != null)
                    {
                        int dx = ix + (slotW - _st.IconSize) / 2;
                        g.DrawImage(ic, dx, iy, _st.IconSize, _st.IconSize);
                    }

                    if (_st.ShowLabels)
                    {
                        string nm = Ellipsis(g, it.DisplayName, fItem, slotW - 2);
                        SizeF ns = g.MeasureString(nm, fItem);
                        DrawOutlinedText(g, nm, fItem, ix + (slotW - ns.Width) / 2f,
                            iy + _st.IconSize + 3, Color.FromArgb(244, 236, 244, 253));
                    }
                }
            }

            // 名称行：名字 + 页码（页码紧跟在名字后面，不再画左右箭头）
            float ny = c.Box.Bottom + 4;
            string pageText = pages > 1 ? " " + (page + 1) + "/" + pages : "";
            SizeF nameSz = g.MeasureString(c.Def.Name, fSec);
            SizeF pageSz = g.MeasureString(pageText, fPage);
            float totalW = nameSz.Width + pageSz.Width;
            float sx = c.Box.X + (c.Box.Width - totalW) / 2f;

            DrawOutlinedText(g, c.Def.Name, fSec, sx, ny, Color.FromArgb(sel ? 255 : 236, accent));
            if (pages > 1)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(190, accent)))
                    g.DrawString(pageText, fPage, b, sx + nameSz.Width, ny + 3);
            }
        }

        /// <summary>名称行区域：点一下翻到下一页（循环）。</summary>
        private Rectangle NameRowRect(CellLayout c)
        {
            return new Rectangle(c.Box.X, c.Box.Bottom, c.Box.Width, NameH);
        }

        private int NameRowSectionAt(Point p)
        {
            Point q = new Point(p.X, p.Y + _scrollY);
            for (int si = 0; si < _layout.Count; si++)
            {
                CellLayout c = _layout[si];
                if (c.IsLoose) continue;
                if (NameRowRect(c).Contains(q)) return si;
            }
            return -1;
        }

        private void DrawLoose(Graphics g, CellLayout c, int si, Font fItem, Font fSec, bool isDropTarget)
        {
            Color accent = Color.FromArgb(160, 200, 212, 226);
            using (GraphicsPath bp = RoundRect(c.Box, 14))
            {
                int fillA = isDropTarget ? 60 : 18;
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255)))
                    g.FillPath(fb, bp);
                using (Pen pen = new Pen(Color.FromArgb(isDropTarget ? 200 : 90, accent), 1f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, bp);
                }
            }

            List<BoxItem> items = _st.LooseItems;
            int slotW = SlotW();
            int itemH = ItemH();
            int n = Math.Min(PerPage, items.Count);

            for (int k = 0; k < n; k++)
            {
                int col = k % 3;
                int row = k / 3;
                int ix = c.Box.X + CellPadX + col * slotW;
                int iy = c.Box.Y + CellPadY + row * (itemH + IconGapY);

                BoxItem it = items[k];
                Bitmap ic = IconLoader.Get(string.IsNullOrEmpty(it.IconPath) ? it.Path : it.IconPath, _st.IconSize);
                if (ic != null) g.DrawImage(ic, ix + (slotW - _st.IconSize) / 2, iy, _st.IconSize, _st.IconSize);

                if (_st.ShowLabels)
                {
                    string nm = Ellipsis(g, it.DisplayName, fItem, slotW - 2);
                    SizeF ns = g.MeasureString(nm, fItem);
                    DrawOutlinedText(g, nm, fItem, ix + (slotW - ns.Width) / 2f, iy + _st.IconSize + 3,
                        Color.FromArgb(240, 232, 240, 250));
                }
            }

            SizeF sns = g.MeasureString("未分组", fSec);
            DrawOutlinedText(g, "未分组", fSec, c.Box.X + (c.Box.Width - sns.Width) / 2f,
                c.Box.Bottom + 4, Color.FromArgb(220, 200, 212, 226));
        }

        private void DrawOutlinedText(Graphics g, string text, Font f, float x, float y, Color color)
        {
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(170, 6, 10, 18)))
            {
                g.DrawString(text, f, sh, x - 1, y);
                g.DrawString(text, f, sh, x + 1, y);
                g.DrawString(text, f, sh, x, y - 1);
                g.DrawString(text, f, sh, x, y + 1);
            }
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, f, b, x, y);
        }

        private static Color ParseColor(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); }
            catch { return Color.FromArgb(143, 180, 217); }
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

        // ================= 命中测试 =================

        private void HitTest(Point p, out int sec, out int item)
        {
            sec = -1; item = -1;
            Point q = new Point(p.X, p.Y + _scrollY);

            for (int si = 0; si < _layout.Count; si++)
            {
                CellLayout c = _layout[si];
                if (!c.Box.Contains(q)) continue;
                sec = si;

                List<BoxItem> items = c.IsLoose ? _st.LooseItems : ViewItems(c.Def);
                if (items == null || items.Count == 0) return;

                int page = c.IsLoose ? 0 : PageOf(c.Def);
                int start = page * PerPage;
                int slotW = SlotW();
                int itemH = ItemH();

                for (int k = 0; k < PerPage; k++)
                {
                    int itemIndex = start + k;
                    if (itemIndex >= items.Count) break;
                    int col = k % 3;
                    int row = k / 3;
                    Rectangle slot = new Rectangle(
                        c.Box.X + CellPadX + col * slotW,
                        c.Box.Y + CellPadY + row * (itemH + IconGapY),
                        slotW, itemH);
                    if (slot.Contains(q)) { item = itemIndex; return; }
                }
                return;
            }
        }

        // ================= 鼠标 =================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right) return;

            // 右上角：固定/解锁（手写命中判断）
            if (e.X >= _btnLock.X && e.X < _btnLock.X + _btnLock.Width &&
                e.Y >= _btnLock.Y && e.Y < _btnLock.Y + _btnLock.Height)
            {
                _st.PanelLocked = !_st.PanelLocked;
                Relayout();
                Render();
                RaiseChanged();
                return;
            }

            // 右下角：拖动缩放（锁定后不响应）
            if (!_st.PanelLocked && e.X >= Width - ResizeBorder && e.Y >= Height - ResizeBorder)
            {
                _resizing = true;
                _resizeStart = Cursor.Position;
                _resizeStartIcon = _st.IconSize;
                return;
            }

            if (_btnSettings.Contains(e.Location))
            {
                ShowSettingsMenu(e.Location);
                return;
            }

            int nsi = NameRowSectionAt(e.Location);
            if (nsi >= 0)
            {
                SectionDef d = _layout[nsi].Def;
                int pages = PageCount(ViewItems(d).Count);
                if (pages > 1)
                {
                    int np = (PageOf(d) + 1) % pages;      // 点一下翻到下一页，到底回第一页
                    _pageOf[d.Id] = np;
                    Render();
                }
                return;
            }

            int sec, item;
            HitTest(e.Location, out sec, out item);

            if (sec >= 0 && item >= 0)
            {
                _selSec = sec; _selItem = item;
                _hoverSec = sec; _hoverItem = item;

                // 双击启动
                if (_lastSec == sec && _lastItem == item &&
                    (DateTime.Now - _lastClick).TotalMilliseconds < 450)
                {
                    _lastClick = DateTime.MinValue;
                    Launch(sec, item);
                    return;
                }
                _lastClick = DateTime.Now;
                _lastSec = sec;
                _lastItem = item;

                _dragKind = 1;
                _dragSec = sec;
                _dragItem = item;
                _dragOrigin = e.Location;
                Render();
                return;
            }

            if (sec >= 0)
            {
                // 分区空白处：可能拖分区排序
                _selSec = sec; _selItem = -1;
                _dragKind = 2;
                _dragSec = sec;
                _dragItem = -1;
                _dragOrigin = e.Location;
                Render();
                return;
            }

            // 面板空白：拖动整个面板（锁定后不响应）
            _selSec = -1; _selItem = -1;
            if (_st.PanelLocked) return;
            _moving = true;
            _moveStart = Cursor.Position;
            _moveOrigin = new Point(Left, Top);
            Render();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 右下角缩放：横向或纵向拖动都算，6px 位移 ≈ 1px 图标
            if (_resizing && _st.PanelLocked)
            {
                _resizing = false;   // 锁定状态下强制中断正在进行的缩放
                return;
            }
            if (_resizing)
            {
                Point cur = Cursor.Position;
                int delta = Math.Max(cur.X - _resizeStart.X, cur.Y - _resizeStart.Y);
                int nsz = _resizeStartIcon + delta / 6;
                nsz = Math.Max(MinIconSize, Math.Min(MaxIconSize, nsz));
                if (nsz != _st.IconSize)
                {
                    _st.IconSize = nsz;
                    IconLoader.Clear();
                    Relayout();
                    Render();
                }
                return;
            }

            // 光标提示：右下角显示斜向缩放箭头
            if (_btnLock.Contains(e.Location) || _btnSettings.Contains(e.Location))
            {
                if (Cursor != Cursors.Hand) Cursor = Cursors.Hand;
            }
            else if (!_st.PanelLocked && e.X >= Width - ResizeBorder && e.Y >= Height - ResizeBorder)
            {
                if (Cursor != Cursors.SizeNWSE) Cursor = Cursors.SizeNWSE;
            }
            else if (Cursor == Cursors.SizeNWSE && _dragKind == 0 && !_moving)
            {
                Cursor = Cursors.Default;
            }

            if (_moving)
            {
                Point cur = Cursor.Position;
                _st.PanelX = _moveOrigin.X + (cur.X - _moveStart.X);
                _st.PanelY = _moveOrigin.Y + (cur.Y - _moveStart.Y);
                Location = new Point(_st.PanelX, _st.PanelY);
                Render();
                return;
            }

            // 拖动判定
            if (_dragKind != 0 && !_dragActive)
            {
                int dx = Math.Abs(e.X - _dragOrigin.X);
                int dy = Math.Abs(e.Y - _dragOrigin.Y);
                if (dx > DragThreshold || dy > DragThreshold) _dragActive = true;
            }

            if (_dragActive)
            {
                int sec, item;
                HitTest(e.Location, out sec, out item);
                int oldDrop = _dropSec;
                _dropSec = sec;
                if (_dragKind == 2) _dropIndex = sec;

                Cursor = Cursors.Hand;
                if (oldDrop != _dropSec) Render();
                else Render();
                return;
            }

            bool hoverSet = _btnSettings.Contains(e.Location);
            bool hoverLock = _btnLock.Contains(e.Location);
            if (hoverLock != _hoverLock) { _hoverLock = hoverLock; Render(); }
            int nsi = NameRowSectionAt(e.Location);

            int hs, hi;
            HitTest(e.Location, out hs, out hi);

            if (hoverSet != _hoverSettings || hs != _hoverSec || hi != _hoverItem || nsi != _hoverPageArrow)
            {
                _hoverSettings = hoverSet;
                _hoverSec = hs;
                _hoverItem = hi;
                _hoverPageArrow = nsi;

                if (hoverSet || nsi >= 0) Cursor = Cursors.Hand;
                else if (hs >= 0 && hi >= 0) Cursor = Cursors.Hand;
                else Cursor = Cursors.Default;

                Render();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_resizing)
            {
                _resizing = false;
                Render();
                RaiseChanged();
                return;
            }

            if (_moving)
            {
                _moving = false;
                RaiseChanged();
                return;
            }

            if (_dragActive)
            {
                int sec, item;
                HitTest(e.Location, out sec, out item);

                if (_dragKind == 1)
                {
                    // 图标拖动：移到目标分区
                    if (sec >= 0 && sec != _dragSec)
                        MoveItem(_dragSec, _dragItem, sec);
                    else if (sec == _dragSec && _dragSec >= 0)
                    {
                        // 分区内换位置：简单处理成"移到末尾"不做，忽略
                    }
                }
                else if (_dragKind == 2)
                {
                    // 分区排序
                    if (sec >= 0 && sec != _dragSec) ReorderSection(_dragSec, sec);
                }

                _dragKind = 0;
                _dragActive = false;
                _dragSec = -1; _dragItem = -1; _dropSec = -1; _dropIndex = -1;
                Relayout();
                Render();
                RaiseChanged();
                return;
            }

            _dragKind = 0;

            if (e.Button == MouseButtons.Right)
            {
                int sec, item;
                HitTest(e.Location, out sec, out item);
                if (sec >= 0 && item >= 0) ShowItemMenu(sec, item, e.Location);
                else if (sec >= 0) ShowSectionMenu(sec, e.Location);
                else ShowPanelMenu(e.Location);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // 滚轮在某个分区上：翻页；否则滚面板
            int sec, item;
            HitTest(e.Location, out sec, out item);
            if (sec >= 0 && sec < _layout.Count && !_layout[sec].IsLoose)
            {
                SectionDef d = _layout[sec].Def;
                int pages = PageCount(ViewItems(d).Count);
                if (pages > 1)
                {
                    int cur = PageOf(d);
                    int np = cur + (e.Delta < 0 ? 1 : -1);
                    if (np >= 0 && np < pages) { _pageOf[d.Id] = np; Render(); }
                    return;
                }
            }

            int maxScroll = Math.Max(0, _contentH - Height);
            if (maxScroll <= 0) return;
            _scrollY -= Math.Sign(e.Delta) * 48;
            if (_scrollY < 0) _scrollY = 0;
            if (_scrollY > maxScroll) _scrollY = maxScroll;
            Render();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverSec != -1 || _hoverItem != -1 || _hoverSettings)
            {
                _hoverSec = -1; _hoverItem = -1; _hoverSettings = false;
                Render();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        // ================= 操作 =================

        private void MoveItem(int fromSec, int itemIndex, int toSec)
        {
            if (fromSec < 0 || toSec < 0) return;
            CellLayout sc = _layout[fromSec];
            CellLayout tc = _layout[toSec];

            List<BoxItem> srcView = sc.IsLoose ? _st.LooseItems : ViewItems(sc.Def);
            if (itemIndex < 0 || itemIndex >= srcView.Count) return;

            BoxItem moved = srcView[itemIndex];
            List<BoxItem> src = sc.IsLoose ? _st.LooseItems : sc.Def.Items;
            src.Remove(moved);

            if (tc.IsLoose)
            {
                if (_st.LooseItems == null) _st.LooseItems = new List<BoxItem>();
                _st.LooseItems.Add(moved);
            }
            else
            {
                if (tc.Def.Items == null) tc.Def.Items = new List<BoxItem>();
                tc.Def.Items.Add(moved);
                // 跳到目标分区显示它的那一页
                _pageOf[tc.Def.Id] = (tc.Def.Items.Count - 1) / PerPage;
            }

            _selSec = -1; _selItem = -1;
        }

        private void ReorderSection(int from, int to)
        {
            if (from < 0 || to < 0) return;
            if (from >= _st.Sections.Count || to >= _st.Sections.Count) return;
            SectionDef s = _st.Sections[from];
            _st.Sections.RemoveAt(from);
            _st.Sections.Insert(to, s);
        }

        private void Launch(int sec, int item)
        {
            if (sec < 0 || sec >= _layout.Count) return;
            CellLayout c = _layout[sec];
            List<BoxItem> items = c.IsLoose ? _st.LooseItems : ViewItems(c.Def);
            if (items == null || item < 0 || item >= items.Count) return;

            string p = items[item].Path;
            _st.BumpClick(p);          // 统计点击频次，下次渲染自动靠前
            RaiseChanged();
            try
            {
                string low = p == null ? "" : p.ToLowerInvariant();
                if (low.StartsWith("shell:mycomputer"))
                {
                    // 必须用 CLSID 形式，shell:MyComputerFolder 交给 explorer 会报错
                    System.Diagnostics.Process.Start("explorer.exe",
                        "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                }
                else if (low.StartsWith("shell:recyclebin"))
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "shell:::{645FF040-5081-101B-9F08-00AA002F954E}");
                }
                else if (Directory.Exists(p))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\"");
                else
                    System.Diagnostics.Process.Start(p);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开：" + p + "\r\n\r\n" + ex.Message, "桌面盒子",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ================= 菜单 =================

        private void ShowSettingsMenu(Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);

            m.Items.Add("自动归类（整理桌面）…", null,
                delegate { if (RequestAutoOrganize != null) RequestAutoOrganize(this, EventArgs.Empty); });
            m.Items.Add("创建盒子…", null,
                delegate { if (RequestNewSection != null) RequestNewSection(this, EventArgs.Empty); });

            ToolStripMenuItem renTitle = new ToolStripMenuItem("重命名标题…");
            renTitle.Click += delegate
            {
                string v = Prompt("面板标题", "标题：", _st.PanelTitle);
                if (v == null) return;
                _st.PanelTitle = v.Trim().Length == 0 ? "桌面应用" : v.Trim();
                Render();
                RaiseChanged();
            };
            m.Items.Add(renTitle);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem bg = new ToolStripMenuItem("面板背景色");
            string[] colors = { "#16202C", "#1E1A2E", "#0F2027", "#202A1E", "#2A1E1E", "#1A1A1A", "#2C2418" };
            string[] cnames = { "深蓝灰", "深紫", "墨青", "深绿", "深棕", "碳黑", "暖褐" };
            for (int i = 0; i < colors.Length; i++)
            {
                string cc = colors[i];
                ToolStripMenuItem ci = new ToolStripMenuItem(cnames[i]);
                ci.Checked = string.Equals(_st.PanelColor, cc, StringComparison.OrdinalIgnoreCase);
                ci.Click += delegate { _st.PanelColor = cc; Render(); RaiseChanged(); };
                bg.DropDownItems.Add(ci);
            }
            m.Items.Add(bg);

            ToolStripMenuItem alpha = new ToolStripMenuItem("面板透明度");
            int[] alphas = { 100, 130, 160, 190, 215, 235, 255 };
            foreach (int a0 in alphas)
            {
                int av = a0;
                ToolStripMenuItem ai = new ToolStripMenuItem((av * 100 / 255) + "%");
                ai.Checked = _st.PanelAlpha == av;
                ai.Click += delegate { _st.PanelAlpha = av; Render(); RaiseChanged(); };
                alpha.DropDownItems.Add(ai);
            }
            m.Items.Add(alpha);

            ToolStripMenuItem custom = new ToolStripMenuItem("自定义颜色…");
            custom.Click += delegate
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.FullOpen = true;
                    cd.Color = ParseColor(_st.PanelColor);
                    if (cd.ShowDialog(this) != DialogResult.OK) return;
                    _st.PanelColor = "#" + cd.Color.R.ToString("X2") + cd.Color.G.ToString("X2") + cd.Color.B.ToString("X2");
                    Render();
                    RaiseChanged();
                }
            };
            m.Items.Add(custom);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem grid = new ToolStripMenuItem("大盒子布局");
            int[][] presets = new int[][]
            {
                new int[] { 3, 3 }, new int[] { 3, 4 }, new int[] { 4, 4 },
                new int[] { 4, 5 }, new int[] { 5, 5 }, new int[] { 5, 6 }, new int[] { 6, 6 }
            };
            for (int gi = 0; gi < presets.Length; gi++)
            {
                int gc = presets[gi][0];
                int gr = presets[gi][1];
                ToolStripMenuItem gitem = new ToolStripMenuItem(gc + " 列 × " + gr + " 行  （" + (gc * gr) + " 个小盒子/屏）");
                gitem.Checked = (_st.SectionCols == gc && _st.SectionRows == gr);
                gitem.Click += delegate
                {
                    _st.SectionCols = gc;
                    _st.SectionRows = gr;
                    _pageOf.Clear();
                    Relayout(); Render(); RaiseChanged();
                };
                grid.DropDownItems.Add(gitem);
            }
            m.Items.Add(grid);

            ToolStripMenuItem iconSz = new ToolStripMenuItem("图标大小");
            int[] sizes = { 28, 32, 36, 44, 52 };
            foreach (int s0 in sizes)
            {
                int sz = s0;
                ToolStripMenuItem si = new ToolStripMenuItem(sz + " px");
                si.Checked = _st.IconSize == sz;
                si.Click += delegate { _st.IconSize = sz; IconLoader.Clear(); Relayout(); Render(); RaiseChanged(); };
                iconSz.DropDownItems.Add(si);
            }
            m.Items.Add(iconSz);

            ToolStripMenuItem fs = new ToolStripMenuItem("字号");
            int[] fss = { 8, 9, 10, 11 };
            foreach (int f0 in fss)
            {
                int fv = f0;
                ToolStripMenuItem fi = new ToolStripMenuItem(fv + " pt");
                fi.Checked = _st.FontSize == fv;
                fi.Click += delegate { _st.FontSize = fv; Relayout(); Render(); RaiseChanged(); };
                fs.DropDownItems.Add(fi);
            }
            m.Items.Add(fs);

            ToolStripMenuItem pin = new ToolStripMenuItem(_st.PanelLocked ? "取消固定面板" : "固定面板（禁止拖动/缩放）");
            pin.Checked = _st.PanelLocked;
            pin.Click += delegate
            {
                _st.PanelLocked = !_st.PanelLocked;
                Relayout(); Render(); RaiseChanged();
            };
            m.Items.Add(pin);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem labels = new ToolStripMenuItem("显示应用名称");
            labels.Checked = _st.ShowLabels;
            labels.Click += delegate { _st.ShowLabels = !_st.ShowLabels; Relayout(); Render(); RaiseChanged(); };
            m.Items.Add(labels);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("刷新图标", null, delegate { IconLoader.Clear(); Render(); });

            DeskGuard.RaiseToFront(Handle);      // 面板先到最前，菜单才压得住其他 TOPMOST 盒子
            m.Show(this, at);

            DeskGuard.RaiseMenu(m.Handle);
        }

        public event EventHandler RequestNewSection;

        private void ShowPanelMenu(Point at)
        {
            ShowSettingsMenu(at);
        }

        private void ShowSectionMenu(int sec, Point at)
        {
            CellLayout c = _layout[sec];
            if (c.IsLoose)
            {
                ContextMenuStrip lm = new ContextMenuStrip();
                lm.Font = new Font("Microsoft YaHei UI", 9f);
                lm.Items.Add("把「未分组」的应用重新归类…", null,
                    delegate { if (RequestAutoOrganize != null) RequestAutoOrganize(this, EventArgs.Empty); });
                lm.Items.Add("新建盒子容纳它们…", null,
                    delegate { if (RequestNewSection != null) RequestNewSection(this, EventArgs.Empty); });
                DeskGuard.RaiseToFront(Handle);      // 面板先到最前，菜单才压得住其他 TOPMOST 盒子
                lm.Show(this, at);

            DeskGuard.RaiseMenu(lm.Handle);
                return;
            }

            SectionDef d = c.Def;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);

            m.Items.Add("往这个盒子添加应用…", null, delegate { AddByDialog(sec); });

            ToolStripMenuItem ren = new ToolStripMenuItem("重命名…");
            ren.Click += delegate
            {
                string v = Prompt("重命名盒子", "名称：", d.Name);
                if (v == null || v.Trim().Length == 0) return;
                d.Name = v.Trim();
                Relayout(); Render(); RaiseChanged();
            };
            m.Items.Add(ren);

            ToolStripMenuItem col = new ToolStripMenuItem("配色");
            string[] cs = { "#8FB4D9", "#9DC3A4", "#C3A6C8", "#C8A882", "#7FB6BC", "#A8ADB8", "#C89A9A" };
            string[] ns = { "淡蓝", "淡绿", "淡紫", "暖沙", "青蓝", "雾灰", "浅红" };
            for (int i = 0; i < cs.Length; i++)
            {
                string cc = cs[i];
                ToolStripMenuItem ci = new ToolStripMenuItem(ns[i]);
                ci.Click += delegate { d.Accent = cc; Render(); RaiseChanged(); };
                col.DropDownItems.Add(ci);
            }
            m.Items.Add(col);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("解散这个盒子（应用留在面板上）", null, delegate
            {
                if (MessageBox.Show("解散「" + d.Name + "」？\r\n\r\n应用不会丢，会落到面板的「未分组」区域。",
                        "桌面盒子", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                if (_st.LooseItems == null) _st.LooseItems = new List<BoxItem>();
                for (int i = 0; i < d.Items.Count; i++) _st.LooseItems.Add(d.Items[i]);
                _st.Sections.RemoveAt(sec);
                _selSec = -1; _selItem = -1;
                Relayout(); Render(); RaiseChanged();
            });

            DeskGuard.RaiseToFront(Handle);      // 面板先到最前，菜单才压得住其他 TOPMOST 盒子
            m.Show(this, at);

            DeskGuard.RaiseMenu(m.Handle);
        }

        private void ShowItemMenu(int sec, int item, Point at)
        {
            CellLayout c = _layout[sec];
            List<BoxItem> items = c.IsLoose ? _st.LooseItems : ViewItems(c.Def);
            if (item < 0 || item >= items.Count) return;
            BoxItem it = items[item];
            List<BoxItem> realList = c.IsLoose ? _st.LooseItems : c.Def.Items;

            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);
            m.Items.Add("打开", null, delegate { Launch(sec, item); });

            // ---- Windows 原生能力 ----
            string realPath = it.Path;
            bool isShellItem = realPath != null &&
                realPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            bool fileExists = !isShellItem && (File.Exists(realPath) || Directory.Exists(realPath));

            if (fileExists)
            {
                m.Items.Add("以管理员身份运行", null, delegate { ShellVerb("runas", realPath); });
                m.Items.Add("打开方式…", null, delegate
                {
                    try
                    {
                        System.Diagnostics.Process.Start("rundll32.exe",
                            "shell32.dll,OpenAs_RunDLL \"" + realPath + "\"");
                    }
                    catch { }
                });
                m.Items.Add("属性", null, delegate { ShellVerb("properties", realPath); });

                ToolStripMenuItem pin = new ToolStripMenuItem("固定");
                pin.DropDownItems.Add("到任务栏", null, delegate { ShellVerb("taskbarpin", realPath); });
                pin.DropDownItems.Add("到「开始」屏幕", null, delegate { ShellVerb("pintostartscreen", realPath); });
                m.Items.Add(pin);
            }

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem ren = new ToolStripMenuItem("改显示名…");
            ren.Click += delegate
            {
                string v = Prompt("改显示名", "名称：", it.DisplayName);
                if (v == null) return;
                it.Label = v.Trim();
                Relayout(); Render(); RaiseChanged();
            };
            m.Items.Add(ren);

            // 移到其他盒子
            if (_st.Sections.Count > 0)
            {
                ToolStripMenuItem mv = new ToolStripMenuItem("移到盒子");
                for (int i = 0; i < _layout.Count; i++)
                {
                    if (i == sec) continue;
                    CellLayout tc = _layout[i];
                    if (tc.IsLoose) continue;
                    int target = i;
                    ToolStripMenuItem mi = new ToolStripMenuItem(tc.Def.Name);
                    mi.Click += delegate
                    {
                        if (tc.Def.Items == null) tc.Def.Items = new List<BoxItem>();
                        tc.Def.Items.Add(it);
                        realList.Remove(it);
                        _selItem = -1;
                        Relayout(); Render(); RaiseChanged();
                    };
                    mv.DropDownItems.Add(mi);
                }
                m.Items.Add(mv);
            }

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("移除", null, delegate
            {
                realList.Remove(it);
                _selItem = -1;
                Relayout(); Render(); RaiseChanged();
            });

            DeskGuard.RaiseToFront(Handle);      // 面板先到最前，菜单才压得住其他 TOPMOST 盒子
            m.Show(this, at);

            DeskGuard.RaiseMenu(m.Handle);
        }

        private void AddByDialog(int sec)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "选择要放进盒子的程序或文件";
                d.Filter = "所有文件|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddPaths(new List<string>(d.FileNames), sec);
            }
        }

        private static string Prompt(string title, string label, string initial)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(340, 108);
                f.MinimizeBox = false; f.MaximizeBox = false;
                f.Font = new Font("Microsoft YaHei UI", 9f);
                f.TopMost = true;

                Label l = new Label { Text = label, AutoSize = true, Location = new Point(14, 18) };
                f.Controls.Add(l);
                TextBox t = new TextBox { Text = initial, Location = new Point(14, 42), Width = 310 };
                f.Controls.Add(t);
                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(168, 72), Size = new Size(74, 26) };
                f.Controls.Add(ok);
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(250, 72), Size = new Size(74, 26) };
                f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        // ================= 拖放 / 外部接口 =================

        protected override void WndProc(ref Message m)
        {
            // Win+D / 「显示桌面」会给所有顶层窗口发 SC_MINIMIZE，拦掉它盒子就不会被藏
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020)
                return;

            if (m.Msg == WM_DROPFILES)
            {
                IntPtr hDrop = m.WParam;
                try
                {
                    int lp = m.LParam.ToInt32();
                    Point screenPt = new Point(lp & 0xFFFF, (lp >> 16) & 0xFFFF);
                    Point client = PointToClient(screenPt);
                    int sec, item;
                    HitTest(client, out sec, out item);

                    uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                    List<string> paths = new List<string>();
                    for (uint i = 0; i < n; i++)
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        if (DragQueryFile(hDrop, i, sb, 1024) > 0) paths.Add(sb.ToString());
                    }
                    if (paths.Count > 0) AddPaths(paths, sec);
                }
                finally { DragFinish(hDrop); }
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>把路径放进指定分区；sec 指向未分组区则进 LooseItems。</summary>
        public void AddPaths(List<string> paths, int sec)
        {
            if (_st.Sections.Count == 0 && LooseCount() == 0)
            {
                SectionDef d = SectionDef.Create("新盒子");
                d.Accent = "#8FB4D9";
                _st.Sections.Add(d);
            }

            List<BoxItem> target;
            if (sec >= 0 && sec < _layout.Count && _layout[sec].IsLoose)
            {
                if (_st.LooseItems == null) _st.LooseItems = new List<BoxItem>();
                target = _st.LooseItems;
            }
            else
            {
                SectionDef d = (sec >= 0 && sec < _layout.Count)
                    ? _layout[sec].Def : _st.Sections[0];
                if (d.Items == null) d.Items = new List<BoxItem>();
                target = d.Items;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                bool dup = false;
                for (int k = 0; k < target.Count; k++)
                    if (string.Equals(target[k].Path, paths[i], StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) target.Add(new BoxItem { Path = paths[i] });
            }

            Relayout();
            Render();
            RaiseChanged();
        }

        public void AddSection(SectionDef d)
        {
            _st.Sections.Add(d);
            Relayout();
            Render();
            RaiseChanged();
        }

        /// <summary>自动归类后重建：清空散落项，全部进分区。</summary>
        public void ReplaceSections(List<SectionDef> sections)
        {
            _st.Sections = sections;
            _st.LooseItems = new List<BoxItem>();
            _pageOf.Clear();
            Relayout();
            Render();
        }

        public int RemoveVaultItems()
        {
            int n = 0;
            string vault = AutoOrganizer.VaultDir;
            for (int si = 0; si < _st.Sections.Count; si++)
            {
                List<BoxItem> items = _st.Sections[si].Items;
                if (items == null) continue;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i].Path != null && items[i].Path.StartsWith(vault, StringComparison.OrdinalIgnoreCase))
                    { items.RemoveAt(i); n++; }
                }
            }
            if (_st.LooseItems != null)
            {
                for (int i = _st.LooseItems.Count - 1; i >= 0; i--)
                {
                    if (_st.LooseItems[i].Path != null &&
                        _st.LooseItems[i].Path.StartsWith(vault, StringComparison.OrdinalIgnoreCase))
                    { _st.LooseItems.RemoveAt(i); n++; }
                }
            }
            if (n > 0) { Relayout(); Render(); }
            return n;
        }

        public void RefreshView()
        {
            IconLoader.Clear();
            Relayout();
            Render();
        }

        private void RaiseChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_st != null) { _st.PanelX = Left; _st.PanelY = Top; }
            Render();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DeskGuard.Unregister(this);
            base.OnFormClosed(e);
        }

    }
}
