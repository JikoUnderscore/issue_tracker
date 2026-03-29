#!/usr/bin/env dotnet-script
#:package Photino.NET@4.0.16
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
// #:property OutputType=WinExe


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Photino.NET;

namespace IssueApp
{
    // --- Message Models for JS <-> C# Interop ---
    public class ApiRequest
    {
        public string action { get; set; } = "";
        public string file { get; set; } = "";
        public Dictionary<string, string> data { get; set; } = new();
    }

    public class ApiResponse
    {
        public string action { get; set; } = "";
        public object? payload { get; set; }
        public string? error { get; set; }
    }

    // --- Data Models ---
    public class IssueItem 
    {
        public string file { get; set; } = "";
        public string title { get; set; } = "";
        public string status { get; set; } = "";
        public string[] labels { get; set; } = Array.Empty<string>();
        public string created { get; set; } = "";
    }
    
    public class CommentItem 
    {
        public string author { get; set; } = "";
        public string date { get; set; } = "";
        public string raw { get; set; } = "";
        public string html { get; set; } = "";
    }
    
    public class IssueDetail 
    {
        public Dictionary<string, string> meta { get; set; } = new();
        public string rendered_body { get; set; } = "";
        public List<CommentItem> rendered_comments { get; set; } = new();
        public string raw_body { get; set; } = "";
    }

