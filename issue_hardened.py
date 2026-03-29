import datetime
import html as html_escape
import http.server
import json
import os
import pathlib
import re
import typing
import urllib.parse

INDEX_DATA = """<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>Local Issues - Viewer & Editor</title>
    <style>
        #commentsCard {
            margin-top: 12px;
        }
        #commentsArea .comment:first-child {
            border-top: none;
        }
        :root {
            --bg: #f6f8fa;
            --card: #fff;
            --muted: #6a737d;
            --border: #d0d7de;
            --accent: #0969da;
            --green: #2da44e;
            --red: #cf222e;
        }
        body {
            background: var(--bg);
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial;
            color: #24292f;
            margin: 0;
        }
        .wrap {
            display: flex;
            gap: 24px;
            max-width: 1200px;
            margin: 24px auto;
            padding: 0 16px;
        }
        .sidebar {
            width: 340px;
        }
        .main {
            flex: 1;
            min-width: 0;
        }
        header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 12px;
        }
        h1 {
            font-size: 20px;
            margin: 0;
        }
        .controls {
            display: flex;
            gap: 8px;
            align-items: center;
        }
        .search {
            width: 100%;
            padding: 8px 10px;
            border-radius: 6px;
            border: 1px solid var(--border);
        }
        .card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 12px;
            margin-bottom: 12px;
            box-shadow: 0 1px 0 rgba(27, 31, 35, 0.04);
        }
        .issue-row {
            display: flex;
            gap: 10px;
            align-items: flex-start;
            padding: 8px;
            border-radius: 6px;
            cursor: pointer;
        }
        .issue-row:hover {
            background: #f2f4f6;
        }
        .issue-title {
            font-weight: 600;
            font-size: 15px;
            margin-bottom: 4px;
            color: #0b1220;
        }
        .meta {
            color: var(--muted);
            font-size: 12px;
        }
        .labels {
            display: inline-flex;
            gap: 6px;
            margin-left: 6px;
            flex-wrap: wrap;
        }
        .label {
            background: #e6edf3;
            color: #0b1220;
            padding: 2px 8px;
            border-radius: 999px;
            font-size: 12px;
        }
        .status {
            font-size: 12px;
            padding: 4px 8px;
            border-radius: 8px;
            color: #fff;
        }
        .status.open {
            background: var(--green);
        }
        .status.closed {
            background: var(--red);
        }
        .button {
            background: var(--accent);
            color: #fff;
            border: none;
            padding: 8px 10px;
            border-radius: 6px;
            cursor: pointer;
            font-weight: 600;
        }
        .btn-ghost {
            background: transparent;
            border: 1px solid var(--border);
            padding: 7px 10px;
            border-radius: 6px;
            cursor: pointer;
        }
        .tiny {
            font-size: 12px;
            padding: 6px 8px;
        }
        .actions {
            display: flex;
            gap: 8px;
            margin-top: 12px;
        }
        .issue-body {
            margin-top: 12px;
            color: #24292f;
            line-height: 1.6;
        }
        input[type=text],
        textarea {
            width: 100%;
            padding: 8px;
            border-radius: 6px;
            border: 1px solid var(--border);
        }
        textarea {
            min-height: 160px;
            font-family: inherit;
            resize: vertical;
        }
        .comment {
            border-left: 4px solid #d0d7de;
            padding-left: 12px;
        }
        .comment {
            border-top: 1px solid #eef0f2;
            padding: 10px 0;
        }
        .comment .meta {
            font-size: 12px;
            color: var(--muted);
        }
        .small {
            font-size: 13px;
            color: var(--muted);
        }
        .top-row {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 8px;
        }
    </style>
</head>
<body>
    <div class="wrap">
        <div class="sidebar">
            <header>
                <h1>Local Issues</h1>
            </header>
            <div class="card">
                <input id="search" class="search" placeholder="Search title or body..." />
                <div style="display:flex; gap:8px; margin-top:8px;">
                    <select id="statusFilter" class="tiny btn-ghost">
                        <option value="all">All</option>
                        <option value="open">Open</option>
                        <option value="closed">Closed</option>
                    </select>
                    <select id="labelFilter" class="tiny btn-ghost">
                        <option value="">All labels</option>
                    </select>
                    <div style="flex:1"></div>
                    <div class="order-toggle small" id="orderToggle">Newest ⇅</div>
                </div>
            </div>
            <div class="card" style="padding:8px;">
                <div style="display:flex; gap:8px; align-items:center; justify-content:space-between;">
                    <div><b>New issue</b>
                        <div class="small">Create a new issue file</div>
                    </div>
                </div>
                <div style="margin-top:8px;">
                    <input id="newTitle" placeholder="Title (required)" />
                    <input id="newLabels" placeholder="Labels (comma separated)" />
                    <textarea id="newBody" placeholder="Markdown body"></textarea>
                    <div style="display:flex; gap:8px; margin-top:8px;">
                        <button class="button" id="createBtn">Create Issue</button>
                    </div>
                </div>
            </div>
            <div id="list" class="card" style="max-height:60vh; overflow:auto;"></div>
        </div>
        <div class="main">
            <div class="top-row">
                <div>
                    <div id="viewTitle" style="font-size:20px; font-weight:700;"></div>
                    <div id="viewMeta" class="small"></div>
                </div>
                <div id="viewActions" style="display:flex; gap:8px; align-items:center;"></div>
            </div>
            <div id="issueCard" class="card" style="margin-top:12px;">
                <div class="top-row">
                    <div>
                        <div id="viewTitle" style="font-size:20px; font-weight:700;"></div>
                        <div id="viewMeta" class="small"></div>
                        <div id="viewLabels" class="labels" style="margin-top:6px;"></div>
                    </div>
                    <div id="viewActions"></div>
                </div>
                <div id="viewRendered" class="issue-body" style="margin-top:12px;">
                    Select an issue to view it here.
                </div>
                <div id="editorArea" style="display:none; margin-top:12px;">
                    <div class="editor">
                        <input id="editTitle" placeholder="Title" />
                        <input id="editLabels" placeholder="Labels (comma)" />
                        <select id="editStatus">
                            <option>open</option>
                            <option>closed</option>
                        </select>
                        <textarea id="editBody"></textarea>
                        <div style="display:flex; gap:8px;">
                            <button id="saveBtn" class="button">Save</button>
                            <button id="cancelEdit" class="btn-ghost">Cancel</button>
                            <div style="flex:1"></div>
                            <div class="small">Edits will modify the issue file contents (and comments are preserved).
                            </div>
                        </div>
                    </div>
                </div>
            </div>
            <div id="commentsCard" class="card">
                <h3 style="margin-top:0">Comments</h3>
                <div id="commentsArea"></div>
                <div>
                    <input id="cAuthor" placeholder="Your name">
                    <textarea id="cBody" placeholder="Write a comment..." style="flex:1"></textarea>
                    <button id="postComment" class="button">Comment</button>
                </div>
            </div>
            <div id="saveHint" class="hint"></div>
        </div>
    </div>
    </div>
    <script>
        let issues = [];
        let currentFile = null;
        let orderNewest = true;
        /* ---------- helpers ---------- */
        function qs(id) { return document.getElementById(id); }
        function apiGet(url) {
            return fetch(url).then(r => {
                if (!r.ok) throw new Error(r.statusText);
                return r.json();
            });
        }
        function apiPost(url, data) {
            return fetch(url, {
                method: "POST",
                headers: {
                    "Content-Type": "application/x-www-form-urlencoded"
                },
                body: new URLSearchParams(data)
            }).then(r => {
                if (!r.ok) throw new Error(r.statusText);
                return r.headers.get("content-type")?.includes("json")
                    ? r.json()
                    : r.text();
            });
        }
        /* ---------- list ---------- */
        function loadList() {
            apiGet("/api/list").then(data => {
                issues = data;
                renderList();
                populateLabels();
            });
        }
        function renderList() {
            const list = qs("list");
            list.innerHTML = "";
            let filtered = [...issues];
            const q = qs("search").value.toLowerCase();
            const status = qs("statusFilter").value;
            const label = qs("labelFilter").value;
            if (q)
                filtered = filtered.filter(i =>
                    i.title.toLowerCase().includes(q)
                );
            if (status !== "all")
                filtered = filtered.filter(i => i.status === status);
            if (label)
                filtered = filtered.filter(i => i.labels.includes(label));
            filtered.sort((a, b) =>
                orderNewest ? b.created.localeCompare(a.created)
                    : a.created.localeCompare(b.created)
            );
            for (const i of filtered) {
                const div = document.createElement("div");
                div.className = "issue-row";
                div.innerHTML = `
            <div style="flex:1">
                <div class="issue-title">${i.title}</div>
                <div class="meta">
                    <span class="status ${i.status}">${i.status}</span>
                    ${i.labels.map(l => `<span class="label">${l}</span>`).join("")}
                </div>
            </div>
        `;
                div.onclick = () => loadIssue(i.file);
                list.appendChild(div);
            }
        }
        function saveEdit() {
            apiPost("/api/save", {
                file: currentFile,
                title: qs("editTitle").value,
                labels: qs("editLabels").value,
                status: qs("editStatus").value,
                body: qs("editBody").value
            }).then(() => {
                qs("editorArea").style.display = "none";
                loadList();
                loadIssue(currentFile);
            });
        }
        function populateLabels() {
            const sel = qs("labelFilter");
            const labels = new Set();
            issues.forEach(i => i.labels.forEach(l => labels.add(l)));
            sel.innerHTML = `<option value="">All labels</option>`;
            [...labels].sort().forEach(l => {
                const o = document.createElement("option");
                o.value = l;
                o.textContent = l;
                sel.appendChild(o);
            });
        }
        /* ---------- view ---------- */
        function loadIssue(file) {
            apiGet("/api/load?file=" + encodeURIComponent(file)).then(d => {
                currentFile = file;
                /* ---------- header ---------- */
                qs("viewTitle").textContent = d.meta.title || file;
                qs("viewMeta").innerHTML = `
    <span class="status ${d.meta.status}">
        ${d.meta.status}
    </span>
    · ${new Date(d.meta.created).toLocaleString("fr") || ""}
`;
                const labels = (d.meta.labels || "")
                    .split(",")
                    .map(l => l.trim())
                    .filter(Boolean);
                const lbl = qs("viewLabels");
                lbl.innerHTML = "";
                labels.forEach(l => {
                    const s = document.createElement("span");
                    s.className = "label";
                    s.textContent = l;
                    lbl.appendChild(s);
                });
                /* ---------- rendered view ---------- */
                qs("viewRendered").innerHTML = d.rendered_body;
                /* ---------- editor (raw markdown) ---------- */
                qs("editTitle").value = d.meta.title || "";
                qs("editLabels").value = d.meta.labels || "";
                qs("editStatus").value = d.meta.status || "open";
                qs("editBody").value = d.raw_body || "";
                /* ---------- comments ---------- */
                renderComments(d.rendered_comments);
                /* ---------- actions ---------- */
                qs("editorArea").style.display = "none";
                showActions();
            });
        }
        function renderComments(list) {
            const area = qs("commentsArea");
            area.innerHTML = "";
            if (!list || !list.length) return;
            list.forEach(c => {
                const initials = c.author
                    .split(" ")
                    .map(s => s[0]?.toUpperCase())
                    .join("")
                    .slice(0, 2);
                const div = document.createElement("div");
                div.className = "card comment";
                div.innerHTML = `
            <div style="display:flex; gap:12px;">
                <div style="
                    width:32px;height:32px;
                    border-radius:50%;
                    background:#d0d7de;
                    display:flex;
                    align-items:center;
                    justify-content:center;
                    font-weight:700;
                ">${initials}</div>
                <div style="flex:1">
                    <div class="meta" style="margin-bottom:6px">
                        <b>${c.author}</b> commented on
                        ${new Date(c.date).toLocaleString("fr")}
                    </div>
                    <div class="issue-body">
                        ${c.html}
                    </div>
                </div>
            </div>
        `;
                area.appendChild(div);
            });
        }
        /* ---------- actions ---------- */
        function showActions() {
            qs("viewActions").innerHTML = `
        <button class="btn-ghost tiny" onclick="startEdit()">Edit</button>
    `;
        }
        function startEdit() {
            qs("editorArea").style.display = "block";
        }
        qs("cancelEdit").onclick = () => {
            qs("editorArea").style.display = "none";
        };
        qs("saveBtn").onclick = saveEdit;
        /* ---------- comments ---------- */
        qs("postComment").onclick = () => {
            if (currentFile !== null) {
                apiPost("/api/comment", {
                    file: currentFile,
                    author: qs("cAuthor").value || "anon",
                    body: qs("cBody").value
                }).then(() => {
                    qs("cBody").value = "";
                    loadIssue(currentFile);
                });
            }
        };
        /* ---------- create ---------- */
        qs("createBtn").onclick = () => {
            apiPost("/api/create", {
                title: qs("newTitle").value,
                labels: qs("newLabels").value,
                body: qs("newBody").value
            }).then(() => {
                qs("newTitle").value = "";
                qs("newLabels").value = "";
                qs("newBody").value = "";
                loadList();
            });
        };
        /* ---------- filters ---------- */
        qs("search").oninput = renderList;
        qs("statusFilter").onchange = renderList;
        qs("labelFilter").onchange = renderList;
        qs("orderToggle").onclick = () => {
            orderNewest = !orderNewest;
            qs("orderToggle").textContent = orderNewest ? "Newest ⇅" : "Oldest ⇅";
            renderList();
        };
        /* ---------- init ---------- */
        loadList();
    </script>
</body>
</html>"""

