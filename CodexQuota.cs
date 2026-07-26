using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuota
{
    internal sealed class QuotaSnapshot
    {
        public double UsedPercent;
        public long WindowMinutes;
        public long ResetAt;
        public string Plan;
        public int? ResetCredits;
        public readonly List<long> ResetCreditExpiresAt = new List<long>();
        public DateTime UpdatedAt;
    }

    internal sealed class TokenUsage
    {
        public long Input;
        public long CachedInput;
        public long CacheWrite;
        public long Output;
        public long Reasoning;
        public long Total;
        public int Requests;

        public void Add(TokenUsage other)
        {
            if (other == null) return;
            Input += other.Input; CachedInput += other.CachedInput; CacheWrite += other.CacheWrite;
            Output += other.Output; Reasoning += other.Reasoning; Total += other.Total;
            Requests += other.Requests;
        }
    }

    internal sealed class TokenEvent
    {
        public DateTime Time;
        public TokenUsage Usage;
    }

    internal sealed class AccountUsage
    {
        public long? LifetimeTokens;
        public readonly Dictionary<DateTime, long> Daily = new Dictionary<DateTime, long>();
    }

    internal static class CodexService
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static QuotaSnapshot ReadQuota()
        {
            Dictionary<string, object> response = Request(2, "account/rateLimits/read", null, 15000);
            Dictionary<string, object> result = Child(response, "result");
            Dictionary<string, object> limits = Child(result, "rateLimits");
            Dictionary<string, object> primary = Child(limits, "primary");
            if (primary == null) return null;

            var snapshot = new QuotaSnapshot();
            snapshot.UsedPercent = Number(primary, "usedPercent");
            snapshot.WindowMinutes = Integer(primary, "windowDurationMins");
            snapshot.ResetAt = Integer(primary, "resetsAt");
            snapshot.Plan = Text(limits, "planType") ?? "unknown";
            Dictionary<string, object> resetCredits = Child(result, "rateLimitResetCredits");
            if (resetCredits != null && resetCredits.ContainsKey("availableCount"))
                snapshot.ResetCredits = (int)Integer(resetCredits, "availableCount");
            object creditsObject;
            if (resetCredits != null && resetCredits.TryGetValue("credits", out creditsObject))
            {
                object[] credits = creditsObject as object[];
                if (credits != null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (object item in credits)
                    {
                        Dictionary<string, object> credit = item as Dictionary<string, object>;
                        if (credit == null) continue;
                        string status = Text(credit, "status");
                        long expiresAt = Integer(credit, "expiresAt");
                        if (string.Equals(status, "available", StringComparison.OrdinalIgnoreCase) && expiresAt > now)
                            snapshot.ResetCreditExpiresAt.Add(expiresAt);
                    }
                    snapshot.ResetCreditExpiresAt.Sort();
                }
            }
            snapshot.UpdatedAt = DateTime.Now;
            return snapshot;
        }

        public static string ConsumeReset(string idempotencyKey)
        {
            string parameters = "{\"idempotencyKey\":\"" + idempotencyKey + "\",\"creditId\":null}";
            Dictionary<string, object> response = Request(3, "account/rateLimitResetCredit/consume", parameters, 20000);
            Dictionary<string, object> result = Child(response, "result");
            return Text(result, "outcome");
        }

        public static AccountUsage ReadAccountUsage()
        {
            Dictionary<string, object> response = Request(4, "account/usage/read", null, 15000);
            Dictionary<string, object> result = Child(response, "result");
            if (result == null) return null;
            var usage = new AccountUsage();
            Dictionary<string, object> summary = Child(result, "summary");
            if (summary != null && summary.ContainsKey("lifetimeTokens") && summary["lifetimeTokens"] != null)
                usage.LifetimeTokens = Integer(summary, "lifetimeTokens");
            object bucketsObject;
            if (result.TryGetValue("dailyUsageBuckets", out bucketsObject))
            {
                object[] buckets = bucketsObject as object[];
                if (buckets != null)
                {
                    foreach (object item in buckets)
                    {
                        Dictionary<string, object> bucket = item as Dictionary<string, object>;
                        DateTime date;
                        if (bucket != null && DateTime.TryParse(Text(bucket, "startDate"), out date))
                            usage.Daily[date.Date] = Integer(bucket, "tokens");
                    }
                }
            }
            return usage;
        }

        private static Dictionary<string, object> Request(int id, string method, string parameters, int timeoutMs)
        {
            Process process = null;
            try
            {
                string exe = FindCodexExe();
                if (exe == null) return null;
                var info = new ProcessStartInfo(exe, "app-server");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                process = Process.Start(info);
                if (process == null) return null;

                Write(process, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-quota\",\"version\":\"2.0\"}}}");
                if (ReadResponse(process, 1, 6000) == null) return null;
                Write(process, "{\"method\":\"initialized\"}");
                string request = "{\"id\":" + id + ",\"method\":\"" + method + "\"";
                if (parameters != null) request += ",\"params\":" + parameters;
                request += "}";
                Write(process, request);
                return ReadResponse(process, id, timeoutMs);
            }
            catch { return null; }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
            }
        }

        private static Dictionary<string, object> ReadResponse(Process process, int id, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int remaining = Math.Max(200, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                Task<string> read = Task.Factory.StartNew<string>(delegate { return process.StandardOutput.ReadLine(); });
                if (!read.Wait(remaining)) return null;
                string line = read.Result;
                if (line == null) return null;
                try
                {
                    var data = Json.DeserializeObject(line) as Dictionary<string, object>;
                    if (data != null && data.ContainsKey("id") && Convert.ToInt32(data["id"]) == id) return data;
                }
                catch { }
            }
            return null;
        }

        private static void Write(Process process, string text)
        {
            process.StandardInput.WriteLine(text);
            process.StandardInput.Flush();
        }

        private static string FindCodexExe()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (!Directory.Exists(root)) return null;
            try
            {
                return new DirectoryInfo(root).GetFiles("codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(x => x.LastWriteTimeUtc).Select(x => x.FullName).FirstOrDefault();
            }
            catch { return null; }
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key)) return null;
            return parent[key] as Dictionary<string, object>;
        }

        private static string Text(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null) return null;
            return Convert.ToString(parent[key]);
        }

        private static double Number(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null) return 0;
            return Convert.ToDouble(parent[key], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static long Integer(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null) return 0;
            return Convert.ToInt64(parent[key], System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal sealed class TokenFileCache
    {
        public long Length;
        public DateTime LastWriteUtc;
        public List<TokenEvent> Events;
    }

    internal static class TokenUsageStore
    {
        private static readonly object Sync = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Dictionary<string, TokenFileCache> Cache =
            new Dictionary<string, TokenFileCache>(StringComparer.OrdinalIgnoreCase);

        public static List<TokenEvent> ReadAll()
        {
            lock (Sync)
            {
                var files = new List<FileInfo>();
                string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                AddFiles(files, Path.Combine(home, "sessions"));
                AddFiles(files, Path.Combine(home, "archived_sessions"));
                var livePaths = new HashSet<string>(files.Select(x => x.FullName), StringComparer.OrdinalIgnoreCase);
                foreach (string oldPath in Cache.Keys.Where(x => !livePaths.Contains(x)).ToList()) Cache.Remove(oldPath);

                foreach (FileInfo file in files)
                {
                    TokenFileCache cached;
                    if (!Cache.TryGetValue(file.FullName, out cached) ||
                        cached.Length != file.Length || cached.LastWriteUtc != file.LastWriteTimeUtc)
                    {
                        Cache[file.FullName] = new TokenFileCache
                        {
                            Length = file.Length,
                            LastWriteUtc = file.LastWriteTimeUtc,
                            Events = ParseFile(file.FullName)
                        };
                    }
                }
                return Cache.Values.SelectMany(x => x.Events).OrderBy(x => x.Time).ToList();
            }
        }

        private static void AddFiles(List<FileInfo> target, string folder)
        {
            if (!Directory.Exists(folder)) return;
            try { target.AddRange(new DirectoryInfo(folder).GetFiles("*.jsonl", SearchOption.AllDirectories)); }
            catch { }
        }

        private static List<TokenEvent> ParseFile(string path)
        {
            var events = new List<TokenEvent>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                            line.IndexOf("\"last_token_usage\"", StringComparison.Ordinal) < 0) continue;
                        try
                        {
                            Dictionary<string, object> root = Json.DeserializeObject(line) as Dictionary<string, object>;
                            Dictionary<string, object> payload = Child(root, "payload");
                            if (Text(payload, "type") != "token_count") continue;
                            Dictionary<string, object> info = Child(payload, "info");
                            Dictionary<string, object> last = Child(info, "last_token_usage");
                            DateTimeOffset stamp;
                            if (last == null || !DateTimeOffset.TryParse(Text(root, "timestamp"), out stamp)) continue;
                            events.Add(new TokenEvent
                            {
                                Time = stamp.LocalDateTime,
                                Usage = new TokenUsage
                                {
                                    Input = Integer(last, "input_tokens"),
                                    CachedInput = Integer(last, "cached_input_tokens"),
                                    CacheWrite = Integer(last, "cache_write_input_tokens"),
                                    Output = Integer(last, "output_tokens"),
                                    Reasoning = Integer(last, "reasoning_output_tokens"),
                                    Total = Integer(last, "total_tokens"),
                                    Requests = 1
                                }
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return events;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key)) return null;
            return parent[key] as Dictionary<string, object>;
        }

        private static string Text(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null) return null;
            return Convert.ToString(parent[key]);
        }

        private static long Integer(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null) return 0;
            return Convert.ToInt64(parent[key], System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal sealed class UsageBar : Control
    {
        private double value;
        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public UsageBar() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Color.FromArgb(50, 58, 72))) e.Graphics.FillRectangle(back, 0, 0, Width, Height);
            int width = (int)Math.Round(Width * Value / 100.0);
            Color color = Value <= 10 ? Color.FromArgb(248, 113, 113) : Value <= 30 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(52, 211, 153);
            using (var fill = new SolidBrush(color)) e.Graphics.FillRectangle(fill, 0, 0, width, Height);
        }
    }

    internal sealed class TokenDetailsForm : Form
    {
        private readonly ComboBox range = new ComboBox();
        private readonly DateTimePicker from = new DateTimePicker();
        private readonly DateTimePicker to = new DateTimePicker();
        private readonly Button query = new Button();
        private readonly DataGridView grid = new DataGridView();
        private readonly Label rangeText = DetailLabel(9, FontStyle.Bold, Color.White);
        private readonly Label accountTotal = CardLabel();
        private readonly Label localTotal = CardLabel();
        private readonly Label inputTotal = CardLabel();
        private readonly Label outputTotal = CardLabel();
        private readonly Label cacheTotal = CardLabel();
        private readonly Label requestTotal = CardLabel();
        private readonly Label footer = DetailLabel(8, FontStyle.Regular, Color.FromArgb(148, 163, 184));
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool loading;
        private AccountUsage accountUsage;
        private DateTime accountFetched;

        public TokenDetailsForm()
        {
            Text = "Codex Token 消耗明细";
            Size = new Size(780, 520);
            MinimumSize = new Size(700, 450);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9);

            Panel toolbar = new Panel();
            toolbar.SetBounds(10, 8, 744, 38);
            toolbar.BackColor = Color.FromArgb(30, 41, 59);
            Controls.Add(toolbar);

            Label heading = DetailLabel(13, FontStyle.Bold, Color.FromArgb(248, 250, 252));
            heading.Text = "Token 消耗明细"; heading.SetBounds(18, 12, 150, 28);
            range.DropDownStyle = ComboBoxStyle.DropDownList;
            range.Items.AddRange(new object[] { "实时", "今天", "昨天", "近7天", "总计", "自定义" });
            range.SelectedIndex = 1; range.SetBounds(178, 13, 90, 26);
            range.FlatStyle = FlatStyle.Flat;
            range.BackColor = Color.FromArgb(51, 65, 85);
            range.ForeColor = Color.FromArgb(248, 250, 252);
            range.DrawMode = DrawMode.OwnerDrawFixed;
            range.ItemHeight = 22;
            range.DrawItem += DrawRangeItem;
            range.SelectedIndexChanged += delegate { UpdateCustomState(); LoadData(); };
            from.Format = DateTimePickerFormat.Custom; from.CustomFormat = "yyyy-MM-dd"; from.SetBounds(278, 13, 118, 26);
            to.Format = DateTimePickerFormat.Custom; to.CustomFormat = "yyyy-MM-dd"; to.SetBounds(404, 13, 118, 26);
            from.CalendarMonthBackground = Color.FromArgb(248, 250, 252);
            from.CalendarForeColor = Color.FromArgb(15, 23, 42);
            to.CalendarMonthBackground = Color.FromArgb(248, 250, 252);
            to.CalendarForeColor = Color.FromArgb(15, 23, 42);
            from.Value = DateTime.Today.AddDays(-6); to.Value = DateTime.Today;
            query.Text = "查询"; query.FlatStyle = FlatStyle.Flat; query.SetBounds(532, 13, 62, 26);
            query.FlatAppearance.BorderSize = 0;
            query.BackColor = Color.FromArgb(14, 165, 233); query.ForeColor = Color.White;
            query.Click += delegate { LoadData(true); };
            rangeText.TextAlign = ContentAlignment.MiddleRight; rangeText.SetBounds(604, 13, 146, 26);

            accountTotal.SetBounds(18, 54, 116, 58);
            localTotal.SetBounds(140, 54, 116, 58);
            inputTotal.SetBounds(262, 54, 116, 58);
            outputTotal.SetBounds(384, 54, 116, 58);
            cacheTotal.SetBounds(506, 54, 116, 58);
            requestTotal.SetBounds(628, 54, 122, 58);

            grid.SetBounds(18, 126, 732, 320);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            grid.BackgroundColor = Color.FromArgb(30, 41, 59);
            grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.GridColor = Color.FromArgb(51, 65, 85);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(51, 65, 85);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(248, 250, 252);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersHeight = 38;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowsDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            grid.RowsDefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(36, 49, 70);
            grid.AlternatingRowsDefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            grid.DefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(3, 105, 161);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            grid.DefaultCellStyle.NullValue = "—";
            grid.RowTemplate.Height = 34;
            grid.Columns.Add("time", "日期/时间");
            grid.Columns.Add("input", "输入");
            grid.Columns.Add("cache", "缓存命中");
            grid.Columns.Add("cacheRate", "命中率");
            grid.Columns.Add("output", "输出");
            grid.Columns.Add("reasoning", "推理输出");
            grid.Columns.Add("local", "本机总量");
            grid.Columns.Add("account", "账号总量");
            grid.Columns["time"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.Columns["cacheRate"].DefaultCellStyle.ForeColor = Color.FromArgb(110, 231, 183);
            foreach (DataGridViewColumn column in grid.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;

            footer.Text = "本机明细来自本机 Codex 任务记录；账号总量来自 Codex 官方账号统计。输入 Token 包含缓存命中部分。";
            footer.SetBounds(18, 454, 732, 25);
            footer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { heading, range, from, to, query, rangeText,
                accountTotal, localTotal, inputTotal, outputTotal, cacheTotal, requestTotal, grid, footer });
            toolbar.SendToBack();
            UpdateCustomState();
            Shown += delegate { LoadData(true); };
            timer.Interval = 10000; timer.Tick += delegate { LoadData(); }; timer.Start();
        }

        private void UpdateCustomState()
        {
            bool custom = range.SelectedIndex == 5;
            from.Visible = custom; to.Visible = custom; query.Visible = custom;
        }

        private void DrawRangeItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? Color.FromArgb(14, 165, 233) : Color.FromArgb(51, 65, 85);
            using (SolidBrush fill = new SolidBrush(background))
                e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, range.Items[e.Index].ToString(), range.Font, e.Bounds,
                Color.FromArgb(248, 250, 252), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }

        private void LoadData() { LoadData(false); }
        private void LoadData(bool forceAccount)
        {
            if (loading) return;
            loading = true; rangeText.Text = "正在统计…";
            int selected = range.SelectedIndex;
            DateTime selectedFrom = from.Value.Date;
            DateTime selectedTo = to.Value.Date;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<TokenEvent> all = TokenUsageStore.ReadAll();
                if (forceAccount || accountUsage == null || DateTime.Now - accountFetched > TimeSpan.FromMinutes(1))
                {
                    AccountUsage latestAccount = CodexService.ReadAccountUsage();
                    if (latestAccount != null) { accountUsage = latestAccount; accountFetched = DateTime.Now; }
                }

                DateTime start = DateTime.MinValue;
                DateTime end = DateTime.MaxValue;
                string title;
                if (selected == 0) title = "最近一次";
                else if (selected == 1) { start = DateTime.Today; end = start.AddDays(1); title = "今天"; }
                else if (selected == 2) { start = DateTime.Today.AddDays(-1); end = DateTime.Today; title = "昨天"; }
                else if (selected == 3) { start = DateTime.Today.AddDays(-6); end = DateTime.Today.AddDays(1); title = "近7天"; }
                else if (selected == 4) title = "全部记录";
                else
                {
                    start = selectedFrom;
                    end = selectedTo.AddDays(1);
                    title = selectedFrom.ToString("yyyy-MM-dd") + " 至 " + selectedTo.ToString("yyyy-MM-dd");
                }

                List<TokenEvent> chosen;
                if (selected == 0)
                    chosen = all.Count == 0 ? new List<TokenEvent>() : new List<TokenEvent> { all[all.Count - 1] };
                else
                    chosen = all.Where(x => x.Time >= start && x.Time < end).ToList();

                var total = new TokenUsage();
                foreach (TokenEvent item in chosen) total.Add(item.Usage);
                var rows = new List<object[]>();
                if (selected == 0)
                {
                    foreach (TokenEvent item in chosen)
                        rows.Add(Row(item.Time.ToString("yyyy-MM-dd HH:mm:ss"), item.Usage, AccountForDate(item.Time.Date)));
                }
                else
                {
                    foreach (var day in chosen.GroupBy(x => x.Time.Date).OrderByDescending(x => x.Key))
                    {
                        var usage = new TokenUsage();
                        foreach (TokenEvent item in day) usage.Add(item.Usage);
                        rows.Add(Row(day.Key.ToString("yyyy-MM-dd"), usage, AccountForDate(day.Key)));
                    }
                }
                long? officialTotal = null;
                if (accountUsage != null)
                {
                    if (selected == 4) officialTotal = accountUsage.LifetimeTokens;
                    else if (selected == 0 && chosen.Count > 0) officialTotal = AccountForDate(chosen[0].Time.Date);
                    else
                    {
                        long sum = 0; bool any = false;
                        foreach (KeyValuePair<DateTime, long> day in accountUsage.Daily)
                            if (day.Key >= start.Date && day.Key < end.Date) { sum += day.Value; any = true; }
                        if (any) officialTotal = sum;
                    }
                }

                try
                {
                    if (!IsDisposed) BeginInvoke((MethodInvoker)delegate
                    {
                        ShowData(title, total, officialTotal, rows);
                        loading = false;
                    });
                }
                catch { loading = false; }
            });
        }

        private long? AccountForDate(DateTime date)
        {
            if (accountUsage == null) return null;
            long value;
            return accountUsage.Daily.TryGetValue(date.Date, out value) ? (long?)value : null;
        }

        private static object[] Row(string time, TokenUsage usage, long? account)
        {
            return new object[] { time, FormatNumber(usage.Input), FormatNumber(usage.CachedInput), CacheRate(usage),
                FormatNumber(usage.Output), FormatNumber(usage.Reasoning), FormatNumber(usage.Total),
                account.HasValue ? FormatNumber(account.Value) : "—" };
        }

        private void ShowData(string title, TokenUsage total, long? officialTotal, List<object[]> rows)
        {
            rangeText.Text = title;
            accountTotal.Text = "账号总量\n" + (officialTotal.HasValue ? FormatShort(officialTotal.Value) : "—");
            localTotal.Text = "本机总量\n" + FormatShort(total.Total);
            inputTotal.Text = "输入\n" + FormatShort(total.Input);
            outputTotal.Text = "输出\n" + FormatShort(total.Output);
            cacheTotal.Text = "缓存命中\n" + FormatShort(total.CachedInput) + " · " + CacheRate(total);
            requestTotal.Text = "请求数\n" + FormatNumber(total.Requests);
            grid.Rows.Clear();
            foreach (object[] row in rows) grid.Rows.Add(row);
        }

        public static string FormatNumber(long value) { return value.ToString("N0"); }
        public static string CacheRate(TokenUsage usage)
        {
            if (usage == null || usage.Input <= 0) return "0%";
            return (usage.CachedInput * 100d / usage.Input).ToString("0.0") + "%";
        }
        public static string FormatShort(long value)
        {
            if (value >= 1000000000) return (value / 1000000000d).ToString("0.##") + "B";
            if (value >= 1000000) return (value / 1000000d).ToString("0.##") + "M";
            if (value >= 1000) return (value / 1000d).ToString("0.#") + "K";
            return value.ToString();
        }

        private static Label DetailLabel(float size, FontStyle style, Color color)
        {
            return new Label { AutoSize = false, Font = new Font("Microsoft YaHei UI", size, style),
                ForeColor = color, BackColor = Color.Transparent };
        }

        private static Label CardLabel()
        {
            return new Label { AutoSize = false, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252), BackColor = Color.FromArgb(30, 41, 59),
                BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleCenter };
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Label plan = LabelOf(8.4f, FontStyle.Bold, Color.FromArgb(167, 139, 250));
        private readonly Label quotaTitle = LabelOf(8.4f, FontStyle.Regular, Color.FromArgb(180, 188, 204));
        private readonly Label quotaValue = LabelOf(18, FontStyle.Bold, Color.White);
        private readonly Label resetTime = LabelOf(8.4f, FontStyle.Regular, Color.FromArgb(148, 163, 184));
        private readonly Label creditTitle = LabelOf(8.4f, FontStyle.Regular, Color.FromArgb(180, 188, 204));
        private readonly Label creditValue = LabelOf(9.6f, FontStyle.Bold, Color.White);
        private readonly Label creditExpiry = LabelOf(7.8f, FontStyle.Regular, Color.FromArgb(110, 231, 183));
        private readonly Label tokenTitle = LabelOf(8.4f, FontStyle.Regular, Color.FromArgb(180, 188, 204));
        private readonly Label tokenValue = LabelOf(9.6f, FontStyle.Bold, Color.White);
        private readonly Label status = LabelOf(7.8f, FontStyle.Regular, Color.FromArgb(100, 116, 139));
        private readonly UsageBar bar = new UsageBar();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer tokenTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Button resetButton;
        private readonly ToolTip tokenTip = new ToolTip();
        private bool refreshing;
        private bool tokenRefreshing;
        private bool resetting;
        private int availableCredits;
        private string pendingResetKey;
        private Point dragStart;
        private TokenDetailsForm details;

        public MainForm()
        {
            Text = "Codex 额度";
            Size = MinimumSize = MaximumSize = new Size(216, 198);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 18, Screen.PrimaryScreen.WorkingArea.Top + 18);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(19, 24, 36);
            ForeColor = Color.White;
            TopMost = true;
            ShowInTaskbar = false;

            Label title = LabelOf(10.8f, FontStyle.Bold, Color.White);
            title.Text = "CODEX"; title.SetBounds(11, 7, 66, 23);
            plan.TextAlign = ContentAlignment.MiddleRight; plan.SetBounds(70, 7, 79, 23);
            Button pin = ButtonOf("顶", 151, 6, 32); pin.Click += delegate { TopMost = !TopMost; pin.Text = TopMost ? "顶" : "浮"; };
            Button close = ButtonOf("×", 185, 6, 22); close.Font = new Font("Segoe UI", 12); close.Click += delegate { Hide(); };

            quotaTitle.SetBounds(11, 32, 120, 18);
            quotaValue.SetBounds(10, 49, 98, 32);
            resetTime.TextAlign = ContentAlignment.MiddleRight;
            resetTime.Font = new Font("Microsoft YaHei UI", 7.6f, FontStyle.Regular);
            resetTime.SetBounds(82, 53, 123, 24);
            bar.SetBounds(11, 83, 194, 6);
            creditTitle.SetBounds(11, 94, 67, 17);
            creditValue.SetBounds(77, 92, 62, 20);
            resetButton = ButtonOf("重置", 143, 91, 62); resetButton.Font = new Font("Microsoft YaHei UI", 8.4f); resetButton.Enabled = false;
            resetButton.Click += delegate { ConfirmReset(); };
            creditExpiry.Text = "到期时间 --";
            creditExpiry.SetBounds(11, 114, 194, 18);
            tokenTitle.Text = "最近请求"; tokenTitle.SetBounds(11, 140, 67, 17);
            tokenValue.Text = "--"; tokenValue.SetBounds(77, 138, 62, 20);
            Button detail = ButtonOf("明细", 143, 137, 62); detail.Font = new Font("Microsoft YaHei UI", 8.4f);
            detail.Click += delegate { ShowTokenDetails(); };
            status.SetBounds(11, 174, 164, 18);
            Button refresh = ButtonOf("↻", 178, 168, 28); refresh.Font = new Font("Segoe UI Symbol", 12);
            refresh.Click += delegate { RefreshQuota(); RefreshLiveToken(); };

            Controls.AddRange(new Control[] { title, plan, pin, close, quotaTitle, quotaValue, resetTime, bar,
                creditTitle, creditValue, creditExpiry, resetButton, tokenTitle, tokenValue, detail, status, refresh });
            foreach (Control c in Controls) { c.MouseDown += DragDown; c.MouseMove += DragMove; }

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示", null, delegate { Show(); Activate(); });
            menu.Items.Add("刷新", null, delegate { RefreshQuota(); });
            menu.Items.Add("退出", null, delegate { tray.Visible = false; Application.Exit(); });
            tray.Text = "Codex 额度"; tray.Icon = SystemIcons.Information; tray.Visible = true; tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Show(); Activate(); };

            timer.Interval = 30000; timer.Tick += delegate { RefreshQuota(); }; timer.Start();
            tokenTimer.Interval = 3000; tokenTimer.Tick += delegate { RefreshLiveToken(); }; tokenTimer.Start();
            Shown += delegate { RefreshQuota(); RefreshLiveToken(); };
        }

        private void RefreshQuota()
        {
            if (refreshing) return;
            refreshing = true;
            status.Text = "正在实时查询…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                QuotaSnapshot data = CodexService.ReadQuota();
                try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { refreshing = false; ShowQuota(data); }); }
                catch { refreshing = false; }
            });
        }

        private void ShowQuota(QuotaSnapshot data)
        {
            if (data == null)
            {
                plan.Text = "离线"; quotaTitle.Text = "无法连接本机 Codex"; quotaValue.Text = "--";
                resetTime.Text = ""; bar.Value = 0; creditTitle.Text = "重置额度"; creditValue.Text = "--"; creditExpiry.Text = "到期时间 --";
                availableCredits = 0; resetButton.Enabled = false; status.Text = "请先登录并启动 Codex"; return;
            }
            double remaining = Math.Max(0, 100 - data.UsedPercent);
            plan.Text = (data.Plan ?? "unknown").ToUpperInvariant();
            quotaTitle.Text = WindowName(data.WindowMinutes) + "剩余";
            quotaValue.Text = remaining.ToString("0") + "%";
            resetTime.Text = "至 " + ResetText(data.ResetAt);
            bar.Value = remaining;
            availableCredits = data.ResetCredits.GetValueOrDefault(0);
            creditTitle.Text = "重置额度";
            creditValue.Text = data.ResetCredits.HasValue ? availableCredits + "次" : "--";
            if (data.ResetCreditExpiresAt.Count > 0)
            {
                creditExpiry.Text = "最近到期 " + UnixLocal(data.ResetCreditExpiresAt[0]).ToString("yyyy-MM-dd HH:mm:ss");
                tokenTip.SetToolTip(creditExpiry, BuildCreditExpiryTip(data.ResetCreditExpiresAt));
            }
            else
            {
                creditExpiry.Text = availableCredits > 0 ? "到期时间暂未返回" : "暂无可用重置额度";
                tokenTip.SetToolTip(creditExpiry, "");
            }
            resetButton.Enabled = availableCredits > 0 && !resetting;
            resetButton.Text = pendingResetKey == null ? "重置" : "重试";
            status.Text = "实时 " + data.UpdatedAt.ToString("HH:mm:ss") + " · 30秒刷新";
            tray.Text = "Codex 剩余 " + remaining.ToString("0") + "% · 重置 " + availableCredits + "次";
        }

        private void RefreshLiveToken()
        {
            if (tokenRefreshing) return;
            tokenRefreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<TokenEvent> events = TokenUsageStore.ReadAll();
                TokenEvent latest = events.Count > 0 ? events[events.Count - 1] : null;
                try
                {
                    if (!IsDisposed) BeginInvoke((MethodInvoker)delegate
                    {
                        tokenRefreshing = false;
                        if (latest == null) { tokenValue.Text = "--"; return; }
                        tokenValue.Text = TokenDetailsForm.FormatShort(latest.Usage.Total);
                        tokenTip.SetToolTip(tokenValue,
                            "时间：" + latest.Time.ToString("HH:mm:ss") +
                            "\n输入：" + TokenDetailsForm.FormatNumber(latest.Usage.Input) +
                            "\n缓存命中：" + TokenDetailsForm.FormatNumber(latest.Usage.CachedInput) +
                            "（" + TokenDetailsForm.CacheRate(latest.Usage) + "）" +
                            "\n输出：" + TokenDetailsForm.FormatNumber(latest.Usage.Output) +
                            "\n推理输出：" + TokenDetailsForm.FormatNumber(latest.Usage.Reasoning));
                    });
                }
                catch { tokenRefreshing = false; }
            });
        }

        private void ShowTokenDetails()
        {
            if (details == null || details.IsDisposed) details = new TokenDetailsForm();
            details.Show(); details.Activate();
        }

        private void ConfirmReset()
        {
            if (resetting || availableCredits <= 0) return;
            string message = "将使用 1 次重置额度，立即重置当前 Codex 用量窗口。\n\n当前可用：" + availableCredits + " 次\n\n已使用的重置额度无法恢复，确定继续吗？";
            if (MessageBox.Show(this, message, "确认重置 Codex 额度", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (pendingResetKey == null) pendingResetKey = Guid.NewGuid().ToString();
            resetting = true; resetButton.Enabled = false; resetButton.Text = "处理中"; status.Text = "正在提交重置…";
            string key = pendingResetKey;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string outcome = CodexService.ConsumeReset(key);
                try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { FinishReset(outcome); }); } catch { }
            });
        }

        private void FinishReset(string outcome)
        {
            resetting = false;
            if (outcome == "reset" || outcome == "alreadyRedeemed")
            {
                pendingResetKey = null;
                MessageBox.Show(this, "重置成功，正在刷新最新额度。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshQuota(); return;
            }
            if (outcome == "nothingToReset" || outcome == "noCredit")
            {
                pendingResetKey = null;
                string text = outcome == "nothingToReset" ? "当前没有符合重置条件的用量窗口，未消耗重置额度。" : "当前没有可用的重置额度。";
                MessageBox.Show(this, text, "无法重置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshQuota(); return;
            }
            resetButton.Text = "重试"; resetButton.Enabled = availableCredits > 0; status.Text = "结果未确认，可安全重试";
            MessageBox.Show(this, "没有收到明确结果。再次点击“重试”会复用同一个请求标识，避免重复扣除。", "请求未确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string WindowName(long minutes)
        {
            if (minutes > 0 && minutes % 10080 == 0) return (minutes / 10080) + "周额度";
            if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440) + "天额度";
            if (minutes > 0 && minutes % 60 == 0) return (minutes / 60) + "小时额度";
            return "订阅额度";
        }

        private static string ResetText(long unix)
        {
            if (unix <= 0) return "未知";
            DateTime local = UnixLocal(unix);
            TimeSpan left = local - DateTime.Now;
            if (left.TotalSeconds <= 0) return "即将刷新";
            if (left.TotalDays >= 1) return local.ToString("M月d日 HH:mm");
            if (left.TotalHours >= 1) return ((int)left.TotalHours) + "小时" + left.Minutes + "分后";
            return Math.Max(1, left.Minutes) + "分钟后";
        }

        private static DateTime UnixLocal(long unix)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
        }

        private static string BuildCreditExpiryTip(List<long> expiries)
        {
            var lines = new List<string>();
            for (int i = 0; i < expiries.Count; i++)
                lines.Add("第 " + (i + 1) + " 次：" + UnixLocal(expiries[i]).ToString("yyyy-MM-dd HH:mm:ss"));
            return "可用重置额度到期时间\n" + string.Join("\n", lines.ToArray());
        }

        private static Label LabelOf(float size, FontStyle style, Color color)
        {
            return new Label { AutoSize = false, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, BackColor = Color.Transparent };
        }

        private Button ButtonOf(string text, int x, int y, int width)
        {
            Button b = new Button { Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(148, 163, 184), BackColor = BackColor, TabStop = false };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 44, 61); b.SetBounds(x, y, width, 24); return b;
        }

        private void DragDown(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragStart = new Point(e.X, e.Y); }
        private void DragMove(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            tray.Visible = false; base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\CodexQuotaMonitor", out created))
            {
                if (!created) { MessageBox.Show("Codex 额度小程序已经在运行。", "Codex 额度"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
