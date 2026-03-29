const PORT = 8080;
const ISSUES_DIR = "./issues";

// Ensure the issues directory exists
try {
    Deno.mkdirSync(ISSUES_DIR, { recursive: true });
} catch (e) {
    // Ignore if it already exists
}

// --- Validation Constants ---
const MAX_TITLE_LEN = 200;
const MAX_BODY_LEN = 50_000;
const MAX_FILE_LEN = 255;
const LABEL_RE = /^[a-zA-Z0-9._\- ]+$/;
const AUTHOR_RE = /^[a-zA-Z0-9 _\-]{1,40}$/;
const STATUS_VALUES = new Set(["open", "closed"]);

// --- Helper Functions ---
function requireField(data: FormData, key: string): string {
    const val = data.get(key)?.toString().trim();
    if (!val) throw new Error(`Missing field: ${key}`);
    return val;
}

function limit(value: string, maxLen: number, name: string): string {
    if (value.length > maxLen) throw new Error(`${name} exceeds maximum length`);
    return value;
}

function validateLabels(raw: string): string {
    const labels = raw.split(",")
        .map(l => l.trim())
        .filter(l => l.length > 0);

    for (const l of labels) {
        if (!LABEL_RE.test(l)) throw new Error(`Invalid label: ${l}`);
    }
    return labels.join(", ");
}

function validateAuthor(author: string): string {
    const clean = author.trim();
    if (!AUTHOR_RE.test(clean)) throw new Error("Invalid author name");
    return clean;
}

function validateStatus(status: string): string {
    if (!STATUS_VALUES.has(status)) throw new Error("Invalid status");
    return status;
}

function safeIssuePath(filename: string): string {
    // Prevent directory traversal by only taking the base name
    const cleanName = filename.replace(/^.*[\\\/]/, '');
    if (cleanName.length > MAX_FILE_LEN) throw new Error("File name too long");
    return `${ISSUES_DIR}/${cleanName}`;
}