ROOT = pathlib.Path(__file__).parent.resolve()
ISSUES = (ROOT / "issues").resolve()

ISSUES.mkdir(exist_ok=True)

MAX_TITLE_LEN = 200
MAX_LABELS_LEN = 200
MAX_BODY_LEN = 50_000
MAX_AUTHOR_LEN = 40
MAX_FILE_LEN = 255

LABEL_RE = re.compile(r"^[a-zA-Z0-9._\- ]+$")
AUTHOR_RE = re.compile(r"^[a-zA-Z0-9 _\-]{1,40}$")
STATUS_VALUES = {"open", "closed"}


def require(data: dict, key: str) -> str:
    if key not in data or not data[key].strip():
        raise ValueError(f"Missing field: {key}")
    return data[key].strip()


def limit(value: str, max_len: int, name: str) -> str:
    if len(value) > max_len:
        raise ValueError(f"{name} exceeds maximum length")
    return value


def validate_labels(raw: str) -> str:
    labels = []
    for l in raw.split(","):
        l = l.strip()
        if not l:
            continue
        if not LABEL_RE.match(l):
            raise ValueError(f"Invalid label: {l}")
        labels.append(l)
    return ", ".join(labels)


def validate_author(author: str) -> str:
    author = author.strip()
    if not AUTHOR_RE.match(author):
        raise ValueError("Invalid author name")
    return author