    // --- JSON Source Generator (For fast, trim-safe compilation) ---
    [JsonSerializable(typeof(ApiRequest))]
    [JsonSerializable(typeof(ApiResponse))]
    [JsonSerializable(typeof(List<IssueItem>))]
    [JsonSerializable(typeof(IssueDetail))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class AppJsonContext : JsonSerializerContext { }

    class Program
    {
        static string issuesDir = "./issues";

        [STAThread]
        static void Main(string[] args)
        {
            // Setup Issues Directory
            // issuesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "issues");
            if (!Directory.Exists(issuesDir)) Directory.CreateDirectory(issuesDir);

            var window = new PhotinoWindow()
                .SetTitle("Local Issues")
                .SetSize(1250, 850)
                .SetUseOsDefaultSize(false)
                .RegisterWebMessageReceivedHandler(HandleWebMessage)
                .LoadRawString(HtmlData.Content); // Load the embedded HTML directly

            window.WaitForClose();
        }

        // --- The Message Router (Replaces the HTTP Server) ---
        private static void HandleWebMessage(object? sender, string message)
        {
            var window = (PhotinoWindow)sender!;
            var request = JsonSerializer.Deserialize(message, AppJsonContext.Default.ApiRequest);
            if (request == null) return;

            var response = new ApiResponse { action = request.action };

            try
            {
                switch (request.action)
                {
                    case "list":
                        response.payload = GetIssuesList();
                        break;
                    case "load":
                        response.payload = LoadIssue(request.file);
                        break;
                    case "save":
                        SaveIssue(request.data);
                        response.payload = new { ok = true };
                        break;
                    case "comment":
                        AddComment(request.data);
                        response.payload = new { ok = true };
                        break;
                    case "create":
                        response.payload = new { file = CreateIssue(request.data) };
                        break;
                    default:
                        response.error = "Unknown action";
                        break;
                }
            }
            catch (Exception ex)
            {
                response.error = ex.Message;
            }

            // Send the JSON answer back to Javascript
            string jsonResponse = JsonSerializer.Serialize(response, AppJsonContext.Default.ApiResponse);
            window.SendWebMessage(jsonResponse);
        }

        // --- Core Application Logic ---

        private static List<IssueItem> GetIssuesList()
        {
            var list = new List<IssueItem>();
            foreach (var file in Directory.GetFiles(issuesDir, "*.md"))
            {
                var p = ParseIssue(File.ReadAllText(file));
                var name = Path.GetFileName(file);
                p.meta.TryGetValue("title", out var t);
                p.meta.TryGetValue("status", out var s);
                p.meta.TryGetValue("labels", out var l);
                p.meta.TryGetValue("created", out var c);

                list.Add(new IssueItem
                {
                    file = name,
                    title = string.IsNullOrEmpty(t) ? name : t,
                    status = string.IsNullOrEmpty(s) ? "open" : s,
                    labels = (l ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToArray(),
                    created = c ?? ""
                });
            }
            return list;
        }

        private static IssueDetail LoadIssue(string filename)
        {
            string safePath = SafePath(filename);
            var parsed = ParseIssue(File.ReadAllText(safePath));
            
            return new IssueDetail
            {
                meta = parsed.meta,
                rendered_body = RenderMarkdown(parsed.meta["body"]),
                rendered_comments = ParseComments(parsed.meta.GetValueOrDefault("comments", "")),
                raw_body = parsed.meta["body"]
            };
        }

        private static void SaveIssue(Dictionary<string, string> data)
        {
            string file = SafePath(data["file"]);
            var parsed = ParseIssue(File.ReadAllText(file));

            parsed.meta["title"] = data["title"];
            parsed.meta["status"] = data["status"];
            parsed.meta["labels"] = ValidateLabels(data.GetValueOrDefault("labels", ""));

            string newContent = SerializeIssue(parsed.meta, data.GetValueOrDefault("body", ""), parsed.meta.GetValueOrDefault("comments", ""));
            File.WriteAllText(file, newContent);
        }

        private static void AddComment(Dictionary<string, string> data)
        {
            string file = SafePath(data["file"]);
            var parsed = ParseIssue(File.ReadAllText(file));

            string author = data.GetValueOrDefault("author", "anon");
            if (string.IsNullOrWhiteSpace(author)) author = "anon";
            
            string stamp = DateTime.UtcNow.ToString("O");
            string body = data["body"];

            string commentBlock = $"\n\nauthor: {author}\ndate: {stamp}\nbody: {body}";
            string comments = parsed.meta.GetValueOrDefault("comments", "").TrimEnd() + commentBlock;

            File.WriteAllText(file, SerializeIssue(parsed.meta, parsed.meta["body"], comments));
        }

        private static string CreateIssue(Dictionary<string, string> data)
        {
            string title = data["title"];
            string safeTitle = Regex.Replace(title.ToLower(), @"[^a-z0-9\-]", "-");
            string ts = DateTime.UtcNow.ToString("O");
            int idx = Directory.GetFiles(issuesDir, "*.md").Length + 1;
            string name = $"{idx:04d}-{safeTitle}.md";

            var meta = new Dictionary<string, string>
            {
                { "title", title },
                { "status", "open" },
                { "labels", ValidateLabels(data.GetValueOrDefault("labels", "")) },
                { "created", ts }
            };

            File.WriteAllText(SafePath(name), SerializeIssue(meta, data.GetValueOrDefault("body", ""), ""));
            return name;
        }

        // --- Parsing & formatting ---

        private static IssueDetail ParseIssue(string text)
        {
            var res = new IssueDetail();
            var parts = text.Split(new[] { "---" }, 3, StringSplitOptions.None);
            
            if (parts.Length >= 3)
            {
                foreach (var line in parts[1].Trim().Split('\n'))
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        string k = line.Substring(0, colonIdx).Trim();
                        string v = line.Substring(colonIdx + 1).Trim();
                        res.meta[k] = v;
                    }
                }

                var rest = parts[2];
                var subParts = rest.Split(new[] { "***" }, 2, StringSplitOptions.None);
                res.meta["body"] = subParts[0].Trim();
                if (subParts.Length > 1) res.meta["comments"] = subParts[1].Trim();
            }
            return res;
        }

        private static string SerializeIssue(Dictionary<string, string> meta, string body, string comments)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            foreach (var kv in meta) sb.AppendLine($"{kv.Key}: {kv.Value}");
            sb.AppendLine("---");
            sb.AppendLine(body.Trim());
            sb.AppendLine();
            
            if (!string.IsNullOrWhiteSpace(comments))
            {
                sb.AppendLine("***");
                sb.AppendLine(comments.Trim());
            }
            return sb.ToString();
        }

