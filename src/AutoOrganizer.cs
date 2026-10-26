using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DeskBoxes
{
    /// <summary>一条移动记录：桌面原路径 -> 盒子仓库路径，用于还原。</summary>
    [DataContract]
    public class MoveRecord
    {
        [DataMember(Name = "from")] public string From = "";
        [DataMember(Name = "to")] public string To = "";
        [DataMember(Name = "section")] public string Section = "";
        [DataMember(Name = "when")] public string When = "";
    }

    [DataContract]
    public class MoveLog
    {
        [DataMember(Name = "records")] public List<MoveRecord> Records = new List<MoveRecord>();
    }

    /// <summary>
    /// 自动整理：扫描桌面快捷方式，按关键词归类，移动到
    /// %APPDATA%\DeskBoxes\Vault\<分区>\ 下，并写移动日志供还原。
    /// </summary>
    public static class AutoOrganizer
    {
        public const string OtherSection = "其他";

        /// <summary>分类规则：按顺序匹配，先命中的算数。</summary>
        private static readonly string[][] Rules =
        {
            new[] { "开发工具", "#8FB4D9",
                "docker","github desktop","mobaxterm","winscp","postman","sublime","typora",
                "mqtt","codex","navicat","pycharm","intellij","idea","vscode","visual studio",
                "heidisql","sourcetree","git ","gitkraken","postgre","dbeaver","xshell","putty",
                "apifox","apipost","jmeter","wireshark","fiddler","microsoft sql","eap_app",
                "draw.io","qoder","qoder" },

            new[] { "AI 工具", "#A9A9D9",
                "trae","codebuddy","deepseek","hermes","千问","chatgpt","claude","cursor",
                "kimi","豆包","copilot","cc switch","tongyi","文心","通义","元宝","yuanbao" },

            // 浏览器要排在办公之前，否则「QQ浏览器」会被办公的 qq 抢走
            new[] { "浏览器", "#7FB6BC",
                "edge","chrome","firefox","brave","360浏览器","qq浏览器","opera","clash","verge" },

            new[] { "聊天通讯", "#8FC0B8",
                "微信","wechat","qq","tim","飞书","lark","钉钉","dingtalk","企业微信","wework",
                "网易邮箱","foxmail","outlook","telegram","whatsapp","discord","slack",
                "腾讯会议","zoom","wemeet","腾讯qq" },

            new[] { "办公协作", "#9DC3A4",
                "wps","office","word","excel","powerpoint","ppt",
                "xmind","axure","excalidraw","docshelf","百度网盘","学习文档库",
                "notion","obsidian","foxit","acrobat","calibre","codesign" },

            new[] { "截图录屏", "#C8A882",
                "snipaste","sharex","fscapture","faststone","bandicam","obs studio","pixpin" },

            new[] { "远程连接", "#9AA8C8",
                "todesk","向日葵","atrust","uu远程","anydesk","teamviewer","vnc","remotedesk",
                "mstsc","远程桌面" },

            new[] { "系统工具", "#A8B0BA",
                "此电脑","我的电脑","计算机","回收站","这台电脑","this pc","recycle",
                "everything","total uninstall","picgo","7-zip","winrar","ccleaner","geek",
                "geek uninstaller","分区助手","diskgenius","cpu-z","rufus" },

            new[] { "虚拟机", "#B0A8C8",
                "vmware","virtualbox","hyper-v","parallels" },

            new[] { "娱乐", "#C89A9A",
                "bilibili","哔哩","汽水","网易云","spotify","steam","qq音乐","douyin","抖音" },
        };

        /// <summary>分区的固定显示顺序（按规则表顺序，「其他」永远最后）。</summary>
        public static List<string> SectionOrder()
        {
            List<string> order = new List<string>();
            for (int r = 0; r < Rules.Length; r++) order.Add(Rules[r][0]);
            order.Add(OtherSection);
            return order;
        }

        public static int OrderIndex(string section)
        {
            List<string> order = SectionOrder();
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], section, StringComparison.OrdinalIgnoreCase)) return i;
            return order.Count;
        }

        public static string VaultDir { get { return Path.Combine(ConfigStore.Dir, "Vault"); } }
        public static string LogPath { get { return Path.Combine(ConfigStore.Dir, "organize-log.json"); } }

        /// <summary>桌面可能有多个：用户桌面 + 公共桌面（Windows 把两者合并显示）。</summary>
        public static List<string> DesktopDirs
        {
            get
            {
                List<string> dirs = new List<string>();

                string over = Environment.GetEnvironmentVariable("DESKBOXES_DESKTOP");
                if (!string.IsNullOrEmpty(over)) { dirs.Add(over); return dirs; }

                string mine = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(mine)) dirs.Add(mine);

                string pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrEmpty(pub) && Directory.Exists(pub) &&
                    !string.Equals(pub, mine, StringComparison.OrdinalIgnoreCase))
                    dirs.Add(pub);

                return dirs;
            }
        }

        public static string DesktopDir
        {
            get
            {
                List<string> d = DesktopDirs;
                return d.Count > 0 ? d[0] : "";
            }
        }

        /// <summary>匹配到的分区名与配色。</summary>
        public static void MatchSection(string fileName, out string section, out string accent)
        {
            string low = (fileName ?? "").ToLowerInvariant();

            for (int r = 0; r < Rules.Length; r++)
            {
                string[] rule = Rules[r];
                string name = rule[0];
                for (int k = 2; k < rule.Length; k++)
                {
                    if (low.IndexOf(rule[k], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        section = name;
                        accent = rule[1];
                        return;
                    }
                }
            }

            section = OtherSection;
            accent = "#A8ADB8";
        }

        public class PlanItem
        {
            public string Path;
            public string Section;
            public string Accent;
        }

        /// <summary>扫描桌面，生成待整理清单（不移动任何东西）。</summary>
        public static List<PlanItem> Scan(bool includeShortcutsOnly)
        {
            List<PlanItem> plan = new List<PlanItem>();
            try
            {
                List<string> dirs = DesktopDirs;

                foreach (string desktopDir in dirs)
                {
                if (!Directory.Exists(desktopDir)) continue;

                foreach (string f in Directory.GetFiles(desktopDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    bool isLnk = (ext == ".lnk" || ext == ".url");
                    if (includeShortcutsOnly && !isLnk) continue;
                    if (!includeShortcutsOnly && !isLnk && ext != ".exe") continue;
                    if (ext == ".ini") continue;                       // desktop.ini 绝对不能动

                    string section, accent;
                    MatchSection(Path.GetFileNameWithoutExtension(f), out section, out accent);

                    PlanItem it = new PlanItem();
                    it.Path = f;
                    it.Section = section;
                    it.Accent = accent;
                    plan.Add(it);
                }
                }

                // 只有一两个图标的分区太单薄，并入「其他」，免得面板上一堆空格子
                Dictionary<string, int> counts = new Dictionary<string, int>();
                for (int i = 0; i < plan.Count; i++)
                {
                    string k = plan[i].Section;
                    counts[k] = counts.ContainsKey(k) ? counts[k] + 1 : 1;
                }
                for (int i = 0; i < plan.Count; i++)
                {
                    string k = plan[i].Section;
                    if (k != OtherSection && counts[k] < 2)
                    {
                        plan[i].Section = OtherSection;
                        plan[i].Accent = "#A8ADB8";
                    }
                }

                // 不再拆分成「xxx 2」：分页由界面处理（每个分区 3×3，超出翻页）
            }
            catch { }
            return plan;
        }

        /// <summary>每个分区最多放几个（3×3 方形布局）。超出的拆成「名字 2」「名字 3」。</summary>
        public const int PerSectionCap = 9;

        public static List<PlanItem> SplitOversized(List<PlanItem> plan, int cap)
        {
            Dictionary<string, int> seen = new Dictionary<string, int>();
            List<PlanItem> outp = new List<PlanItem>();

            for (int i = 0; i < plan.Count; i++)
            {
                PlanItem it = plan[i];
                string baseName = it.Section;
                int n = seen.ContainsKey(baseName) ? seen[baseName] : 0;
                seen[baseName] = n + 1;

                int part = n / cap;          // 第几块
                PlanItem copy = new PlanItem();
                copy.Path = it.Path;
                copy.Accent = it.Accent;
                copy.Section = part == 0 ? baseName : baseName + " " + (part + 1);
                outp.Add(copy);
            }
            return outp;
        }

        /// <summary>按分区汇总，便于显示预览。</summary>
        public static Dictionary<string, List<PlanItem>> Group(List<PlanItem> plan)
        {
            Dictionary<string, List<PlanItem>> g = new Dictionary<string, List<PlanItem>>();
            List<string> order = new List<string>();

            for (int i = 0; i < plan.Count; i++)
            {
                PlanItem it = plan[i];
                if (!g.ContainsKey(it.Section))
                {
                    g[it.Section] = new List<PlanItem>();
                    order.Add(it.Section);
                }
                g[it.Section].Add(it);
            }
            return g;
        }

        /// <summary>执行移动，返回 分区名 -> 移动后的新路径列表。</summary>
        public static Dictionary<string, List<string>> Execute(List<PlanItem> plan, out List<MoveRecord> records)
        {
            Dictionary<string, List<string>> result = new Dictionary<string, List<string>>();
            records = new List<MoveRecord>();

            for (int i = 0; i < plan.Count; i++)
            {
                PlanItem it = plan[i];
                try
                {
                    string safeSection = SafeName(it.Section);
                    string dir = Path.Combine(VaultDir, safeSection);
                    Directory.CreateDirectory(dir);

                    string fileName = Path.GetFileName(it.Path);
                    string dst = Path.Combine(dir, fileName);

                    // 重名时加序号
                    if (File.Exists(dst))
                    {
                        string stem = Path.GetFileNameWithoutExtension(fileName);
                        string ext = Path.GetExtension(fileName);
                        int n = 2;
                        do
                        {
                            dst = Path.Combine(dir, stem + " (" + n + ")" + ext);
                            n++;
                        } while (File.Exists(dst) && n < 999);
                    }

                    File.Move(it.Path, dst);

                    MoveRecord rec = new MoveRecord();
                    rec.From = it.Path;
                    rec.To = dst;
                    rec.Section = it.Section;
                    rec.When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    records.Add(rec);

                    if (!result.ContainsKey(it.Section)) result[it.Section] = new List<string>();
                    result[it.Section].Add(dst);
                }
                catch
                {
                    // 单个失败不影响其他
                }
            }

            AppendLog(records);
            return result;
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "其他";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(s);
            for (int i = 0; i < bad.Length; i++) sb.Replace(bad[i], '_');
            return sb.ToString().Trim();
        }

        // ---------------- 移动日志 / 还原 ----------------

        public static MoveLog LoadLog()
        {
            try
            {
                if (!File.Exists(LogPath)) return new MoveLog();
                using (FileStream fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(MoveLog));
                    MoveLog l = ser.ReadObject(fs) as MoveLog;
                    if (l == null || l.Records == null) return new MoveLog();
                    return l;
                }
            }
            catch { return new MoveLog(); }
        }

        private static void AppendLog(List<MoveRecord> add)
        {
            if (add == null || add.Count == 0) return;
            try
            {
                MoveLog l = LoadLog();
                l.Records.AddRange(add);
                SaveLog(l);
            }
            catch { }
        }

        private static void SaveLog(MoveLog l)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                using (FileStream fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(MoveLog));
                    ser.WriteObject(fs, l);
                }
            }
            catch { }
        }

        /// <summary>把所有移动过的快捷方式还原回桌面，返回成功数量。</summary>
        public static int Restore(out int skipped)
        {
            skipped = 0;
            int ok = 0;
            MoveLog l = LoadLog();
            List<MoveRecord> keep = new List<MoveRecord>();

            for (int i = 0; i < l.Records.Count; i++)
            {
                MoveRecord r = l.Records[i];
                try
                {
                    if (!File.Exists(r.To)) { skipped++; continue; }        // 已被用户删掉

                    string target = r.From;
                    if (File.Exists(target))
                    {
                        // 桌面上已有同名文件，加序号避免覆盖
                        string stem = Path.GetFileNameWithoutExtension(r.From);
                        string ext = Path.GetExtension(r.From);
                        int n = 2;
                        do
                        {
                            target = Path.Combine(DesktopDir, stem + " (" + n + ")" + ext);
                            n++;
                        } while (File.Exists(target) && n < 999);
                    }

                    File.Move(r.To, target);
                    ok++;
                }
                catch
                {
                    skipped++;
                    keep.Add(r);      // 还原失败的保留记录，下次还能再试
                }
            }

            MoveLog nl = new MoveLog();
            nl.Records = keep;
            SaveLog(nl);

            // 清理空的分区仓库目录
            try
            {
                if (Directory.Exists(VaultDir))
                {
                    string[] subdirs = Directory.GetDirectories(VaultDir);
                    for (int i = 0; i < subdirs.Length; i++)
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(subdirs[i]).Length == 0)
                                Directory.Delete(subdirs[i]);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return ok;
        }

        /// <summary>还有多少个可还原的记录。</summary>
        public static int RestorableCount()
        {
            MoveLog l = LoadLog();
            int n = 0;
            for (int i = 0; i < l.Records.Count; i++)
                if (File.Exists(l.Records[i].To)) n++;
            return n;
        }
    }
}
