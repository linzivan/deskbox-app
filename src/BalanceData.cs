using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>DeepSeek 余额数据：API 余额 + 本地历史（用于算今日/本月消耗和趋势图）。</summary>
    public static class BalanceData
    {
        public const string ApiUrl = "https://api.deepseek.com/user/balance";

        public static string ApiKeyPath { get { return Path.Combine(ConfigStore.Dir, "apikey.txt"); } }
        /// <summary>
        /// 历史采样文件放在 exe 同目录的 data\ 下。
        /// 不能放 %APPDATA%：火绒的勒索防护会拦截本 exe 写 AppData 里的数据文件
        /// （UnauthorizedAccessException），而程序自己目录可写。
        /// </summary>
        public static string HistoryPath
        {
            get
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (string.IsNullOrEmpty(exeDir)) exeDir = ConfigStore.Dir;
                string dir = Path.Combine(exeDir, "data");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "history.csv");
            }
        }

        public static string ReadApiKey()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (!string.IsNullOrEmpty(env)) return env.Trim();

                if (!File.Exists(ApiKeyPath)) return "";
                string[] lines = File.ReadAllLines(ApiKeyPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim().Trim('\uFEFF');
                    if (t.Length > 0) return t;
                }
            }
            catch { }
            return "";
        }

        /// <summary>同步拉余额；返回 -1 表示失败。</summary>
        public static double FetchBalance(out string error)
        {
            error = null;
            string key = ReadApiKey();
            if (string.IsNullOrEmpty(key)) { error = "未配置 apikey.txt"; return -1; }

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                req.Method = "GET";
                req.Timeout = 12000;
                req.ReadWriteTimeout = 12000;
                req.Headers.Add("Authorization", "Bearer " + key);
                req.Accept = "application/json";
                req.Proxy = null;      // 直连，避免系统代理干扰

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    double v = ParseTotalBalance(json);
                    if (v < 0) { error = "响应里没有余额字段"; return -1; }
                    return v;
                }
            }
            catch (WebException wex)
            {
                error = wex.Status == WebExceptionStatus.ProtocolError
                    ? "HTTP " + (int)((HttpWebResponse)wex.Response).StatusCode
                    : wex.Status.ToString();
                return -1;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return -1;
            }
        }

        /// <summary>从 {"balance_infos":[{"currency":"CNY","total_balance":"110.00"}]} 取 CNY 总额。</summary>
        public static double ParseTotalBalance(string json)
        {
            try
            {
                int idx = json.IndexOf("\"balance_infos\"", StringComparison.Ordinal);
                if (idx < 0) return -1;

                // 优先找 CNY 那一段
                int cny = json.IndexOf("\"CNY\"", idx, StringComparison.Ordinal);
                int from = cny > 0 ? cny : idx;

                int tb = json.IndexOf("\"total_balance\"", from, StringComparison.Ordinal);
                if (tb < 0) return -1;

                int colon = json.IndexOf(':', tb);
                int q1 = json.IndexOf('"', colon + 1);
                int q2 = json.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) return -1;

                string s = json.Substring(q1 + 1, q2 - q1 - 1);
                double v;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            }
            catch { }
            return -1;
        }

        // ---------------- 历史记录 ----------------

        public class Point
        {
            public long Ts;
            public double Balance;
        }

        public static List<Point> LoadHistory()
        {
            List<Point> list = new List<Point>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                string[] lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length < 2) continue;

                    long ts;
                    double bal;
                    if (!long.TryParse(parts[0].Trim(), out ts)) continue;
                    if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bal)) continue;

                    Point p = new Point();
                    p.Ts = ts;
                    p.Balance = bal;
                    list.Add(p);
                }
            }
            catch { }
            return list;
        }

        /// <summary>追加一个采样点，并只保留最近 3 天。</summary>
        /// <summary>出问题时写到 exe 目录的 bal_debug.log（不用 AppData，那里会被安全软件拦）。</summary>
        private static void DebugLog(string msg)
        {
            try
            {
                string p = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) ?? ".",
                    "bal_debug.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public static void AppendHistory(double balance)
        {
            if (balance < 0) return;
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                List<Point> pts = LoadHistory();
                if (pts.Count > 0 && now - pts[pts.Count - 1].Ts < 25)
                {
                    return;
                }

                Point np = new Point();
                np.Ts = now;
                np.Balance = balance;
                pts.Add(np);

                long cutoff = now - 3 * 24 * 3600;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (pts[i].Ts < cutoff) continue;
                    sb.Append(pts[i].Ts.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(pts[i].Balance.ToString("0.00", CultureInfo.InvariantCulture)).Append("\r\n");
                }

                // 直接覆盖原文件：安全软件（火绒勒索防护）会拦截在 AppData 下新建 .tmp 文件，
                // 导致 UnauthorizedAccessException。162KB 的小文件直接写即可。
                File.WriteAllText(HistoryPath, sb.ToString(), new UTF8Encoding(false));

            }
            catch (Exception ex)
            {
                DebugLog("EX " + ex.GetType().Name + ": " + ex.Message);
                PanelForm.LogError("AppendHistory", ex);
            }
        }

        /// <summary>某时间点之后余额下降的总量（充值导致的上升不算消耗）。</summary>
        public static double SpentSince(List<Point> pts, long sinceTs)
        {
            double spent = 0;
            double? prev = null;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].Ts < sinceTs) { prev = pts[i].Balance; continue; }
                if (prev.HasValue && pts[i].Balance < prev.Value)
                    spent += prev.Value - pts[i].Balance;
                prev = pts[i].Balance;
            }
            return spent;
        }

        public static long TodayStartTs()
        {
            DateTime local = DateTime.Today;
            return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static long MonthStartTs()
        {
            DateTime local = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static List<Point> Last24h(List<Point> pts)
        {
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            long from = now - 24 * 3600;
            List<Point> r = new List<Point>();
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].Ts >= from) r.Add(pts[i]);
            return r;
        }
    }
}
