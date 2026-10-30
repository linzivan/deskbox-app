using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>
    /// 菜单审计（无干扰测试）：不点鼠标、不切前台、不动用户配置。
    ///
    ///   --audit-menus  进程内跑：
    ///     1. 把配置目录 / 桌面目录指到 %TEMP%\deskbox-audit（沙箱），绝不碰真实数据；
    ///     2. 建出四个盒子窗体（不显示）；
    ///     3. 反射调用每个菜单构建方法，把菜单"截获"下来（不 Show）；
    ///     4. 深度遍历每个菜单项：记录 文本 / 是否禁用 / 勾选状态 →
    ///        PerformClick → 对比 AppState 前后变化 → 记录异常与弹出的对话框；
    ///     5. 输出 report.json + 控制台摘要。
    /// </summary>
    public static class MenuAudit
    {
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetClassNameW(IntPtr h, StringBuilder b, int n);
        [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr h, StringBuilder b, int n);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

        public static bool On;
        private static readonly List<KeyValuePair<string, ContextMenuStrip>> _menus =
            new List<KeyValuePair<string, ContextMenuStrip>>();
        private static Timer _closer;
        private static string _lastDialog;
        private static readonly List<string> _rows = new List<string>();

        private static readonly string[] SkipItems = { "退出", "开机自动启动" };

        public static void Capture(string name, ContextMenuStrip m)
        {
            if (!On || m == null) return;
            _menus.Add(new KeyValuePair<string, ContextMenuStrip>(name, m));
        }

        public static int RunStandalone()
        {
            string root = Path.Combine(Path.GetTempPath(), "deskbox-audit");
            string cfg = Path.Combine(root, "cfg");
            string desk = Path.Combine(root, "desk");
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(cfg);
                Directory.CreateDirectory(desk);
                File.WriteAllText(Path.Combine(desk, "示例一.txt"), "x");
                File.WriteAllText(Path.Combine(desk, "示例二.txt"), "x");
            }
            catch { }
            Environment.SetEnvironmentVariable("DESKBOXES_DIR", cfg);
            Environment.SetEnvironmentVariable("DESKBOXES_DESKTOP", desk);

            AppState st = ConfigStore.Load();
            BindState(st);

            PanelForm panel = new PanelForm(st);
            BalanceForm bal = new BalanceForm(st);
            ShortcutForm key = new ShortcutForm(st);
            HermesBox hermes = new HermesBox(st);

            // 强制创建句柄（与 --selftest 同样做法），但不 Show
            IntPtr dummy = panel.Handle; dummy = bal.Handle; dummy = key.Handle; dummy = hermes.Handle;
            Pump(250);

            On = true;
            Directory.CreateDirectory(root);
            Progress("=== audit start ===");
            StartDialogCloser();

            AuditOne(panel, "ShowSettingsMenu", "面板-设置菜单", new object[] { new System.Drawing.Point(10, 10) });
            AuditOne(panel, "ShowSectionMenu", "分区-菜单", new object[] { 0, new System.Drawing.Point(10, 10) });
            AuditOne(panel, "ShowItemMenu", "图标-菜单", new object[] { 0, 0, new System.Drawing.Point(10, 10) });
            AuditOne(bal, "ShowMenu", "余额盒子-菜单", new object[] { new System.Drawing.Point(10, 10) });
            AuditOne(key, "ShowMenu", "快捷键盒子-菜单", new object[] { new System.Drawing.Point(10, 10) });
            AuditOne(hermes, "ShowMenu", "Hermes盒子-菜单", new object[] { new System.Drawing.Point(10, 10) });

            StopDialogCloser();

            // ---- MenuWatch 子菜单判定自测（不依赖用户屏幕）----
            string mwResult;
            try
            {
                ContextMenuStrip t = new ContextMenuStrip();
                ToolStripMenuItem sub = new ToolStripMenuItem("子菜单");
                sub.DropDownItems.Add("选项一");
                t.Items.Add(sub);
                t.Show(new System.Drawing.Point(200, 200));
                Pump(200);
                sub.DropDown.Show(new System.Drawing.Point(420, 260));
                Pump(200);
                System.Drawing.Point pSub = sub.DropDown.PointToScreen(new System.Drawing.Point(8, 8));
                System.Drawing.Point pMain = t.PointToScreen(new System.Drawing.Point(8, 8));
                bool inSub = MenuWatch.InsideForTest(t, pSub);
                bool inMain = MenuWatch.InsideForTest(t, pMain);
                bool outside = MenuWatch.InsideForTest(t, new System.Drawing.Point(950, 750));
                mwResult = "subVisible=" + sub.DropDown.Visible + " mainVisible=" + t.Visible
                    + " subBounds=" + sub.DropDown.Bounds
                    + " | 子菜单内=" + inSub + " 主菜单内=" + inMain + " 菜单外=" + outside;
                try { sub.DropDown.Close(); t.Close(); } catch { }
            }
            catch (Exception ex) { mwResult = "自测异常: " + ex.GetType().Name + ": " + ex.Message; }
            Console.WriteLine("MENUWATCH " + mwResult);

            string outFile = Path.Combine(root, "report.json");
            try { File.WriteAllText(outFile, "[\r\n" + string.Join(",\r\n", _rows.ToArray()) + "\r\n]", Encoding.UTF8); } catch { }

            Console.WriteLine("AUDIT REPORT -> " + outFile);
            Console.WriteLine("MENUS=" + _menus.Count + " ITEMS=" + _rows.Count);
            Console.Out.Flush();
            Environment.Exit(0);
            return 0;
        }

        private static void AuditOne(object target, string method, string label, object[] args)
        {
            _menus.Clear();
            try
            {
                MethodInfo mi = target.GetType().GetMethod(method,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (mi == null) { _rows.Add(Row(label, "(方法不存在)", false, false, "", "", "")); return; }
                mi.Invoke(target, args);
            }
            catch (Exception ex)
            {
                _rows.Add(Row(label, "(调用失败)", false, false, "", "",
                    ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return;
            }
            Pump(150);

            foreach (KeyValuePair<string, ContextMenuStrip> kv in _menus.ToArray())
                Walk(kv.Key, kv.Value.Items, "");
            _menus.Clear();
        }

        private static void Walk(string menu, ToolStripItemCollection items, string path)
        {
            foreach (ToolStripItem it in items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                string name = path.Length == 0 ? mi.Text : path + " > " + mi.Text;

                if (mi.DropDownItems.Count > 0) { Walk(menu, mi.DropDownItems, name); continue; }

                bool skip = false;
                foreach (string s in SkipItems) if (mi.Text.IndexOf(s, StringComparison.Ordinal) >= 0) skip = true;
                if (skip) { _rows.Add(Row(menu, name, mi.Enabled, mi.Checked, "", "", "skipped")); continue; }

                Progress(menu + " | " + name);
                Dictionary<string, string> before = Snapshot();
                string err = "", dialog = "";
                try
                {
                    if (mi.Enabled) mi.PerformClick();
                }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                Pump(320);
                Dictionary<string, string> after = Snapshot();
                if (_lastDialog != null) { dialog = _lastDialog; _lastDialog = null; }

                List<string> diff = new List<string>();
                foreach (KeyValuePair<string, string> kv in after)
                {
                    string old;
                    if (!before.TryGetValue(kv.Key, out old) || old != kv.Value)
                        diff.Add(kv.Key + ":" + (old ?? "?") + "→" + kv.Value);
                }

                _rows.Add(Row(menu, name, mi.Enabled, mi.Checked,
                    string.Join("; ", diff.ToArray()), dialog, err));
            }
        }

        private static string _logPath;
        private static void Progress(string what)
        {
            try
            {
                if (_logPath == null) _logPath = Path.Combine(Path.GetTempPath(), "deskbox-audit", "progress.log");
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what + Environment.NewLine);
            }
            catch { }
        }

        private static string Row(string menu, string item, bool enabled, bool chk,
            string changed, string dialog, string note)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"menu\":\"").Append(Esc(menu))
              .Append("\",\"item\":\"").Append(Esc(item))
              .Append("\",\"enabled\":").Append(enabled ? "true" : "false")
              .Append(",\"checked\":").Append(chk ? "true" : "false")
              .Append(",\"changed\":\"").Append(Esc(changed))
              .Append("\",\"dialog\":\"").Append(Esc(dialog))
              .Append("\",\"note\":\"").Append(Esc(note)).Append("\"}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
        }

        private static Dictionary<string, string> Snapshot()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            AppState st = _st;
            if (st == null) return d;
            foreach (FieldInfo f in typeof(AppState).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!(f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType == typeof(decimal))) continue;
                object v = f.GetValue(st);
                d[f.Name] = v == null ? "" : v.ToString();
            }
            return d;
        }

        private static AppState _st;
        public static void BindState(AppState st) { _st = st; }

        private static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        private static void StartDialogCloser()
        {
            _closer = new Timer();
            _closer.Interval = 120;
            _closer.Tick += delegate
            {
                uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if (pid != me || !IsWindowVisible(h)) return true;
                    StringBuilder sb = new StringBuilder(64);
                    GetClassNameW(h, sb, 64);
                    if (sb.ToString() != "#32770") return true;
                    StringBuilder t = new StringBuilder(128);
                    GetWindowTextW(h, t, 128);
                    _lastDialog = t.ToString();
                    PostMessageW(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            };
            _closer.Start();
        }

        private static void StopDialogCloser()
        {
            if (_closer != null) { _closer.Stop(); _closer.Dispose(); _closer = null; }
        }
    }
}
