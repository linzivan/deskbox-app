using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeskBoxes
{

    /// <summary>托盘常驻 + 面板生命周期。</summary>
    public class TrayContext : ApplicationContext
    {
        private AppState _st;
        private PanelForm _panel;
        private BalanceForm _balance;
        private HermesBox _hermes;
        private ShortcutForm _shortcuts;
        private TrayNotify _tray;
        private Icon _trayIcon;
        private readonly System.Windows.Forms.Timer _saveTimer;
        private System.Windows.Forms.Timer _testQuitTimer;
        private bool _dirty;

        public TrayContext()
        {
            _st = ConfigStore.Load();
            if (ConfigStore.LastLoadMigrated) { try { ConfigStore.Save(_st); } catch { } }

            // 上次退出已把快捷方式还原到桌面；启动时按原布局把它们接回仓库。
            try
            {
                int removed;
                if (AutoOrganizer.Relink(_st.Sections, out removed) > 0 || removed > 0) _dirty = true;
            }
            catch { }

            _saveTimer = new System.Windows.Forms.Timer();
            _saveTimer.Interval = 600;
            _saveTimer.Tick += delegate
            {
                if (!_dirty) return;
                _dirty = false;
                ConfigStore.Save(_st);
            };
            _saveTimer.Start();

            Trace.Start();          // 层级问题追踪：%APPDATA%\DeskBoxes	race.log

            BuildTray();
            BuildPanel();
            BuildWidgets();

            // 关机/注销时也把快捷方式还原回桌面，避免下次开机桌面空空如也。
            Microsoft.Win32.SystemEvents.SessionEnding += delegate
            {
                try { int skipped; AutoOrganizer.Restore(out skipped); } catch { }
            };

            if (_st.HideAll) _panel.Hide();

            // BuildTray 早于 BuildPanel/BuildWidgets 执行，第一次构建的菜单里四个盒子还没建出来，
            // 状态一定是错的 —— 盒子都建好后再刷一次。
            RefreshTrayMenu();
        }

        private void BuildPanel()
        {
            _panel = new PanelForm(_st);
            _panel.Changed += delegate { _dirty = true; };
            _panel.RequestNewSection += delegate { NewSection(); };
            _panel.RequestAutoOrganize += delegate { OrganizeDesktop(); };
            _panel.Show();
            _panel.RefreshView();
        }

        /// <summary>余额盒子 + 快捷键盒子（同一个进程，各自独立浮窗）。</summary>
        private void BuildWidgets()
        {
            try
            {
                _balance = new BalanceForm(_st);
                _balance.Changed += delegate { _dirty = true; };
                if (_st.BalVisible) _balance.Show();
            }
            catch (Exception ex) { PanelForm.LogError("BuildBalance", ex); }

            try
            {
                _hermes = new HermesBox(_st);
                _hermes.Changed += delegate { _dirty = true; };
                if (_st.HermesVisible) _hermes.Show();
            }
            catch (Exception ex) { PanelForm.LogError("BuildHermes", ex); }

            try
            {
                _shortcuts = new ShortcutForm(_st);
                _shortcuts.Changed += delegate { _dirty = true; };
                if (_st.KeyVisible) _shortcuts.Show();
            }
            catch (Exception ex) { PanelForm.LogError("BuildShortcut", ex); }
        }

        // ---------------- 托盘 ----------------

        private void BuildTray()
        {
            try
            {
                _trayIcon = MakeTrayIcon();
                _tray = new TrayNotify("桌面盒子", _trayIcon, true);
                _tray.DoubleClick = delegate { TogglePanel(); };

                RefreshTrayMenu();      // 先把菜单挂好

                bool ok = _tray.Show();
                Console.WriteLine("[TRAY] Shell_NotifyIcon ok=" + ok
                    + " visible=" + _tray.Visible
                    + " err=" + (_tray.LastError ?? "-")
                    + " icon=" + (_trayIcon != null ? _trayIcon.Width + "x" + _trayIcon.Height : "null"));
                Console.Out.Flush();
                if (!ok) PanelForm.LogError("BuildTray", new Exception("Shell_NotifyIcon 失败: " + _tray.LastError));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TRAY] EX " + ex.GetType().Name + ": " + ex.Message);
                Console.Out.Flush();
                PanelForm.LogError("BuildTray", ex);
            }
        }

        private void RefreshTrayMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);

            m.Items.Add("新建分区…", null, delegate { NewSection(); });
            m.Items.Add("自动整理桌面…", null, delegate { OrganizeDesktop(); });

            int restorable = AutoOrganizer.RestorableCount();
            ToolStripMenuItem restore = new ToolStripMenuItem(
                restorable > 0 ? "还原本地快捷方式到桌面（" + restorable + "）" : "还原本地快捷方式到桌面");
            restore.Enabled = restorable > 0;
            restore.Click += delegate { RestoreShortcuts(); };
            m.Items.Add(restore);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildBoxItem(0));
            m.Items.Add(BuildBoxItem(1));
            m.Items.Add(BuildBoxItem(2));
            m.Items.Add(BuildBoxItem(3));
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem secs = new ToolStripMenuItem("分区（" + _st.Sections.Count + "）");
            for (int i = 0; i < _st.Sections.Count; i++)
            {
                SectionDef d = _st.Sections[i];
                ToolStripMenuItem si = new ToolStripMenuItem(d.Name + "（" + d.Items.Count + "）");
                si.Click += delegate { if (_panel != null && !_panel.Visible) _panel.Show(); };
                secs.DropDownItems.Add(si);
            }
            m.Items.Add(secs);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem opt = new ToolStripMenuItem("设置");

            ToolStripMenuItem iconSz = new ToolStripMenuItem("图标大小");
            int[] sizes = { 28, 32, 36, 44, 52 };
            foreach (int s0 in sizes)
            {
                int sz = s0;
                ToolStripMenuItem si = new ToolStripMenuItem(sz + " px");
                si.Checked = _st.IconSize == sz;
                si.Click += delegate
                {
                    _st.IconSize = sz;
                    if (_panel != null) _panel.RefreshView();
                    _dirty = true; RefreshTrayMenu();
                };
                iconSz.DropDownItems.Add(si);
            }
            opt.DropDownItems.Add(iconSz);

            ToolStripMenuItem labels = new ToolStripMenuItem("显示应用名称");
            labels.Checked = _st.ShowLabels;
            labels.Click += delegate
            {
                _st.ShowLabels = !_st.ShowLabels;
                if (_panel != null) _panel.RefreshView();
                _dirty = true; RefreshTrayMenu();
            };
            opt.DropDownItems.Add(labels);

            ToolStripMenuItem fs = new ToolStripMenuItem("字号");
            int[] fss = { 8, 9, 10, 11 };
            foreach (int f0 in fss)
            {
                int fv = f0;
                ToolStripMenuItem fi = new ToolStripMenuItem(fv + " pt");
                fi.Checked = _st.FontSize == fv;
                fi.Click += delegate
                {
                    _st.FontSize = fv;
                    if (_panel != null) _panel.RefreshView();
                    _dirty = true; RefreshTrayMenu();
                };
                fs.DropDownItems.Add(fi);
            }
            opt.DropDownItems.Add(fs);

            ToolStripMenuItem boot = new ToolStripMenuItem("开机自动启动");
            boot.Checked = IsAutoStart();
            boot.Click += delegate { SetAutoStart(!IsAutoStart()); RefreshTrayMenu(); };
            opt.DropDownItems.Add(boot);

            opt.DropDownItems.Add(new ToolStripSeparator());
            opt.DropDownItems.Add("打开配置文件夹", null,
                delegate { System.Diagnostics.Process.Start("explorer.exe", ConfigStore.Dir); });

            m.Items.Add(opt);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, delegate { Quit(); });

            m.Opening += delegate { SyncBoxItems(); };   // 每次打开前重算：任何来源的显隐变化都能同步
            _tray.Menu = m;
        }

        // ================= 盒子显隐（唯一入口） =================
        // 约定：勾选 = **用户意图**（配置），不是当前窗口可见性。
        // 意图为「显示」但实际不可见时，在文字后注明原因（Hermes 未运行等），
        // 这样"勾着但看不见"有解释，而不是让勾选跟着自动隐藏一起灭掉。
        private ToolStripMenuItem _mPanel, _mBal, _mKey, _mHermes;

        private static readonly string[] BoxNames = { "桌面应用盒子", "余额盒子", "快捷键盒子", "Hermes 盒子" };

        private bool BoxIntent(int kind)
        {
            switch (kind)
            {
                case 0: return _panel != null && !_st.HideAll;
                case 1: return _st.BalVisible;
                case 2: return _st.KeyVisible;
                default: return _st.HermesVisible;
            }
        }

        private bool BoxActual(int kind)
        {
            switch (kind)
            {
                case 0: return _panel != null && _panel.Visible;
                case 1: return _balance != null && _balance.Visible;
                case 2: return _shortcuts != null && _shortcuts.Visible;
                default: return _hermes != null && _hermes.Visible;
            }
        }

        /// <summary>唯一入口：改意图（仅用户操作）→ 应用可见性 → 刷新菜单。</summary>
        internal void SetBoxVisible(int kind, bool on, bool byUser)
        {
            try
            {
                if (byUser)
                {
                    if (kind == 0) _st.HideAll = !on;
                    else if (kind == 1) _st.BalVisible = on;
                    else if (kind == 2) _st.KeyVisible = on;
                    else _st.HermesVisible = on;
                }

                if (kind == 0 && _panel != null) { if (on) _panel.Show(); else _panel.Hide(); }
                else if (kind == 1 && _balance != null) { if (on) _balance.Show(); else _balance.Hide(); }
                else if (kind == 2 && _shortcuts != null) { if (on) _shortcuts.Show(); else _shortcuts.Hide(); }
                else if (kind == 3 && _hermes != null)
                {
                    if (on) _hermes.Fetch();     // 有数据时 Fetch 内部会 Show
                    else _hermes.Hide();
                }

                _dirty = true;
                RefreshTrayMenu();
            }
            catch (Exception ex) { PanelForm.LogError("SetBoxVisible", ex); }
        }

        private ToolStripMenuItem BuildBoxItem(int kind)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(BoxNames[kind]);
            it.Click += delegate { SetBoxVisible(kind, !BoxIntent(kind), true); };

            if (kind == 0) _mPanel = it;
            else if (kind == 1) _mBal = it;
            else if (kind == 2) _mKey = it;
            else _mHermes = it;

            SyncBoxItem(it, kind);
            return it;
        }

        private void SyncBoxItem(ToolStripMenuItem it, int kind)
        {
            bool intent = BoxIntent(kind);
            bool actual = BoxActual(kind);
            it.Checked = intent;

            string suffix = "";
            if (intent && !actual) suffix = (kind == 3) ? "（未运行）" : "（未显示）";
            it.Text = BoxNames[kind] + suffix;
            it.ForeColor = suffix.Length > 0 ? SystemColors.GrayText : SystemColors.ControlText;
        }

        // 测试钩子用：模拟 Hermes 无数据自动隐藏（只改实际可见性，不动用户意图）
        internal void HermesHideForTest() { if (_hermes != null) _hermes.Hide(); }

        internal void DumpTrayBoxItems(string tag)
        {
            SyncBoxItems();   // 等价于用户打开菜单时的 Opening 事件
            Console.WriteLine("--- " + tag);
            if (_tray == null || _tray.Menu == null) { Console.WriteLine("  (no menu)"); Console.Out.Flush(); return; }
            foreach (ToolStripItem it in _tray.Menu.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                for (int k = 0; k < BoxNames.Length; k++)
                {
                    if (mi.Text.StartsWith(BoxNames[k]))
                    {
                        Console.WriteLine("  [" + (mi.Checked ? "x" : " ") + "] " + mi.Text
                            + "   (intent=" + BoxIntent(k) + " actual=" + BoxActual(k) + ")");
                        break;
                    }
                }
            }
            Console.Out.Flush();
        }

        private void SyncBoxItems()
        {
            if (_mPanel != null) SyncBoxItem(_mPanel, 0);
            if (_mBal != null) SyncBoxItem(_mBal, 1);
            if (_mKey != null) SyncBoxItem(_mKey, 2);
            if (_mHermes != null) SyncBoxItem(_mHermes, 3);
        }

        private void TogglePanel()
        {
            SetBoxVisible(0, !BoxIntent(0), true);   // 统一走单一入口
        }

        private void NewSection()
        {
            string name = PromptText("新建分区", "分区名称：", "新分区");
            if (name == null || name.Trim().Length == 0) return;

            SectionDef d = SectionDef.Create(name.Trim());
            if (_panel != null) _panel.AddSection(d);
            else { _st.Sections.Add(d); }

            _dirty = true;
            RefreshTrayMenu();
        }

        /// <summary>
        /// 自动归类：预览 -> 移动桌面文件 -> 按文件名重新分类 -> 全部塞进盒子（不留散落）。
        /// 手动拖出来的布局不会被记住，每次自动归类都以规则为准。
        /// </summary>
        private void OrganizeDesktop()
        {
            List<AutoOrganizer.PlanItem> plan = AutoOrganizer.Scan(true);

            // 桌面没有可整理的也不算错：可能只是想把已有的重新归一遍
            if (plan.Count > 0)
            {
                using (OrganizeForm f = new OrganizeForm(plan))
                {
                    if (f.ShowDialog() != DialogResult.OK || !f.Confirmed) return;
                    if (f.IncludeNonShortcuts) plan = AutoOrganizer.Scan(false);
                }

                List<MoveRecord> recs;
                Dictionary<string, List<string>> moved = AutoOrganizer.Execute(plan, out recs);
                int failed = plan.Count - recs.Count;
                if (failed > 0)
                    MessageBox.Show(failed + " 个文件移动失败（可能被占用），仍留在桌面。", "自动归类",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            List<SectionDef> sections = RebuildFromVault();
            if (_panel != null) _panel.ReplaceSections(sections);

            _dirty = true;
            ConfigStore.Save(_st);
            RefreshTrayMenu();

            int total = 0;
            for (int i = 0; i < sections.Count; i++) total += sections[i].Items.Count;
            MessageBox.Show("归类完成：\r\n\r\n" + sections.Count + " 个盒子，共 " + total + " 个应用。\r\n\r\n" +
                "手动拖动的布局不会被记住，下次自动归类仍按规则重排。",
                "自动归类", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>扫 Vault 全部文件 -> 按文件名分类 -> 合并小类 -> 补系统项 -> 返回分区列表。</summary>
        private List<SectionDef> RebuildFromVault()
        {
            List<string> files = new List<string>();
            if (Directory.Exists(AutoOrganizer.VaultDir))
            {
                string[] dirs = Directory.GetDirectories(AutoOrganizer.VaultDir);
                for (int i = 0; i < dirs.Length; i++) files.AddRange(Directory.GetFiles(dirs[i]));
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<string>> byCat = new Dictionary<string, List<string>>();
            Dictionary<string, string> accents = new Dictionary<string, string>();
            for (int i = 0; i < files.Count; i++)
            {
                string sec, acc;
                AutoOrganizer.MatchSection(Path.GetFileNameWithoutExtension(files[i]), out sec, out acc);
                if (!byCat.ContainsKey(sec)) { byCat[sec] = new List<string>(); accents[sec] = acc; }
                byCat[sec].Add(files[i]);
            }

            // 成员太少的并入「其他」
            List<string> tiny = new List<string>();
            foreach (KeyValuePair<string, List<string>> kv in byCat)
                if (kv.Key != AutoOrganizer.OtherSection && kv.Value.Count < 2) tiny.Add(kv.Key);
            for (int i = 0; i < tiny.Count; i++)
            {
                if (!byCat.ContainsKey(AutoOrganizer.OtherSection))
                {
                    byCat[AutoOrganizer.OtherSection] = new List<string>();
                    accents[AutoOrganizer.OtherSection] = "#A8ADB8";
                }
                byCat[AutoOrganizer.OtherSection].AddRange(byCat[tiny[i]]);
                byCat.Remove(tiny[i]);
            }

            // 「此电脑」「回收站」固定放进系统工具
            if (!byCat.ContainsKey("系统工具"))
            {
                byCat["系统工具"] = new List<string>();
                accents["系统工具"] = "#A8B0BA";
            }
            byCat["系统工具"].Insert(0, "shell:RecycleBinFolder");
            byCat["系统工具"].Insert(0, "shell:MyComputerFolder");

            // 按规则固定顺序输出，保证每次归类后布局一致
            List<string> keys = new List<string>();
            foreach (KeyValuePair<string, List<string>> kv in byCat) keys.Add(kv.Key);
            keys.Sort(delegate(string x, string y)
            {
                int ix = AutoOrganizer.OrderIndex(x), iy = AutoOrganizer.OrderIndex(y);
                if (ix != iy) return ix.CompareTo(iy);
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            });

            List<SectionDef> list = new List<SectionDef>();
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                List<string> vals = byCat[key];
                SectionDef s = SectionDef.Create(key);
                s.Accent = accents.ContainsKey(key) ? accents[key] : "#A8ADB8";
                for (int i = 0; i < vals.Count; i++)
                    s.Items.Add(new BoxItem { Path = vals[i] });
                list.Add(s);
            }
            return list;
        }

        /// <summary>把整理走的快捷方式全部移回桌面，并清掉分区里的条目。</summary>
        private void RestoreShortcuts()
        {
            int n = AutoOrganizer.RestorableCount();
            if (n == 0)
            {
                MessageBox.Show("没有可还原的快捷方式。", "还原", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show("将把 " + n + " 个快捷方式移回桌面，并从分区里移除对应条目。\r\n\r\n继续吗？",
                    "还原到桌面", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            int skipped;
            int ok = AutoOrganizer.Restore(out skipped);

            int removed = _panel != null ? _panel.RemoveVaultItems() : 0;
            _dirty = true;
            ConfigStore.Save(_st);
            RefreshTrayMenu();

            string msg = "已还原 " + ok + " 个快捷方式到桌面。";
            if (skipped > 0) msg += "\r\n" + skipped + " 个跳过（文件已不存在或被占用）。";
            if (removed > 0) msg += "\r\n分区里移除了 " + removed + " 个条目。";
            MessageBox.Show(msg, "还原到桌面", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- 开机自启 ----------------

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DeskBoxes";

        private bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(AppName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败：" + ex.Message, "桌面盒子");
            }
        }

        private static string PromptText(string title, string label, string initial)
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

                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        /// <summary>
        /// 托盘图标直接取 exe 内嵌图标（compiled with /win32icon:app.ico）。
        /// 不走"运行时画图 + 写文件"，避免安全软件拦截写 .ico、以及句柄所有权问题。
        /// </summary>
        private static Icon MakeTrayIcon()
        {
            try
            {
                Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch (Exception ex) { PanelForm.LogError("MakeTrayIcon", ex); }
            return SystemIcons.Application;
        }

        /// <summary>测试用：N 秒后走正常退出流程（含还原桌面）。</summary>
        public void ScheduleQuit(int seconds)
        {
            _testQuitTimer = new System.Windows.Forms.Timer();
            _testQuitTimer.Interval = Math.Max(1, seconds) * 1000;
            _testQuitTimer.Tick += delegate
            {
                _testQuitTimer.Stop();
                _testQuitTimer.Dispose();
                _testQuitTimer = null;
                Quit();
            };
            _testQuitTimer.Start();
        }

        private void Quit()
        {
            // 盒子不在运行时桌面必须保持原样：退出前把所有收纳的快捷方式还原回桌面。
            try { int skipped; AutoOrganizer.Restore(out skipped); } catch { }
            ConfigStore.Save(_st);
            if (_panel != null) { try { _panel.Hide(); _panel.Dispose(); } catch { } }
            if (_balance != null) { try { _balance.Hide(); _balance.Dispose(); } catch { } }
            if (_hermes != null) { try { _hermes.Hide(); _hermes.Dispose(); } catch { } }
            if (_shortcuts != null) { try { _shortcuts.Hide(); _shortcuts.Dispose(); } catch { } }
            if (_tray != null) { _tray.Dispose(); }
            ExitThread();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// 自检：逐个创建每个盒子并断言窗口句柄真的建出来了。
        /// 这是回归护栏 —— 任何一次重写/移植漏掉一个盒子，这里都会 FAIL 并返回非 0 退出码。
        /// </summary>
        static int SelfTest()
        {
            AppState st = ConfigStore.Load();
            Form[] forms =
            {
                new PanelForm(st),
                new BalanceForm(st),
                new ShortcutForm(st),
                new HermesBox(st)
            };
            string[] names = { "面板 桌面盒子", "余额盒子", "快捷键盒子", "Hermes 盒子" };

            int fail = 0;
            for (int i = 0; i < forms.Length; i++)
            {
                Form f = forms[i];
                bool ok;
                try
                {
                    IntPtr h = f.Handle;                    // 强制创建窗口句柄
                    ok = h != IntPtr.Zero && f.Width > 0 && f.Height > 0;
                }
                catch (Exception ex)
                {
                    ok = false;
                    PanelForm.LogError("SelfTest:" + names[i], ex);
                }
                Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + names[i] + "  " + f.Width + "x" + f.Height);
                if (!ok) fail++;
                Application.DoEvents();          // 让异步取数回调在窗体还活着时执行完
                System.Threading.Thread.Sleep(120);
                Application.DoEvents();
                try { f.Dispose(); } catch { }
            }

            Console.WriteLine(fail == 0 ? "SELFTEST OK (" + forms.Length + " boxes)" : "SELFTEST FAILED: " + fail);
            Console.Out.Flush();
            Environment.Exit(fail);      // 直接结束，避开 WinForms 关停阶段的异步回调
            return fail;
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            string mutexName = Array.IndexOf(args, "--audit-menus") >= 0 ? "DeskBoxes_AuditInstance" : "DeskBoxes_SingleInstance_v2";
            using (Mutex mutex = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("桌面盒子已经在运行了（看任务栏右下角托盘）。", "桌面盒子",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    PanelForm.LogError("ThreadException", e.Exception);
                    MessageBox.Show("发生错误（已记录到配置目录 error.log）：\r\n\r\n" + e.Exception.Message,
                        "桌面盒子", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    PanelForm.LogError("UnhandledException", e.ExceptionObject as Exception);
                };

                // 测试钩子：--audit-menus 进程内审计所有菜单项（沙箱、无鼠标、不改真实配置）
                if (Array.IndexOf(args, "--audit-menus") >= 0)
                {
                    Environment.ExitCode = MenuAudit.RunStandalone();
                    return;
                }

                TrayContext ctx = new TrayContext();

                // 测试钩子：--selftest 逐个创建所有盒子并断言窗口真的建出来了。
                // 目的：重写/移植时漏掉某个盒子（比如曾经的 Hermes 盒子），这里会直接 FAIL。
                if (Array.IndexOf(args, "--selftest") >= 0)
                {
                    Environment.ExitCode = SelfTest();
                    return;
                }

                // 测试钩子：--tray-dump 打印托盘里四个盒子的状态，并模拟两种变化
                if (Array.IndexOf(args, "--tray-dump") >= 0)
                {
                    ctx.DumpTrayBoxItems("初始");
                    ctx.SetBoxVisible(1, false, true);   // 模拟：用户在托盘里取消「余额盒子」
                    ctx.DumpTrayBoxItems("用户取消余额盒子后");
                    ctx.HermesHideForTest();             // 模拟：Hermes 无数据自动隐藏（不改意图）
                    ctx.DumpTrayBoxItems("Hermes 自动隐藏后");
                    Environment.ExitCode = 0;
                    return;
                }

                // 测试钩子：--quit-after=N 表示 N 秒后走正常退出流程（会还原桌面图标）
                int quitAfter = 0;
                for (int i = 0; i < args.Length; i++)
                    if (args[i].StartsWith("--quit-after=")) int.TryParse(args[i].Substring(13), out quitAfter);
                if (quitAfter > 0) ctx.ScheduleQuit(quitAfter);

                Application.Run(ctx);
                GC.KeepAlive(mutex);
            }
        }
    }
}