function escapeHtml(unsafe: string): string {
    return unsafe
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

function renderMarkdown(text: string): string {
    if (!text.trim()) return "";
    let esc = escapeHtml(text);

    // 1. Link to other issues (#0001, #0002, etc.)
    esc = esc.replace(/#(\d{4})/g, (match, id) => {
        try {
            // Look for a file in the issues directory starting with this ID
            for (const entry of Deno.readDirSync(ISSUES_DIR)) {
                if (entry.name.startsWith(id + "-") && entry.name.endsWith(".md")) {
                    // Return a link that calls the frontend 'loadIssue' function
                    return `<a href="#" onclick="loadIssue('${entry.name}'); return false;" style="color:var(--accent); text-decoration:none; font-weight:bold;">#${id}</a>`;
                }
            }
        } catch (e) {
            // Directory might not exist yet or permission error
        }
        return match; // Return original text if no match found
    });

    // 2. Standard Markdown (Headings)
    esc = esc.replace(/^### (.+)$/gm, "<h3>$1</h3>");
    esc = esc.replace(/^## (.+)$/gm, "<h2>$1</h2>");
    esc = esc.replace(/^# (.+)$/gm, "<h1>$1</h1>");

    // 3. Inline Formatting
    esc = esc.replace(/\*\*(.+?)\*\*/g, "<b>$1</b>");
    esc = esc.replace(/\*(.+?)\*/g, "<i>$1</i>");
    esc = esc.replace(/`(.+?)`/g, "<code>$1</code>");

    // 4. Paragraphs
    const paragraphs = esc.split("\n\n")
        .map(p => p.trim())
        .filter(p => p.length > 0)
        .map(p => `<p>${p}</p>`);

    return paragraphs.join("\n");
}

// --- Parsing ---
interface ParsedIssue {
    meta: Record<string, string>;
    body: string;
    comments: string;
}

function parseIssue(text: string): ParsedIssue {
    const parts = text.split("---", 3);
    const meta: Record<string, string> = {};
    let body = "";
    let comments = "";

    if (parts.length >= 3) {
        const header = parts[1].trim().split("\n");
        const rest = parts[2];

        for (const line of header) {
            const colonIdx = line.indexOf(":");
            if (colonIdx !== -1) {
                const k = line.substring(0, colonIdx).trim();
                const v = line.substring(colonIdx + 1).trim();
                meta[k] = v;
            }
        }

        const splitIdx = rest.indexOf("***");
        if (splitIdx !== -1) {
            body = rest.substring(0, splitIdx).trim();
            comments = rest.substring(splitIdx + 3).trim();
        } else {
            body = rest.trim();
        }
    }

    return { meta, body, comments };
}

function serializeIssue(meta: Record<string, string>, body: string, comments: string): string {
    const out = ["---"];
    for (const [k, v] of Object.entries(meta)) {
        out.push(`${k}: ${v}`);
    }
    out.push("---");
    out.push(body.trim());
    out.push("");
    if (comments.trim()) {
        out.push("***");
        out.push(comments.trim());
    }
    return out.join("\n") + "\n";
}

function parseComments(raw: string) {
    const comments = [];
    if (!raw.trim()) return comments;

    const lines = raw.split("\n");
    let currentAuthor: string | null = null;
    let currentDate = "";
    let bodyLines: string[] = [];

    const flush = () => {
        if (currentAuthor && bodyLines.length > 0) {
            const text = bodyLines.join("\n").trim();
            comments.push({
                author: escapeHtml(currentAuthor),
                date: escapeHtml(currentDate),
                raw: text,
                html: renderMarkdown(text)
            });
        }
    };

    for (const line of lines) {
        if (line.startsWith("author:")) {
            flush();
            currentAuthor = line.substring(7).trim();
            currentDate = "";
            bodyLines = [];
        } else if (line.startsWith("date:") && currentAuthor) {
            currentDate = line.substring(5).trim();
        } else if (line.startsWith("body:") && currentAuthor) {
            bodyLines.push(line.substring(5).trimStart());
        } else if (currentAuthor) {
            bodyLines.push(line);
        }
    }
    flush();
    return comments;
}

// --- API Handlers ---

async function apiList() {
    const issues = [];
    for await (const dirEntry of Deno.readDir(ISSUES_DIR)) {
        if (dirEntry.isFile && dirEntry.name.endsWith(".md")) {
            const text = await Deno.readTextFile(`${ISSUES_DIR}/${dirEntry.name}`);
            const parsed = parseIssue(text);
            issues.push({
                file: dirEntry.name,
                title: parsed.meta.title || dirEntry.name,
                status: parsed.meta.status || "open",
                labels: (parsed.meta.labels || "").split(",").map(x => x.trim()).filter(Boolean),
                created: parsed.meta.created || ""
            });
        }
    }
    return Response.json(issues);
}

async function apiLoad(url: URL) {
    const file = url.searchParams.get("file") || "";
    try {
        const path = safeIssuePath(file);
        const text = await Deno.readTextFile(path);
        const parsed = parseIssue(text);
        return Response.json({
            meta: parsed.meta,
            rendered_body: renderMarkdown(parsed.body),
            rendered_comments: parseComments(parsed.comments),
            raw_body: parsed.body
        });
    } catch (e: any) {
        return new Response(JSON.stringify({ error: e.message }), { status: 400 });
    }
}

async function apiSave(req: Request) {
    const data = await req.formData();
    try {
        const file = limit(requireField(data, "file"), MAX_FILE_LEN, "file");
        const title = limit(requireField(data, "title"), MAX_TITLE_LEN, "title");
        const status = validateStatus(requireField(data, "status"));
        const labels = validateLabels(data.get("labels")?.toString() || "");
        const body = limit(data.get("body")?.toString() || "", MAX_BODY_LEN, "body");

        const path = safeIssuePath(file);
        const text = await Deno.readTextFile(path);
        const parsed = parseIssue(text);

        parsed.meta.title = title;
        parsed.meta.status = status;
        parsed.meta.labels = labels;

        await Deno.writeTextFile(path, serializeIssue(parsed.meta, body, parsed.comments));
        return Response.json({ ok: true });
    } catch (e: any) {
        return new Response(JSON.stringify({ error: e.message }), { status: 400 });
    }
}

async function apiComment(req: Request) {
    const data = await req.formData();
    try {
        const file = limit(requireField(data, "file"), MAX_FILE_LEN, "file");
        const rawAuthor = data.get("author")?.toString() || "anon";
        const author = validateAuthor(rawAuthor.trim() ? rawAuthor : "anon");
        const body = limit(requireField(data, "body"), MAX_BODY_LEN, "comment body");

        const path = safeIssuePath(file);
        const text = await Deno.readTextFile(path);
        const parsed = parseIssue(text);

        const stamp = new Date().toISOString();
        const commentBlock = `\n\nauthor: ${author}\ndate: ${stamp}\nbody: ${body}`;
        const newComments = parsed.comments.trimEnd() + commentBlock;

        await Deno.writeTextFile(path, serializeIssue(parsed.meta, parsed.body, newComments));
        return Response.json({ ok: true });
    } catch (e: any) {
        return new Response(JSON.stringify({ error: e.message }), { status: 400 });
    }
}

async function apiCreate(req: Request) {
    const data = await req.formData();
    try {
        const title = limit(requireField(data, "title"), MAX_TITLE_LEN, "title");
        const labels = validateLabels(data.get("labels")?.toString() || "");
        const body = limit(data.get("body")?.toString() || "", MAX_BODY_LEN, "body");

        const ts = new Date().toISOString();

        // Count files for ID
        let count = 0;
        for await (const _ of Deno.readDir(ISSUES_DIR)) count++;
        const idx = count + 1;

        const safeTitle = title.toLowerCase();
        const padded_idx = idx.toString().padStart(4, '0');
        const name = `${padded_idx}-${safeTitle}.md`;
        
        const title_with_issue_idx = `${title} (${padded_idx})`;
        const meta = {
            "title": title_with_issue_idx,
            status: "open",
            "labels": labels,
            created: ts
        };

        const path = safeIssuePath(name);
        await Deno.writeTextFile(path, serializeIssue(meta, body, ""));
        return Response.json({ file: name });
    } catch (e: any) {
        return new Response(JSON.stringify({ error: e.message }), { status: 400 });
    }
}

// --- Main Server ---
Deno.serve({ port: PORT }, async (req: Request) => {
    const url = new URL(req.url);

    if (req.method === "GET" && url.pathname === "/") {
        return new Response(INDEX_DATA, {
            headers: { "Content-Type": "text/html; charset=utf-8" },
        });
    }

    if (req.method === "GET" && url.pathname === "/api/list") return await apiList();
    if (req.method === "GET" && url.pathname === "/api/load") return await apiLoad(url);

    if (req.method === "POST" && url.pathname === "/api/save") return await apiSave(req);
    if (req.method === "POST" && url.pathname === "/api/comment") return await apiComment(req);
    if (req.method === "POST" && url.pathname === "/api/create") return await apiCreate(req);

    return new Response("Not Found", { status: 404 });
});

// --- Auto-open browser ---
console.log(`Local Issues running at http://localhost:${PORT}`);
const platform = Deno.build.os;

let cmd: string;
let cmdArgs: string[];

if (platform === "windows") {
    // On Windows, 'start' is a cmd.exe built-in, not an executable
    cmd = "cmd";
    cmdArgs = ["/c", "start", `http://localhost:${PORT}`];
} else if (platform === "darwin") {
    cmd = "open";
    cmdArgs = [`http://localhost:${PORT}`];
} else {
    cmd = "xdg-open";
    cmdArgs = [`http://localhost:${PORT}`];
}
new Deno.Command(cmd, { args: cmdArgs }).spawn();


// --- HTML Payload ---
const INDEX_DATA = `<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>Local Issues - Viewer & Editor</title>
    <style>
        #commentsCard { margin-top: 12px; }
        #commentsArea .comment:first-child { border-top: none; }
        :root { --bg: #f6f8fa; --card: #fff; --muted: #6a737d; --border: #d0d7de; --accent: #0969da; --green: #2da44e; --red: #cf222e; }
        body { background: var(--bg); font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial; color: #24292f; margin: 0; }
        .wrap { display: flex; gap: 24px; max-width: 1200px; margin: 24px auto; padding: 0 16px; }
        .sidebar { width: 340px; }
        .main { flex: 1; min-width: 0; }
        header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
        h1 { font-size: 20px; margin: 0; }
        .controls { display: flex; gap: 8px; align-items: center; }
        .search { width: 100%; padding: 8px 10px; border-radius: 6px; border: 1px solid var(--border); }
        .card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 12px; margin-bottom: 12px; box-shadow: 0 1px 0 rgba(27, 31, 35, 0.04); }
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
        .actions { display: flex; gap: 8px; margin-top: 12px; }
        .issue-body { margin-top: 12px; color: #24292f; line-height: 1.6; }
        input[type=text], textarea { width: 100%; padding: 8px; border-radius: 6px; border: 1px solid var(--border); }
        textarea { min-height: 160px; font-family: inherit; resize: vertical; }
        .comment { border-left: 4px solid #d0d7de; padding-left: 12px; }
        .comment { border-top: 1px solid #eef0f2; padding: 10px 0; }
        .comment .meta { font-size: 12px; color: var(--muted); }
        .small { font-size: 13px; color: var(--muted); }
        .top-row { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
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
                    <div class="order-toggle small" id="orderToggle" style="cursor:pointer">Newest ⇅</div>
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
                        <div id="viewLabels" class="labels" style="margin-top:6px;"></div>
                    </div>
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
                        <div style="display:flex; gap:8px; margin-top:8px;">
                            <button id="saveBtn" class="button">Save</button>
                            <button id="cancelEdit" class="btn-ghost">Cancel</button>
                            <div style="flex:1"></div>
                            <div class="small">Edits modify the file (comments preserved).</div>
                        </div>
                    </div>
                </div>
            </div>
            <div id="commentsCard" class="card" style="display:none;">
                <h3 style="margin-top:0">Comments</h3>
                <div id="commentsArea"></div>
                <div style="margin-top: 12px; display:flex; flex-direction: column; gap: 8px;">
                    <input id="cAuthor" placeholder="Your name">
                    <textarea id="cBody" placeholder="Write a comment..."></textarea>
                    <button id="postComment" class="button" style="align-self: flex-start;">Comment</button>
                </div>
            </div>
        </div>
    </div>
    <script>
        let issues = [];
        let currentFile = null;
        let orderNewest = true;
        
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
                headers: { "Content-Type": "application/x-www-form-urlencoded" },
                body: new URLSearchParams(data)
            }).then(r => {
                if (!r.ok) throw new Error(r.statusText);
                return r.headers.get("content-type")?.includes("json") ? r.json() : r.text();
            });
        }
        
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
            
            if (q) filtered = filtered.filter(i => i.title.toLowerCase().includes(q));
            if (status !== "all") filtered = filtered.filter(i => i.status === status);
            if (label) filtered = filtered.filter(i => i.labels.includes(label));
            
            filtered.sort((a, b) => orderNewest ? b.created.localeCompare(a.created) : a.created.localeCompare(b.created));
            
            for (const i of filtered) {
                const div = document.createElement("div");
                div.className = "issue-row";
                div.innerHTML = \`
                    <div style="flex:1">
                        <div class="issue-title">\${i.title}</div>
                        <div class="meta">
                            <span class="status \${i.status}">\${i.status}</span>
                            \${i.labels.map(l => \`<span class="label">\${l}</span>\`).join("")}
                        </div>
                    </div>
                \`;
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
            sel.innerHTML = \`<option value="">All labels</option>\`;
            [...labels].sort().forEach(l => {
                const o = document.createElement("option");
                o.value = l; o.textContent = l;
                sel.appendChild(o);
            });
        }
        
        function loadIssue(file) {
            apiGet("/api/load?file=" + encodeURIComponent(file)).then(d => {
                currentFile = file;
                qs("viewTitle").textContent = d.meta.title || file;
                qs("viewMeta").innerHTML = \`<span class="status \${d.meta.status}">\${d.meta.status}</span> · \${new Date(d.meta.created).toLocaleString("fr") || ""}\`;
                
                const labels = (d.meta.labels || "").split(",").map(l => l.trim()).filter(Boolean);
                const lbl = qs("viewLabels");
                lbl.innerHTML = "";
                labels.forEach(l => {
                    const s = document.createElement("span");
                    s.className = "label"; s.textContent = l;
                    lbl.appendChild(s);
                });
                
                qs("viewRendered").innerHTML = d.rendered_body;
                qs("editTitle").value = d.meta.title || "";
                qs("editLabels").value = d.meta.labels || "";
                qs("editStatus").value = d.meta.status || "open";
                qs("editBody").value = d.raw_body || "";
                
                qs("commentsCard").style.display = "block";
                renderComments(d.rendered_comments);
                
                qs("editorArea").style.display = "none";
                qs("viewActions").innerHTML = \`<button class="btn-ghost tiny" onclick="startEdit()">Edit</button>\`;
            });
        }
        
        function renderComments(list) {
            const area = qs("commentsArea");
            area.innerHTML = "";
            if (!list || !list.length) return;
            list.forEach(c => {
                const initials = c.author.split(" ").map(s => s[0]?.toUpperCase()).join("").slice(0, 2);
                const div = document.createElement("div");
                div.className = "card comment";
                div.innerHTML = \`
                    <div style="display:flex; gap:12px;">
                        <div style="width:32px;height:32px;border-radius:50%;background:#d0d7de;display:flex;align-items:center;justify-content:center;font-weight:700;">\${initials}</div>
                        <div style="flex:1">
                            <div class="meta" style="margin-bottom:6px"><b>\${c.author}</b> commented on \${new Date(c.date).toLocaleString("fr")}</div>
                            <div class="issue-body">\${c.html}</div>
                        </div>
                    </div>
                \`;
                area.appendChild(div);
            });
        }
        
        function startEdit() { qs("editorArea").style.display = "block"; }
        qs("cancelEdit").onclick = () => qs("editorArea").style.display = "none";
        qs("saveBtn").onclick = saveEdit;
        
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
        
        qs("search").oninput = renderList;
        qs("statusFilter").onchange = renderList;
        qs("labelFilter").onchange = renderList;
        qs("orderToggle").onclick = () => {
            orderNewest = !orderNewest;
            qs("orderToggle").textContent = orderNewest ? "Newest ⇅" : "Oldest ⇅";
            renderList();
        };
        
        loadList();
    </script>
</body>
</html>`;

// deno run --allow-net --allow-read --allow-write --allow-run .\issue.ts