def validate_status(status: str) -> str:
    if status not in STATUS_VALUES:
        raise ValueError("Invalid status")
    return status


class IssueParsed(typing.TypedDict):
    meta: dict[str, str]
    body: str
    comments: str


def parse_comments(raw: str) -> list[dict[str, str]]:
    comments: list[dict[str, str]] = []
    if not raw.strip():
        return comments

    lines: list[str] = raw.splitlines()
    current: dict[str, str] | None = None
    body_lines: list[str] = []

    def flush():
        if current and body_lines:
            text = "\n".join(body_lines).strip()
            current["raw"] = text
            current["html"] = render_markdown(text)
            comments.append(current.copy())

    for line in lines:
        if line.startswith("author:"):
            flush()
            current = {
                "author": html_escape.escape(line[7:].strip(), quote=True),
                "date": "",
                "raw": "",
                "html": "",
            }
            body_lines = []
        elif line.startswith("date:") and current:
            current["date"] = html_escape.escape(line[5:].strip(), quote=True)
        elif line.startswith("body:") and current:
            body_lines.append(line[5:].lstrip())
        elif current:
            body_lines.append(line)

    flush()
    return comments


def render_markdown(text: str) -> str:
    if not text.strip():
        return ""

    esc: str = html_escape.escape(text, quote=True)

    esc = re.sub(r"^### (.+)$", r"<h3>\1</h3>", esc, flags=re.M)
    esc = re.sub(r"^## (.+)$", r"<h2>\1</h2>", esc, flags=re.M)
    esc = re.sub(r"^# (.+)$", r"<h1>\1</h1>", esc, flags=re.M)

    esc = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", esc)
    esc = re.sub(r"\*(.+?)\*", r"<i>\1</i>", esc)
    esc = re.sub(r"`(.+?)`", r"<code>\1</code>", esc)

    paragraphs = []
    for p in esc.split("\n\n"):
        p = p.strip()
        if not p:
            continue
        paragraphs.append(f"<p>{p}</p>")

    return "\n".join(paragraphs)


