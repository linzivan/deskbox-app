using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DeskBoxes
{
    /// <summary>一个应用格：只记引用，不搬动真实文件。</summary>
    [DataContract]
    public class BoxItem
    {
        [DataMember(Name = "path")] public string Path = "";
        [DataMember(Name = "label")] public string Label = "";
        [DataMember(Name = "iconPath")] public string IconPath = "";

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Label)) return Label;

                string p = Path == null ? "" : Path;
                string lowp = p.ToLowerInvariant();
                if (lowp.StartsWith("shell:mycomputer")) return "此电脑";
                if (lowp.StartsWith("shell:recyclebin")) return "回收站";
                if (lowp.StartsWith("shell:")) return p.Substring(6);
                if (p.EndsWith("\\")) p = p.Substring(0, p.Length - 1);
                int i = p.LastIndexOf('\\');
                string name = i >= 0 ? p.Substring(i + 1) : p;
                string low = name.ToLowerInvariant();
                if (low.EndsWith(".lnk") || low.EndsWith(".url") || low.EndsWith(".exe"))
                    name = name.Substring(0, name.Length - 4);
                return name;
            }
        }
    }

    /// <summary>一个分类分区：面板里的一块格子，名称显示在格子正下方。</summary>
    [DataContract]
    public class SectionDef
    {
        [DataMember(Name = "id")] public string Id = "";
        [DataMember(Name = "name")] public string Name = "新分区";
        [DataMember(Name = "accent")] public string Accent = "#8FB4D9";   // 边框/名称色调
        [DataMember(Name = "items")] public List<BoxItem> Items = new List<BoxItem>();

        public static SectionDef Create(string name)
        {
            SectionDef s = new SectionDef();
            s.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            s.Name = name;
            return s;
        }
    }

    /// <summary>整体状态：一个面板 + 里面若干分区。</summary>
    [DataContract]
    public class AppState
    {
        [DataMember(Name = "panelX")] public int PanelX = 120;
        [DataMember(Name = "panelY")] public int PanelY = 120;
        [DataMember(Name = "panelW")] public int PanelW = 0;   // 0 = 按分区自动算宽度
        [DataMember(Name = "panelLocked")] public bool PanelLocked = false;   // 固定后不能拖动/缩放
        // 大盒子（整个面板）里排列小盒子（分区）的网格：几列几行，如 3×3 / 4×4 / 4×5。
        // 小盒子内部固定 3×3 = 最多 9 个图标，超出翻页。
        [DataMember(Name = "sectionCols")] public int SectionCols = 3;
        [DataMember(Name = "sectionRows")] public int SectionRows = 3;

        [DataMember(Name = "iconSize")] public int IconSize = 36;
        [DataMember(Name = "fontSize")] public int FontSize = 9;
        [DataMember(Name = "showLabels")] public bool ShowLabels = true;
        [DataMember(Name = "hideAll")] public bool HideAll = false;
        [DataMember(Name = "sections")] public List<SectionDef> Sections = new List<SectionDef>();

        /// <summary>解散分区后掉出来的散落应用：直接显示在面板上，不属于任何分区。</summary>
        [DataMember(Name = "looseItems")] public List<BoxItem> LooseItems = new List<BoxItem>();

        [DataMember(Name = "panelTitle")] public string PanelTitle = "桌面应用";
        [DataMember(Name = "panelColor")] public string PanelColor = "#16202C";
        [DataMember(Name = "panelAlpha")] public int PanelAlpha = 210;

        // ---- 余额盒子 ----
        [DataMember(Name = "balVisible")] public bool BalVisible = true;
        [DataMember(Name = "balX")] public int BalX = 1500;
        [DataMember(Name = "balY")] public int BalY = 620;
        [DataMember(Name = "balW")] public int BalW = 248;
        [DataMember(Name = "balH")] public int BalH = 148;
        [DataMember(Name = "balFont")] public int BalFont = 24;
        [DataMember(Name = "balAlpha")] public int BalAlpha = 200;
        [DataMember(Name = "balColor")] public string BalColor = "#1E1E2E";
        [DataMember(Name = "balRefreshMin")] public int BalRefreshMin = 5;

        // ---- 快捷键盒子 ----
        [DataMember(Name = "keyVisible")] public bool KeyVisible = true;
        [DataMember(Name = "keyX")] public int KeyX = 40;
        [DataMember(Name = "keyY")] public int KeyY = 160;
        [DataMember(Name = "keyW")] public int KeyW = 320;
        [DataMember(Name = "keyH")] public int KeyH = 460;
        [DataMember(Name = "keyFont")] public int KeyFont = 11;
        [DataMember(Name = "keyAlpha")] public int KeyAlpha = 200;
        [DataMember(Name = "keyColor")] public string KeyColor = "#1E1E2E";

        /// <summary>每个应用被点开的次数，用来把常用的排到前面。</summary>
        [DataMember(Name = "clicks")] public List<ClickRec> Clicks = new List<ClickRec>();

        /// <summary>指数衰减半衰期（天）。30 天前的使用大约还剩 50%。</summary>
        public const double HalfLifeDays = 30.0;

        public static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static double DaysSince(long unix)
        {
            if (unix <= 0) return 0.0;
            long now = NowUnix();
            if (now <= unix) return 0.0;
            return (now - unix) / 86400.0;
        }

        private ClickRec Find(string path)
        {
            if (Clicks == null || string.IsNullOrEmpty(path)) return null;
            for (int i = 0; i < Clicks.Count; i++)
                if (string.Equals(Clicks[i].Path, path, StringComparison.OrdinalIgnoreCase)) return Clicks[i];
            return null;
        }

        /// <summary>旧接口：累计次数（保留兼容，不再用于排序）。</summary>
        public int ClickOf(string path)
        {
            ClickRec r = Find(path);
            return r == null ? 0 : r.N;
        }

        /// <summary>衰减后的原始分（未分桶）：衰减分 × 0.5^(距上次使用天数/半衰期)。</summary>
        public double RawScoreOf(string path)
        {
            ClickRec r = Find(path);
            if (r == null) return 0.0;
            return r.Score * Math.Pow(0.5, DaysSince(r.LastUsed) / HalfLifeDays);
        }

        /// <summary>
        /// 排序档位：按 log(1+分)/log(1.15) 分档（约 15% 一档）。
        /// 同档内保持原顺序，只有明显更高（约 15% 以上）才升档 —— 滞回，避免来回换位。
        /// </summary>
        public int RankOf(string path)
        {
            double s = RawScoreOf(path);
            if (s <= 0.0) return 0;
            return (int)Math.Floor(Math.Log(s + 1.0) / Math.Log(1.15));
        }

        /// <summary>记一次点击：先把旧分衰减到当前，再 +1；同时更新 last 与旧计数。</summary>
        public void BumpClick(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Clicks == null) Clicks = new List<ClickRec>();
            ClickRec r = Find(path);
            if (r == null)
            {
                r = new ClickRec();
                r.Path = path;
                Clicks.Add(r);
            }
            r.Score = r.Score * Math.Pow(0.5, DaysSince(r.LastUsed) / HalfLifeDays) + 1.0;
            r.LastUsed = NowUnix();
            r.N = r.N + 1;
        }

        /// <summary>老数据迁移：旧版只有 n，没有 score/last；用 n 当初始分、last 记为今天。返回是否改动过。</summary>
        public bool MigrateClicks()
        {
            if (Clicks == null) return false;
            bool changed = false;
            long now = NowUnix();
            for (int i = 0; i < Clicks.Count; i++)
            {
                ClickRec r = Clicks[i];
                if (r.Score <= 0.0 && r.N > 0) { r.Score = r.N; r.LastUsed = now; changed = true; }
                else if (r.LastUsed <= 0) { r.LastUsed = now; changed = true; }
            }
            return changed;
        }
    }

    /// <summary>一条点击计数记录（不用 Dictionary：DataContractJsonSerializer 会把它写成数组）。</summary>
    [DataContract]
    public class ClickRec
    {
        [DataMember(Name = "path")] public string Path = "";
        [DataMember(Name = "n")] public int N = 0;              // 旧字段：累计次数（保留，仅统计/迁移）
        [DataMember(Name = "score")] public double Score = 0;   // 衰减分：每次点击先衰减到当前再 +1
        [DataMember(Name = "last")] public long LastUsed = 0;   // 上次点击时间（Unix 秒）
    }

    public static class ConfigStore
    {
        /// <summary>上一次 Load 是否做过老数据迁移（Program 据此立即落盘，避免每次启动重置 last）。</summary>
        public static bool LastLoadMigrated = false;

        public static string Dir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("DESKBOXES_DIR");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskBoxes");
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "boxes.json"); } }

        public static AppState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return Defaults();
                using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppState));
                    AppState st = ser.ReadObject(fs) as AppState;
                    if (st == null) return Defaults();
                    if (st.Sections == null) st.Sections = new List<SectionDef>();
                    if (st.SectionCols < 2 || st.SectionCols > 6) st.SectionCols = 3;
                    if (st.SectionRows < 2 || st.SectionRows > 6) st.SectionRows = 3;
                    if (st.IconSize < 20) st.IconSize = 36;
                    if (st.FontSize < 7) st.FontSize = 9;
                    if (st.PanelW < 0) st.PanelW = 0;
                    if (string.IsNullOrEmpty(st.PanelTitle)) st.PanelTitle = "桌面应用";
                    if (string.IsNullOrEmpty(st.PanelColor)) st.PanelColor = "#16202C";
                    if (st.PanelAlpha < 40) st.PanelAlpha = 210;
                    if (st.Clicks == null) st.Clicks = new List<ClickRec>();
                    LastLoadMigrated = st.MigrateClicks();
                    if (st.BalW < 120) st.BalW = 248;
                    if (st.BalH < 80) st.BalH = 148;
                    if (st.KeyW < 150) st.KeyW = 320;
                    if (st.KeyH < 150) st.KeyH = 460;
                    // DataContractJsonSerializer 反序列化不执行字段初始化器，
                    // 旧配置里没有这些字段时读出来是 false/0，必须显式补默认值
                    if (string.IsNullOrEmpty(st.BalColor))
                    {
                        st.BalColor = "#1E1E2E";
                        st.BalVisible = true;
                        st.BalX = 1500; st.BalY = 620;
                        st.BalFont = 24; st.BalAlpha = 200;
                    }
                    if (string.IsNullOrEmpty(st.KeyColor))
                    {
                        st.KeyColor = "#1E1E2E";
                        st.KeyVisible = true;
                        st.KeyX = 40; st.KeyY = 160;
                        st.KeyFont = 11; st.KeyAlpha = 200;
                    }
                    if (st.BalFont < 14) st.BalFont = 24;
                    if (st.BalAlpha < 60) st.BalAlpha = 200;
                    if (st.KeyFont < 7) st.KeyFont = 11;
                    if (st.KeyAlpha < 60) st.KeyAlpha = 200;
                    if (st.BalRefreshMin < 1) st.BalRefreshMin = 5;
                    return st;
                }
            }
            catch { return Defaults(); }
        }

        public static AppState Defaults()
        {
            AppState st = new AppState();
            st.Sections.Add(SectionDef.Create("开发工具"));
            st.Sections.Add(SectionDef.Create("AI 工具"));
            st.Sections.Add(SectionDef.Create("办公协作"));
            st.BalVisible = true;
            st.KeyVisible = true;
            return st;
        }

        public static void Save(AppState st)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                using (FileStream fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppState));
                    ser.WriteObject(fs, st);
                }
            }
            catch { }
        }
    }
}