        private static List<CommentItem> ParseComments(string raw)
        {
            var list = new List<CommentItem>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            string? currentAuthor = null, currentDate = null;
            var bodyLines = new List<string>();

            void Flush()
            {
                if (currentAuthor != null && bodyLines.Count > 0)
                {
                    string text = string.Join("\n", bodyLines).Trim();
                    list.Add(new CommentItem
                    {
                        author = System.Net.WebUtility.HtmlEncode(currentAuthor),
                        date = System.Net.WebUtility.HtmlEncode(currentDate ?? ""),
                        raw = text,
                        html = RenderMarkdown(text)
                    });
                }
            }

            foreach (var line in raw.Split('\n'))
            {
                if (line.StartsWith("author:"))
                {
                    Flush();
                    currentAuthor = line.Substring(7).Trim();
                    currentDate = "";
                    bodyLines.Clear();
                }
                else if (line.StartsWith("date:") && currentAuthor != null)
                {
                    currentDate = line.Substring(5).Trim();
                }
                else if (line.StartsWith("body:") && currentAuthor != null)
                {
                    bodyLines.Add(line.Substring(5).TrimStart());
                }
                else if (currentAuthor != null)
                {
                    bodyLines.Add(line);
                }
            }
            Flush();
            return list;
        }

        private static string RenderMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var esc = System.Net.WebUtility.HtmlEncode(text);

            // 1. Link to other issues: Find #0001, #0002, etc.
            esc = Regex.Replace(esc, @"#(\d{4})", m =>
            {
                string id = m.Groups[1].Value;
                string[] matches = Directory.GetFiles(issuesDir, id + "-*.md");
                if (matches.Length > 0)
                {
                    string fileName = Path.GetFileName(matches[0]);
                    return $"<a href=\"#\" onclick=\"loadIssue('{fileName}'); return false;\" style=\"color:var(--accent); text-decoration:none; font-weight:bold;\">#{id}</a>";
                }
                return m.Value; 
            });

            esc = Regex.Replace(esc, @"^### (.+)$", "<h3>$1</h3>", RegexOptions.Multiline);
            esc = Regex.Replace(esc, @"^## (.+)$", "<h2>$1</h2>", RegexOptions.Multiline);
            esc = Regex.Replace(esc, @"^# (.+)$", "<h1>$1</h1>", RegexOptions.Multiline);
            esc = Regex.Replace(esc, @"\*\*(.+?)\*\*", "<b>$1</b>");
            esc = Regex.Replace(esc, @"\*(.+?)\*", "<i>$1</i>");
            esc = Regex.Replace(esc, @"`(.+?)`", "<code>$1</code>");