def safe_issue_path(name: str) -> pathlib.Path:
    p: pathlib.Path = (ISSUES / name).resolve()
    if not str(p).startswith(str(ISSUES)):
        raise ValueError("Forbidden path")
    return p


def parse_issue(text: str) -> IssueParsed:
    parts: list[str] = text.split("---", 2)
    meta: dict[str, str] = {}
    body: str = ""
    comments: str = ""

    if len(parts) >= 3:
        header: list[str] = parts[1].strip().splitlines()
        rest: str = parts[2]

        for line in header:
            if ":" in line:
                k, v = line.split(":", 1)
                meta[k.strip()] = v.strip()

        if "***" in rest:
            body, comments = rest.split("***", 1)
            body = body.strip()
            comments = comments.strip()
        else:
            body = rest.strip()

    return {"meta": meta, "body": body, "comments": comments}


def serialize_issue(meta: dict[str, str], body: str, comments: str) -> str:
    out: list[str] = ["---"]
    for k, v in meta.items():
        out.append(f"{k}: {v}")
    out.append("---\n")
    out.append(body.strip() + "\n\n")
    if comments.strip():
        out.append("***\n")
        out.append(comments.strip())
    return "\n".join(out)


class IssueHandler(http.server.BaseHTTPRequestHandler):
    # ---------------- GET ----------------

    def do_GET(self):
        parsed: urllib.parse.ParseResult | urllib.parse.ParseResultBytes = (
            urllib.parse.urlparse(self.path)
        )

        if parsed.path == "/":
            self.serve_index()
        elif parsed.path == "/api/list":
            self.api_list()
        elif parsed.path == "/api/load":
            self.api_load(parsed)
        else:
            self.send_error(404)

    # ---------------- POST ----------------

    def do_POST(self):
        parsed: urllib.parse.ParseResult | urllib.parse.ParseResultBytes = (
            urllib.parse.urlparse(self.path)
        )

        if parsed.path == "/api/save":
            self.api_save()
        elif parsed.path == "/api/comment":
            self.api_comment()
        elif parsed.path == "/api/create":
            self.api_create()
        else:
            self.send_error(404)

    # ---------------- UI ----------------

    def serve_index(self):
        data: bytes = bytes(INDEX_DATA, "utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.end_headers()
        self.wfile.write(data)

    # ---------------- API ----------------

    def api_list(self):
        issues: list[dict] = []

        for p in sorted(ISSUES.glob("*.md")):
            parsed: IssueParsed = parse_issue(p.read_text(encoding="utf-8"))
            meta: dict[str, str] = parsed["meta"]

            issues.append(
                {
                    "file": p.name,
                    "title": meta.get("title", p.name),
                    "status": meta.get("status", "open"),
                    "labels": [
                        x.strip()
                        for x in meta.get("labels", "").split(",")
                        if x.strip()
                    ],
                    "created": meta.get("created", ""),
                }
            )

        self.json(issues)

    def api_load(self, parsed):
        qs: dict[str, list[str]] = urllib.parse.parse_qs(parsed.query)
        name: str = qs.get("file", [""])[0]

        try:
            p: pathlib.Path = safe_issue_path(name)
            parsed_issue: IssueParsed = parse_issue(p.read_text(encoding="utf-8"))

            rendered_body: str = render_markdown(parsed_issue["body"])
            rendered_comments: list[dict[str, str]] = parse_comments(
                parsed_issue["comments"]
            )

            self.json(
                {
                    "meta": parsed_issue["meta"],
                    "rendered_body": rendered_body,
                    "rendered_comments": rendered_comments,
                    "raw_body": parsed_issue["body"],
                }
            )
        except Exception as e:
            self.send_error(400, str(e))

    def api_save(self):
        data: dict[str, str] = self.read_form()

        file = limit(require(data, "file"), MAX_FILE_LEN, "file")
        title = limit(require(data, "title"), MAX_TITLE_LEN, "title")
        status = validate_status(require(data, "status"))
        labels = validate_labels(data.get("labels", ""))
        body = limit(data.get("body", ""), MAX_BODY_LEN, "body")

        p = safe_issue_path(file)
        parsed = parse_issue(p.read_text(encoding="utf-8"))

        meta = parsed["meta"]
        meta.update({"title": title, "status": status, "labels": labels})

        p.write_text(serialize_issue(meta, body, parsed["comments"]), encoding="utf-8")

        self.json({"ok": True})

    def api_comment(self):
        data: dict[str, str] = self.read_form()

        file = limit(require(data, "file"), MAX_FILE_LEN, "file")
        author = validate_author(data.get("author", "anon"))
        body = limit(require(data, "body"), MAX_BODY_LEN, "comment body")

        p = safe_issue_path(file)
        parsed = parse_issue(p.read_text(encoding="utf-8"))

        stamp = datetime.datetime.now(datetime.timezone.utc).isoformat()

        comment_block = f"\n\nauthor: {author}\ndate: {stamp}\nbody: {body}"

        comments = parsed["comments"].rstrip() + comment_block

        p.write_text(
            serialize_issue(parsed["meta"], parsed["body"], comments), encoding="utf-8"
        )

        self.json({"ok": True})

    def api_create(self):
        data: dict[str, str] = self.read_form()

        title = limit(require(data, "title"), MAX_TITLE_LEN, "title")
        labels = validate_labels(data.get("labels", ""))
        body = limit(data.get("body", ""), MAX_BODY_LEN, "body")

        ts = datetime.datetime.now(datetime.timezone.utc).isoformat()
        idx = len(list(ISSUES.glob("*.md"))) + 1
        safe_title = re.sub(r"[^a-z0-9\-]", "-", title.lower())
        name = f"{idx:04d}-{safe_title}.md"

        meta = {
            "title": title,
            "status": "open",
            "labels": labels,
            "created": ts,
        }

        p = safe_issue_path(name)
        p.write_text(serialize_issue(meta, body, ""), encoding="utf-8")

        self.json({"file": name})

    # ---------------- helpers ----------------

    def read_form(self) -> dict[str, str]:
        size = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(size).decode("utf-8")
        data = urllib.parse.parse_qs(raw, keep_blank_values=True)
        return {k: v[0] for k, v in data.items()}

    def json(self, obj: typing.Any):
        out: bytes = json.dumps(obj).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(out)


def print_ip(PORT: int):
    import socket

    hostname = socket.gethostname()
    ip = socket.gethostbyname(hostname)

    print("Access URLs:")
    print(f"  http://{ip}:{PORT}")
    print(f"  http://{hostname}.local:{PORT}  (if mDNS available)")


if __name__ == "__main__":
    IP: str = "127.0.0.1"  # localhost
    PORT: int = 8080
    # print_ip(PORT)
    print(f"Local Issues running at http://localhost:{PORT}")
    server = http.server.HTTPServer((IP, PORT), IssueHandler)
    if os.name == "nt":
        os.system(f"start http://localhost:{PORT}")
        # print(os.name)

    server.serve_forever()
