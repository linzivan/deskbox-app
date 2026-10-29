using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DeskBoxes
{
    /// <summary>一个会话的显示状态。</summary>
    public enum HState
    {
        Running,      // 正在执行（agent 在处理 / 工具在跑）
        WaitingYou,   // agent 说完了，等你回复
        NeedCheck,    // 工具调用长时间没返回：很可能在等你批准
        Done,         // 会话已结束
        Orphan,       // 上次 Hermes 重启时被清理的孤儿会话
        Unknown
    }

    /// <summary>盒子要显示的一条会话。</summary>
    public class HSession
    {
        public string Id;
        public string Title;
        public double LastActive;
        public int MessageCount;
        public double CostUsd;
        public HState State;
        public string StateText;
        public string Dot;          // 状态点的颜色（#RRGGBB）
    }

    /// <summary>今日统计。</summary>
    public class HSummary
    {
        public int Sessions;
        public int Messages;
        public double CostUsd;
        public int Running;
    }

    /// <summary>
    /// 从 Hermes 本地网关 API 读会话状态。
    ///
    /// 数据源：http://127.0.0.1:8642（gateway_state.json 里的 listener_base）
    ///   GET /health                       免认证，判断 Hermes 是否在跑
    ///   GET /api/sessions                 会话列表（含 title / last_active / ended_at / cost）
    ///   GET /api/sessions/{id}/messages?limit=3&amp;order=latest   最后几条消息，用于精确判定状态
    ///
    /// 为什么用 API 而不是直接读 state.db：那个库有 300MB+、带 WAL，边跑边读容易踩锁，
    /// 而且 schema 会随版本变。API 只读、schema 稳定，还顺带给了花费。
    ///
    /// 密钥：~/.hermes/.env 里的 API_SERVER_KEY，只在内存中使用，不落日志。
    /// </summary>
    public static class HermesData
    {
        private const string BaseUrl = "http://127.0.0.1:8642";

        private static string _cachedKey;
        private static bool _keyLoaded;

        // ---------------- DTO ----------------

        [DataContract]
        private class SessionListDto
        {
            [DataMember(Name = "data")]
            public List<SessionDto> Data;
        }

        [DataContract]
        private class SessionDto
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "title")] public string Title;
            [DataMember(Name = "last_active")] public double LastActive;
            [DataMember(Name = "started_at")] public double StartedAt;
            [DataMember(Name = "ended_at")] public double? EndedAt;
            [DataMember(Name = "end_reason")] public string EndReason;
            [DataMember(Name = "message_count")] public int MessageCount;
            [DataMember(Name = "estimated_cost_usd")] public double? CostUsd;
            [DataMember(Name = "hidden")] public bool Hidden;
            [DataMember(Name = "archived")] public bool Archived;
        }

        [DataContract]
        private class MessageListDto
        {
            [DataMember(Name = "data")]
            public List<MessageDto> Data;
        }

        [DataContract]
        private class MessageDto
        {
            [DataMember(Name = "role")] public string Role;
            [DataMember(Name = "content")] public string Content;
            [DataMember(Name = "tool_name")] public string ToolName;

            // 只需要知道"有没有"—— assistant 带 tool_calls 表示它发起工具调用后停住了
            [DataMember(Name = "tool_calls")]
            public List<ToolCallDto> ToolCalls;
        }

        [DataContract]
        private class ToolCallDto
        {
            [DataMember(Name = "id")] public string Id;
        }

        // ---------------- 密钥 ----------------

        private static string HermesHome()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "hermes");
        }

        /// <summary>读 API_SERVER_KEY（只读一次，缓存在内存）。失败返回空串。</summary>
        public static string ReadApiKey()
        {
            if (_keyLoaded) return _cachedKey;
            _keyLoaded = true;
            _cachedKey = "";

            try
            {
                string envPath = Path.Combine(HermesHome(), ".env");
                if (!File.Exists(envPath)) return _cachedKey;

                string[] lines = File.ReadAllLines(envPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i].Trim();
                    if (l.StartsWith("API_SERVER_KEY=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = l.Substring("API_SERVER_KEY=".Length).Trim().Trim('"', '\'');
                        if (v.Length > 0) { _cachedKey = v; break; }
                    }
                }
            }
            catch (Exception ex) { PanelForm.LogError("HermesReadKey", ex); }

            return _cachedKey;
        }

        // ---------------- HTTP ----------------

        private static string HttpGet(string path, bool needAuth, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;       // 本机回环，别走系统代理（否则连不上）

            if (needAuth)
            {
                string key = ReadApiKey();
                if (key.Length == 0) throw new Exception("找不到 API_SERVER_KEY");
                req.Headers["Authorization"] = "Bearer " + key;
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static T ParseJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return ser.ReadObject(ms) as T;
        }

        /// <summary>Hermes 网关是否在线（/health 免认证）。</summary>
        public static bool IsApiAvailable()
        {
            try
            {
                string body = HttpGet("/health", false, 2000);
                return body != null && body.IndexOf("\"ok\"", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // ---------------- 取数 + 状态判定 ----------------

        private static double NowSec()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// 本地今天 0 点对应的 unix 秒。
        /// 注意不能写成 (DateTime.Today - 1970).TotalSeconds 再减时区偏移 ——
        /// 两个 DateTime 都是 Unspecified，相减得到的已经是「本地时间轴的秒数」，
        /// 再减偏移会多减一个时区（本机 UTC+8 就直接少 8 小时，今日统计恒为 0）。
        /// </summary>
        private static long TodayStartTs()
        {
            DateTime localMidnight = DateTime.Today;                      // 本地 00:00
            DateTime utcMidnight = localMidnight.ToUniversalTime();        // 转成 UTC
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(utcMidnight - epoch).TotalSeconds;
        }

        /// <summary>
        /// 拉最近 count 个会话并判定状态。Hermes 没在跑时返回 null（调用方据此隐藏盒子）。
        /// </summary>
        public static List<HSession> FetchRecent(int count, out HSummary summary, out string error)
        {
            summary = new HSummary();
            error = null;

            string json;
            try { json = HttpGet("/api/sessions", true, 6000); }
            catch (Exception ex)
            {
                error = "Hermes 未运行";
                PanelForm.LogError("HermesSessions", ex);
                return null;
            }

            SessionListDto lst;
            try { lst = ParseJson<SessionListDto>(json); }
            catch (Exception ex) { error = "解析失败"; PanelForm.LogError("HermesParse", ex); return null; }

            if (lst == null || lst.Data == null) { error = "无数据"; return null; }

            // 今日统计：用 last_active 判定「今天有活动」。
            // 用 started_at 是错的 —— 长会话（比如从 10/20 开到今天）会被整个漏掉。
            long todayStart = TodayStartTs();
            for (int i = 0; i < lst.Data.Count; i++)
            {
                SessionDto s = lst.Data[i];
                if (s.Hidden) continue;
                if (s.LastActive >= todayStart)
                {
                    summary.Sessions++;
                    summary.Messages += s.MessageCount;
                    if (s.CostUsd.HasValue) summary.CostUsd += s.CostUsd.Value;
                }
            }

            // 只取未隐藏、未归档的，按最后活动倒序
            List<SessionDto> visible = new List<SessionDto>();
            for (int i = 0; i < lst.Data.Count; i++)
            {
                SessionDto s = lst.Data[i];
                if (s.Hidden || s.Archived) continue;
                visible.Add(s);
            }
            visible.Sort(delegate(SessionDto a, SessionDto b) { return b.LastActive.CompareTo(a.LastActive); });

            List<HSession> result = new List<HSession>();
            int take = Math.Min(count, visible.Count);
            for (int i = 0; i < take; i++)
            {
                SessionDto s = visible[i];
                MessageDto last = FetchLastMessage(s.Id);
                HSession h = new HSession();
                h.Id = s.Id;
                h.Title = string.IsNullOrEmpty(s.Title) ? "(未命名会话)" : s.Title;
                h.LastActive = s.LastActive;
                h.MessageCount = s.MessageCount;
                h.CostUsd = s.CostUsd.HasValue ? s.CostUsd.Value : 0;
                h.State = Judge(s, last);
                ApplyStateStyle(h);
                result.Add(h);
            }

            for (int i = 0; i < result.Count; i++)
                if (result[i].State == HState.Running) summary.Running++;

            return result;
        }

        /// <summary>取某会话的最后一条消息；失败返回 null（不影响整体）。</summary>
        private static MessageDto FetchLastMessage(string sessionId)
        {
            try
            {
                string json = HttpGet("/api/sessions/" + sessionId + "/messages?limit=1&order=latest", true, 5000);
                MessageListDto m = ParseJson<MessageListDto>(json);
                if (m == null || m.Data == null || m.Data.Count == 0) return null;
                return m.Data[m.Data.Count - 1];
            }
            catch { return null; }
        }

        /// <summary>
        /// 用「最后一条消息的形态」判定状态 —— 这是精准判定的关键：
        /// assistant + tool_calls  → agent 发起了工具调用后停住（工具在跑，或在等批准）
        /// assistant 无 tool_calls → 这一轮说完了，在等你
        /// tool                    → 工具刚返回，agent 还要继续
        /// user                    → 你发了消息，agent 在处理
        /// </summary>
        private static HState Judge(SessionDto s, MessageDto last)
        {
            if (s.EndedAt.HasValue || !string.IsNullOrEmpty(s.EndReason))
            {
                if (string.Equals(s.EndReason, "startup_orphan_reap", StringComparison.OrdinalIgnoreCase))
                    return HState.Orphan;
                return HState.Done;
            }

            if (last == null) return HState.Unknown;

            double idle = NowSec() - s.LastActive;

            if (string.Equals(last.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                bool hasTools = last.ToolCalls != null && last.ToolCalls.Count > 0;
                if (hasTools)
                {
                    // 工具调用发出后长时间没有 tool 消息回来：正常长任务，也可能是等你批准
                    if (idle > 300) return HState.NeedCheck;
                    return HState.Running;
                }
                return HState.WaitingYou;
            }

            if (string.Equals(last.Role, "tool", StringComparison.OrdinalIgnoreCase)) return HState.Running;
            if (string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase)) return HState.Running;

            return HState.Unknown;
        }

        private static void ApplyStateStyle(HSession h)
        {
            switch (h.State)
            {
                case HState.Running:    h.StateText = "执行中";     h.Dot = "#4CAF50"; break;
                case HState.WaitingYou: h.StateText = "等你回复";   h.Dot = "#FFC107"; break;
                case HState.NeedCheck:  h.StateText = "需查看";     h.Dot = "#FF9800"; break;
                case HState.Done:       h.StateText = "已完成";     h.Dot = "#9E9E9E"; break;
                case HState.Orphan:     h.StateText = "异常中断";   h.Dot = "#F44336"; break;
                default:                h.StateText = "未知";       h.Dot = "#607D8B"; break;
            }
        }

        /// <summary>把 unix 秒转成「x 分钟前」。</summary>
        public static string AgoText(double ts)
        {
            if (ts <= 0) return "";
            double diff = NowSec() - ts;
            if (diff < 60) return "刚刚";
            if (diff < 3600) return (int)(diff / 60) + " 分钟前";
            if (diff < 86400) return (int)(diff / 3600) + " 小时前";
            return (int)(diff / 86400) + " 天前";
        }
    }
}