            var paragraphs = new List<string>();
            foreach (var p in Regex.Split(esc, @"\n\n"))
            {
                string trimmed = p.Trim();
                if (!string.IsNullOrEmpty(trimmed)) paragraphs.Add($"<p>{trimmed}</p>");
            }
            return string.Join("\n", paragraphs);
        }

        // --- Helpers ---

        private static string SafePath(string filename)
        {
            string safeName = Path.GetFileName(filename);
            string path = Path.Combine(issuesDir, safeName);
            if (!path.StartsWith(issuesDir)) throw new Exception("Forbidden path");
            return path;
        }

        private static string ValidateLabels(string raw)
        {
            var valid = new List<string>();
            foreach (var l in raw.Split(','))
            {
                string t = l.Trim();
                if (!string.IsNullOrEmpty(t) && Regex.IsMatch(t, @"^[a-zA-Z0-9._\- ]+$"))
                {
                    valid.Add(t);
                }
            }
            return string.Join(", ", valid);
        }
    }

    public static class HtmlData
    {
        public const string Content = @"<!doctype html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <title>Local Issues</title>
    <style>
        #commentsCard { margin-top: 12px; }
        #commentsArea .comment:first-child { border-top: none; }
        :root { --bg: #f6f8fa; --card: #fff; --muted: #6a737d; --border: #d0d7de; --accent: #0969da; --green: #2da44e; --red: #cf222e; }
        body { background: var(--bg); font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Arial; color: #24292f; margin: 0; }
        .wrap { display: flex; gap: 24px; max-width: 1200px; margin: 24px auto; padding: 0 16px; }
        .sidebar { width: 340px; }
        .main { flex: 1; min-width: 0; }
        header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
        h1 { font-size: 20px; margin: 0; }
        .card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 12px; margin-bottom: 12px; box-shadow: 0 1px 0 rgba(27, 31, 35, 0.04); }
        .search, input[type=text], textarea { width: 100%; padding: 8px; border-radius: 6px; border: 1px solid var(--border); box-sizing: border-box; }
        textarea { min-height: 160px; font-family: inherit; resize: vertical; }
        .issue-row { display: flex; gap: 10px; align-items: flex-start; padding: 8px; border-radius: 6px; cursor: pointer; }
        .issue-row:hover { background: #f2f4f6; }
        .issue-title { font-weight: 600; font-size: 15px; margin-bottom: 4px; color: #0b1220; }
        .meta { color: var(--muted); font-size: 12px; }
        .labels { display: inline-flex; gap: 6px; margin-left: 6px; flex-wrap: wrap; }
        .label { background: #e6edf3; color: #0b1220; padding: 2px 8px; border-radius: 999px; font-size: 12px; }
        .status { font-size: 12px; padding: 4px 8px; border-radius: 8px; color: #fff; }
        .status.open { background: var(--green); }
        .status.closed { background: var(--red); }
        .button { background: var(--accent); color: #fff; border: none; padding: 8px 10px; border-radius: 6px; cursor: pointer; font-weight: 600; }
        .btn-ghost { background: transparent; border: 1px solid var(--border); padding: 7px 10px; border-radius: 6px; cursor: pointer; }
        .tiny { font-size: 12px; padding: 6px 8px; }
        .issue-body { margin-top: 12px; color: #24292f; line-height: 1.6; }
        .comment { border-left: 4px solid #d0d7de; padding-left: 12px; border-top: 1px solid #eef0f2; padding: 10px 0 10px 12px; }
        .small { font-size: 13px; color: var(--muted); }
        .top-row { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
    </style>
</head>
<body>
    <div class=""wrap"">
        <div class=""sidebar"">
            <header><h1>Local Issues</h1></header>
            <div class=""card"">
                <input id=""search"" class=""search"" placeholder=""Search title or body..."" />
                <div style=""display:flex; gap:8px; margin-top:8px;"">
                    <select id=""statusFilter"" class=""tiny btn-ghost"">
                        <option value=""all"">All</option>
                        <option value=""open"">Open</option>
                        <option value=""closed"">Closed</option>
                    </select>
                    <select id=""labelFilter"" class=""tiny btn-ghost""><option value="""">All labels</option></select>
                    <div style=""flex:1""></div>
                    <div class=""order-toggle small"" id=""orderToggle"" style=""cursor:pointer"">Newest ⇅</div>
                </div>
            </div>
            <div class=""card"" style=""padding:8px;"">
                <div style=""display:flex; gap:8px; align-items:center; justify-content:space-between;"">
                    <div><b>New issue</b><div class=""small"">Create a new issue file</div></div>
                </div>
                <div style=""margin-top:8px;"">
                    <input id=""newTitle"" placeholder=""Title (required)"" style=""margin-bottom:8px;"" />
                    <input id=""newLabels"" placeholder=""Labels (comma separated)"" style=""margin-bottom:8px;"" />
                    <textarea id=""newBody"" placeholder=""Markdown body""></textarea>
                    <div style=""display:flex; gap:8px; margin-top:8px;"">
                        <button class=""button"" id=""createBtn"">Create Issue</button>
                    </div>
                </div>
            </div>
            <div id=""list"" class=""card"" style=""max-height:60vh; overflow:auto;""></div>
        </div>
        <div class=""main"">
            <div class=""top-row"">
                <div>
                    <div id=""viewTitle"" style=""font-size:20px; font-weight:700;""></div>
                    <div id=""viewMeta"" class=""small""></div>
                </div>
                <div id=""viewActions"" style=""display:flex; gap:8px; align-items:center;""></div>
            </div>
            <div id=""issueCard"" class=""card"" style=""margin-top:12px;"">
                <div class=""top-row"">
                    <div><div id=""viewLabels"" class=""labels"" style=""margin-top:6px;""></div></div>
                </div>
                <div id=""viewRendered"" class=""issue-body"" style=""margin-top:12px;"">Select an issue to view it here.</div>
                <div id=""editorArea"" style=""display:none; margin-top:12px;"">
                    <div class=""editor"">
                        <input id=""editTitle"" placeholder=""Title"" style=""margin-bottom:8px;"" />
                        <input id=""editLabels"" placeholder=""Labels (comma)"" style=""margin-bottom:8px;"" />
                        <select id=""editStatus"" style=""margin-bottom:8px; padding:4px;"">
                            <option>open</option>
                            <option>closed</option>
                        </select>
                        <textarea id=""editBody""></textarea>
                        <div style=""display:flex; gap:8px; margin-top:8px;"">
                            <button id=""saveBtn"" class=""button"">Save</button>
                            <button id=""cancelEdit"" class=""btn-ghost"">Cancel</button>
                        </div>
                    </div>
                </div>
            </div>
            <div id=""commentsCard"" class=""card"" style=""display:none;"">
                <h3 style=""margin-top:0"">Comments</h3>
                <div id=""commentsArea""></div>
                <div style=""margin-top: 12px; display:flex; flex-direction: column; gap: 8px;"">
                    <input id=""cAuthor"" placeholder=""Your name"">
                    <textarea id=""cBody"" placeholder=""Write a comment...""></textarea>
                    <button id=""postComment"" class=""button"" style=""align-self: flex-start;"">Comment</button>
                </div>
            </div>
        </div>
    </div>

    <script>
        let issues = [];
        let currentFile = null;
        let orderNewest = true;
        const qs = id => document.getElementById(id);

        // --- Photino Interop Bridge ---
        const pendingRequests = {};
        
        window.external.receiveMessage(msg => {
            const res = JSON.parse(msg);
            if (pendingRequests[res.action]) {
                pendingRequests[res.action](res);
            }
        });

        function apiCall(action, file = """", data = {}) {
            return new Promise((resolve, reject) => {
                pendingRequests[action] = (res) => {
                    if (res.error) {
                        alert(""Error: "" + res.error);
                        reject(new Error(res.error));
                    } else {
                        resolve(res.payload);
                    }
                };
                window.external.sendMessage(JSON.stringify({ action, file, data }));
            });
        }

        // --- App Logic ---
        function loadList() {
            apiCall(""list"").then(data => {
                issues = data;
                renderList();
                populateLabels();
            });
        }

        function renderList() {
            const list = qs(""list"");
            list.innerHTML = """";
            let filtered = [...issues];
            const q = qs(""search"").value.toLowerCase();
            const status = qs(""statusFilter"").value;
            const label = qs(""labelFilter"").value;
            
            if (q) filtered = filtered.filter(i => i.title.toLowerCase().includes(q));
            if (status !== ""all"") filtered = filtered.filter(i => i.status === status);
            if (label) filtered = filtered.filter(i => i.labels.includes(label));
            
            filtered.sort((a, b) => orderNewest ? b.created.localeCompare(a.created) : a.created.localeCompare(b.created));
            
            for (const i of filtered) {
                const div = document.createElement(""div"");
                div.className = ""issue-row"";
                div.innerHTML = `
                    <div style=""flex:1"">
                        <div class=""issue-title"">${i.title}</div>
                        <div class=""meta"">
                            <span class=""status ${i.status}"">${i.status}</span>
                            ${i.labels.map(l => `<span class=""label"">${l}</span>`).join("""")}
                        </div>
                    </div>`;
                div.onclick = () => loadIssue(i.file);
                list.appendChild(div);
            }
        }

        function populateLabels() {
            const sel = qs(""labelFilter"");
            const labels = new Set();
            issues.forEach(i => i.labels.forEach(l => labels.add(l)));
            sel.innerHTML = `<option value="""">All labels</option>`;
            [...labels].sort().forEach(l => {
                const o = document.createElement(""option"");
                o.value = l; o.textContent = l;
                sel.appendChild(o);
            });
        }

        function loadIssue(file) {
            apiCall(""load"", file).then(d => {
                currentFile = file;
                qs(""viewTitle"").textContent = d.meta.title || file;
                qs(""viewMeta"").innerHTML = `<span class=""status ${d.meta.status}"">${d.meta.status}</span> · ${new Date(d.meta.created).toLocaleString(""fr"") || """"}`;
                
                const labels = (d.meta.labels || """").split("","").map(l => l.trim()).filter(Boolean);
                const lbl = qs(""viewLabels"");
                lbl.innerHTML = """";
                labels.forEach(l => {
                    const s = document.createElement(""span"");
                    s.className = ""label""; s.textContent = l;
                    lbl.appendChild(s);
                });
                
                qs(""viewRendered"").innerHTML = d.rendered_body;
                qs(""editTitle"").value = d.meta.title || """";
                qs(""editLabels"").value = d.meta.labels || """";
                qs(""editStatus"").value = d.meta.status || ""open"";
                qs(""editBody"").value = d.raw_body || """";
                
                qs(""commentsCard"").style.display = ""block"";
                renderComments(d.rendered_comments);
                
                qs(""editorArea"").style.display = ""none"";
                qs(""viewActions"").innerHTML = `<button class=""btn-ghost tiny"" onclick=""qs('editorArea').style.display='block'"">Edit</button>`;
            });
        }

        function renderComments(list) {
            const area = qs(""commentsArea"");
            area.innerHTML = """";
            if (!list || !list.length) return;
            list.forEach(c => {
                const initials = c.author.split("" "").map(s => s[0]?.toUpperCase()).join("""").slice(0, 2);
                const div = document.createElement(""div"");
                div.className = ""comment"";
                div.innerHTML = `
                    <div style=""display:flex; gap:12px;"">
                        <div style=""width:32px;height:32px;border-radius:50%;background:#d0d7de;display:flex;align-items:center;justify-content:center;font-weight:700;"">${initials}</div>
                        <div style=""flex:1"">
                            <div class=""meta"" style=""margin-bottom:6px""><b>${c.author}</b> commented on ${new Date(c.date).toLocaleString(""fr"")}</div>
                            <div class=""issue-body"">${c.html}</div>
                        </div>
                    </div>`;
                area.appendChild(div);
            });
        }

        qs(""cancelEdit"").onclick = () => qs(""editorArea"").style.display = ""none"";
        
        qs(""saveBtn"").onclick = () => {
            apiCall(""save"", """", {
                file: currentFile,
                title: qs(""editTitle"").value,
                labels: qs(""editLabels"").value,
                status: qs(""editStatus"").value,
                body: qs(""editBody"").value
            }).then(() => {
                qs(""editorArea"").style.display = ""none"";
                loadList();
                loadIssue(currentFile);
            });
        };

        qs(""postComment"").onclick = () => {
            if (currentFile) {
                apiCall(""comment"", """", {
                    file: currentFile,
                    author: qs(""cAuthor"").value || ""anon"",
                    body: qs(""cBody"").value
                }).then(() => {
                    qs(""cBody"").value = """";
                    loadIssue(currentFile);
                });
            }
        };

        qs(""createBtn"").onclick = () => {
            apiCall(""create"", """", {
                title: qs(""newTitle"").value,
                labels: qs(""newLabels"").value,
                body: qs(""newBody"").value
            }).then(() => {
                qs(""newTitle"").value = """";
                qs(""newLabels"").value = """";
                qs(""newBody"").value = """";
                loadList();
            });
        };

        qs(""search"").oninput = renderList;
        qs(""statusFilter"").onchange = renderList;
        qs(""labelFilter"").onchange = renderList;
        qs(""orderToggle"").onclick = () => {
            orderNewest = !orderNewest;
            qs(""orderToggle"").textContent = orderNewest ? ""Newest ⇅"" : ""Oldest ⇅"";
            renderList();
        };

        // Init
        loadList();
    </script>
</body>
</html>";
    }
}