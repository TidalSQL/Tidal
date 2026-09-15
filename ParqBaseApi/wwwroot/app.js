"use strict";

const $ = (id) => document.getElementById(id);

const editor = $("editor");
const gutter = $("gutter");
const runBtn = $("runBtn");
const cancelBtn = $("cancelBtn");
const dbSelect = $("dbSelect");
const statusEl = $("status");
const treeEl = $("tree");
const gridWrap = $("gridWrap");
const messagesEl = $("messages");
const tabbar = $("tabbar");
const queryView = $("queryView");
const tableView = $("tableView");

// ---- tabs ----------------------------------------------------------------------
// The query editor is a permanent tab; each opened table adds its own tab.
const QUERY_TAB = { id: "__query__", kind: "query", title: "SQL query 1" };
let tabs = [QUERY_TAB];
let activeTabId = QUERY_TAB.id;

function renderTabs() {
    tabbar.innerHTML = "";
    for (const t of tabs) {
        const el = document.createElement("div");
        el.className = "tab" + (t.id === activeTabId ? " active" : "");
        const icon = document.createElement("span");
        icon.className = "tab-icon";
        icon.textContent = t.kind === "query" ? "🖹" : "▦";
        const title = document.createElement("span");
        title.textContent = t.title;
        el.appendChild(icon);
        el.appendChild(title);
        el.addEventListener("click", () => activateTab(t.id));
        if (t.kind !== "query") {
            const close = document.createElement("span");
            close.className = "tab-close";
            close.textContent = "✕";
            close.title = "Close";
            close.addEventListener("click", (e) => { e.stopPropagation(); closeTab(t.id); });
            el.appendChild(close);
        }
        tabbar.appendChild(el);
    }
}

function activateTab(id) {
    activeTabId = id;
    const tab = tabs.find((t) => t.id === id);
    const isQuery = tab.kind === "query";
    queryView.classList.toggle("hidden", !isQuery);
    tableView.classList.toggle("hidden", isQuery);
    if (!isQuery) renderTableView(tab);
    renderTabs();
}

function closeTab(id) {
    const idx = tabs.findIndex((t) => t.id === id);
    tabs = tabs.filter((t) => t.id !== id);
    if (activeTabId === id) activateTab((tabs[idx - 1] || tabs[0]).id);
    else renderTabs();
}

function openTableTab(db, table) {
    const id = `tbl:${db}.${table}`;
    if (!tabs.some((t) => t.id === id)) {
        tabs.push({ id, kind: "table", title: table, db, table, info: null, mode: "overview" });
    }
    activateTab(id);
}

// ---- editor line-number gutter -------------------------------------------------
function syncGutter() {
    const lines = editor.value.split("\n").length || 1;
    let out = "";
    for (let i = 1; i <= lines; i++) out += i + "\n";
    gutter.textContent = out;
    gutter.scrollTop = editor.scrollTop;
}
editor.addEventListener("input", syncGutter);
editor.addEventListener("scroll", () => { gutter.scrollTop = editor.scrollTop; });
editor.addEventListener("keydown", (e) => {
    if (e.key === "Tab") {
        e.preventDefault();
        const s = editor.selectionStart, en = editor.selectionEnd;
        editor.value = editor.value.slice(0, s) + "    " + editor.value.slice(en);
        editor.selectionStart = editor.selectionEnd = s + 4;
        syncGutter();
    } else if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        runQuery();
    }
});

// ---- result tab switching (query view) -----------------------------------------
function showTab(which) {
    const results = which === "results";
    $("tabResults").classList.toggle("active", results);
    $("tabMessages").classList.toggle("active", !results);
    $("resultsPane").classList.toggle("active", results);
    $("messagesPane").classList.toggle("active", !results);
}
$("tabResults").addEventListener("click", () => showTab("results"));
$("tabMessages").addEventListener("click", () => showTab("messages"));

// ---- explorer tree -------------------------------------------------------------
// Databases the user has expanded in the tree. Tracked so the explorer can rebuild
// itself (e.g. after DDL) without losing which nodes were open.
const expandedDbs = new Set();

async function loadDatabases() {
    treeEl.innerHTML = "";
    const previousSelection = dbSelect.value;
    dbSelect.innerHTML = '<option value="">(none)</option>';
    let dbs = [];
    try {
        dbs = await fetchJson("/api/databases");
    } catch (err) {
        treeEl.innerHTML = `<li class="empty">Failed to load: ${escapeHtml(err.message)}</li>`;
        return;
    }
    for (const name of dbs) {
        treeEl.appendChild(makeDatabaseNode(name));
        const opt = document.createElement("option");
        opt.value = name;
        opt.textContent = name;
        dbSelect.appendChild(opt);
    }
    if (dbs.length === 0) treeEl.innerHTML = '<li class="empty">No databases.</li>';

    // Forget any expanded databases that no longer exist, and keep the selected database
    // in the dropdown if it is still available.
    for (const n of [...expandedDbs]) if (!dbs.includes(n)) expandedDbs.delete(n);
    if (dbs.includes(previousSelection)) dbSelect.value = previousSelection;
}

// Rebuilds the explorer while preserving expanded nodes and the selected database. Called
// after statements that change the catalog so newly created/dropped objects show up at once.
async function refreshExplorer() {
    await loadDatabases();
}

function makeDatabaseNode(name) {
    const li = document.createElement("li");
    const node = rowNode("▶", "🗄", name);
    const children = document.createElement("ul");
    children.className = "children collapsed";
    let loaded = false;

    async function expand() {
        children.classList.remove("collapsed");
        node.querySelector(".twisty").textContent = "▼";
        expandedDbs.add(name);
        loaded = true;
        children.innerHTML = "";
        children.appendChild(loadingNode());
        await loadObjects(name, children);
    }

    function collapse() {
        children.classList.add("collapsed");
        node.querySelector(".twisty").textContent = "▶";
        expandedDbs.delete(name);
    }

    node.addEventListener("click", async () => {
        dbSelect.value = name;
        if (children.classList.contains("collapsed")) await expand();
        else collapse();
    });

    li.appendChild(node);
    li.appendChild(children);

    // Re-expand automatically if this database was open before a refresh.
    if (expandedDbs.has(name)) { expand(); }

    return li;
}

// Builds a collapsible "folder" node (e.g. Tables, Stored Procedures) and returns the <ul>
// its child items should be appended to.
function objectFolder(container, label) {
    const li = document.createElement("li");
    const node = rowNode("▼", "📁", label);
    const ul = document.createElement("ul");
    ul.className = "children";
    node.addEventListener("click", () => {
        const collapsed = ul.classList.toggle("collapsed");
        node.querySelector(".twisty").textContent = collapsed ? "▶" : "▼";
    });
    li.appendChild(node);
    li.appendChild(ul);
    container.appendChild(li);
    return ul;
}

async function loadObjects(db, container) {
    container.innerHTML = "";
    await loadTables(db, container);
    await loadProcedures(db, container);
}

async function loadTables(db, container) {
    const tablesUl = objectFolder(container, "Tables");

    let tables = [];
    try {
        tables = await fetchJson(`/api/databases/${encodeURIComponent(db)}/tables`);
    } catch (err) {
        tablesUl.innerHTML = `<li class="empty">${escapeHtml(err.message)}</li>`;
        return;
    }
    if (tables.length === 0) {
        tablesUl.innerHTML = '<li class="empty">No tables.</li>';
        return;
    }
    for (const t of tables) {
        const li = document.createElement("li");
        const tn = rowNode("", "▦", t);
        tn.addEventListener("click", () => { dbSelect.value = db; openTableTab(db, t); });
        li.appendChild(tn);
        tablesUl.appendChild(li);
    }
}

async function loadProcedures(db, container) {
    const procsUl = objectFolder(container, "Stored Procedures");

    let procedures = [];
    try {
        procedures = await fetchJson(`/api/databases/${encodeURIComponent(db)}/procedures`);
    } catch (err) {
        procsUl.innerHTML = `<li class="empty">${escapeHtml(err.message)}</li>`;
        return;
    }
    if (procedures.length === 0) {
        procsUl.innerHTML = '<li class="empty">No stored procedures.</li>';
        return;
    }
    for (const p of procedures) {
        const li = document.createElement("li");
        const pn = rowNode("", "⚙", p);
        pn.addEventListener("click", async () => {
            dbSelect.value = db;
            activateTab(QUERY_TAB.id);
            editor.value = `-- Loading definition of ${p}…`;
            syncGutter();
            try {
                const res = await fetchJson(
                    `/api/databases/${encodeURIComponent(db)}/procedures/${encodeURIComponent(p)}`);
                editor.value = res.definition;
            } catch (err) {
                editor.value = `-- Failed to load definition of ${p}: ${err.message}`;
            }
            syncGutter();
            editor.focus();
        });
        li.appendChild(pn);
        procsUl.appendChild(li);
    }
}

function rowNode(twisty, ico, label) {
    const node = document.createElement("div");
    node.className = "node";
    node.innerHTML =
        `<span class="twisty">${twisty}</span><span class="ico">${ico}</span><span class="label"></span>`;
    node.querySelector(".label").textContent = label;
    return node;
}

function loadingNode() {
    const li = document.createElement("li");
    li.className = "empty";
    li.textContent = "Loading…";
    return li;
}

// ---- table overview view -------------------------------------------------------
async function renderTableView(tab) {
    if (!tab.info) {
        tableView.innerHTML = '<div class="empty">Loading table…</div>';
        try {
            tab.info = await fetchJson(
                `/api/databases/${encodeURIComponent(tab.db)}/tables/${encodeURIComponent(tab.table)}`);
        } catch (err) {
            tableView.innerHTML = `<div class="empty">Failed to load: ${escapeHtml(err.message)}</div>`;
            return;
        }
    }
    paintTableView(tab);
}

function paintTableView(tab) {
    tableView.innerHTML = `
        <div class="tv-subtabs">
            <div class="tv-subtab ${tab.mode === "overview" ? "active" : ""}" data-mode="overview">Overview</div>
            <div class="tv-subtab ${tab.mode === "preview" ? "active" : ""}" data-mode="preview">Data preview</div>
        </div>
        <div class="tv-body" id="tvBody"></div>`;

    tableView.querySelectorAll(".tv-subtab").forEach((el) =>
        el.addEventListener("click", () => {
            if (tab._streamAbort) { tab._streamAbort.abort(); tab._streamAbort = null; }
            tab.mode = el.dataset.mode;
            paintTableView(tab);
        }));

    const body = $("tvBody");
    if (tab.mode === "overview") body.appendChild(overviewSection(tab, tab.info));
    else loadPreview(tab, body);
}

function overviewSection(tab, info) {
    const wrap = document.createElement("div");
    wrap.className = "overview";

    wrap.appendChild(heading("Summary"));
    const summary = document.createElement("div");
    summary.className = "summary-card";
    summary.appendChild(summaryCell("Rows", info.rowCount.toLocaleString()));
    summary.appendChild(summaryCell("Columns", String(info.columnCount)));
    summary.appendChild(summaryCell("Size (on disk)", formatBytes(info.sizeBytes)));
    summary.appendChild(summaryCell("Last modified", formatDate(info.lastModified)));
    wrap.appendChild(summary);

    wrap.appendChild(heading("Quick actions"));
    const actions = document.createElement("div");
    actions.className = "actions-card";
    actions.appendChild(actionBtn("▷", "SELECT TOP 100", () =>
        runInEditor(tab.db, `SELECT TOP (100) *\nFROM [${info.schema}].[${info.table}];`)));
    actions.appendChild(actionBtn("▦", "View data", () => { tab.mode = "preview"; paintTableView(tab); }));
    actions.appendChild(actionBtn("⧉", "Copy name", () => copyText(`[${info.schema}].[${info.table}]`)));
    actions.appendChild(actionBtn("🗑", "DROP", () =>
        runInEditor(tab.db, `DROP TABLE [${info.schema}].[${info.table}];`)));
    actions.appendChild(actionBtn("⟳", "Refresh", async () => { tab.info = null; await renderTableView(tab); }));
    wrap.appendChild(actions);

    wrap.appendChild(heading("Columns"));
    const card = document.createElement("div");
    card.className = "columns-card";
    const search = document.createElement("input");
    search.className = "col-search";
    search.placeholder = "Search columns";
    const tableWrap = document.createElement("div");
    tableWrap.className = "grid-wrap";
    const label = document.createElement("div");
    label.className = "col-count";
    label.textContent = `${info.columns.length} column(s)`;

    const head = document.createElement("div");
    head.className = "columns-head";
    head.appendChild(label);
    head.appendChild(search);
    card.appendChild(head);
    card.appendChild(tableWrap);

    const renderCols = (filter) => {
        const cols = info.columns.filter((c) =>
            !filter || c.name.toLowerCase().includes(filter.toLowerCase()));
        tableWrap.innerHTML = "";
        tableWrap.appendChild(columnsGrid(cols));
    };
    search.addEventListener("input", () => renderCols(search.value));
    renderCols("");

    wrap.appendChild(card);
    return wrap;
}

function columnsGrid(cols) {
    const headers = ["Column ID", "Column name", "Data type", "Nullable", "Maximum length", "Precision", "Scale"];
    const table = document.createElement("table");
    table.className = "grid";
    const htr = document.createElement("tr");
    for (const h of headers) htr.appendChild(th(h));
    const thd = document.createElement("thead");
    thd.appendChild(htr);
    table.appendChild(thd);

    const tbody = document.createElement("tbody");
    for (const c of cols) {
        const tr = document.createElement("tr");
        tr.appendChild(td(c.columnId));
        tr.appendChild(td(`${typeIndicator(c.dataType)} ${c.name}`));
        tr.appendChild(td(c.dataType));
        tr.appendChild(td(c.nullable ? "Yes" : "No"));
        tr.appendChild(td(c.maxLength < 0 ? "max" : c.maxLength));
        tr.appendChild(td(c.precision));
        tr.appendChild(td(c.scale));
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    return table;
}

function typeIndicator(dataType) {
    const t = dataType.toLowerCase();
    if (t.includes("char") || t.includes("text") || t.includes("uniqueidentifier")) return "🅰";
    if (t.includes("date") || t.includes("time")) return "🕐";
    return "#";
}

async function loadPreview(tab, body) {
    // Cancel any previous stream still running for this tab (e.g. user re-entered the subtab).
    if (tab._streamAbort) { tab._streamAbort.abort(); tab._streamAbort = null; }

    const BATCH_SIZE = 10000;   // rows per NDJSON batch requested from the server
    const MAX_RENDER = 20000;   // cap DOM rows so a 500k-row table can't freeze the browser

    body.innerHTML = "";
    const toolbar = document.createElement("div");
    toolbar.className = "stream-toolbar";
    const status = document.createElement("span");
    status.className = "stream-status";
    status.textContent = "Connecting…";
    const stopBtn = document.createElement("button");
    stopBtn.className = "action-btn";
    stopBtn.innerHTML = '<span class="a-icon">■</span><span class="a-label">Stop</span>';
    toolbar.appendChild(status);
    toolbar.appendChild(stopBtn);
    body.appendChild(toolbar);

    const gw = document.createElement("div");
    gw.className = "grid-wrap preview-grid";
    body.appendChild(gw);

    const controller = new AbortController();
    tab._streamAbort = controller;
    stopBtn.addEventListener("click", () => controller.abort());

    let table = null;
    let tbody = null;
    let columns = null;
    let total = 0;
    let rendered = 0;
    let received = 0;
    let capped = false;

    const buildGrid = (cols) => {
        table = document.createElement("table");
        table.className = "grid";
        const htr = document.createElement("tr");
        htr.appendChild(th(""));
        for (const c of cols) htr.appendChild(th(c));
        const thd = document.createElement("thead");
        thd.appendChild(htr);
        table.appendChild(thd);
        tbody = document.createElement("tbody");
        table.appendChild(tbody);
        gw.appendChild(table);
    };

    const appendRows = (start, rows) => {
        const frag = document.createDocumentFragment();
        for (let r = 0; r < rows.length && rendered < MAX_RENDER; r++) {
            const values = rows[r];
            const tr = document.createElement("tr");
            const rn = document.createElement("td");
            rn.className = "rownum";
            rn.textContent = (start + r + 1).toLocaleString();
            tr.appendChild(rn);
            for (let c = 0; c < columns.length; c++) {
                const cell = document.createElement("td");
                const v = values[c];
                if (v === null || v === undefined) { cell.textContent = "NULL"; cell.className = "null"; }
                else cell.textContent = String(v);
                tr.appendChild(cell);
            }
            frag.appendChild(tr);
            rendered++;
        }
        tbody.appendChild(frag);
        if (rendered >= MAX_RENDER && !capped) capped = true;
    };

    const updateStatus = (done) => {
        const totalLabel = total.toLocaleString();
        if (done) {
            const shown = capped ? `showing first ${rendered.toLocaleString()} of ` : "";
            status.textContent = `Loaded ${received.toLocaleString()} row(s) — ${shown}${totalLabel} total`;
        } else {
            status.textContent = `Streaming… ${received.toLocaleString()} / ${totalLabel} row(s)`;
        }
    };

    const url = `/api/databases/${encodeURIComponent(tab.db)}/tables/${encodeURIComponent(tab.info.table)}`
        + `/stream?schema=${encodeURIComponent(tab.info.schema)}&batchSize=${BATCH_SIZE}`;

    try {
        const res = await fetch(url, { signal: controller.signal });
        if (!res.ok) {
            let msg = `HTTP ${res.status}`;
            try { msg = (await res.json()).message || msg; } catch { /* ignore */ }
            throw new Error(msg);
        }

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let bufferText = "";

        const handleLine = (line) => {
            if (!line) return;
            const obj = JSON.parse(line);
            if (columns === null) {
                columns = obj.columns;
                total = obj.totalRows;
                buildGrid(columns);
                updateStatus(false);
                return;
            }
            received += obj.rows.length;
            if (!capped) appendRows(obj.start, obj.rows);
            updateStatus(false);
            // Once the DOM cap is hit, stop pulling more data over the wire.
            if (capped) controller.abort();
        };

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            bufferText += decoder.decode(value, { stream: true });
            let nl;
            while ((nl = bufferText.indexOf("\n")) >= 0) {
                const line = bufferText.slice(0, nl).trim();
                bufferText = bufferText.slice(nl + 1);
                handleLine(line);
            }
        }
        const tailLine = bufferText.trim();
        if (tailLine) handleLine(tailLine);
        updateStatus(true);
    } catch (err) {
        if (err.name === "AbortError") {
            // User pressed Stop or the DOM cap was reached — treat as a clean end.
            updateStatus(true);
        } else if (!columns) {
            body.innerHTML = `<div class="empty">Preview failed: ${escapeHtml(err.message)}</div>`;
        } else {
            status.textContent += " — stream ended: " + err.message;
        }
    } finally {
        stopBtn.disabled = true;
        if (tab._streamAbort === controller) tab._streamAbort = null;
    }
}

// ---- run query (query view) ----------------------------------------------------
function runInEditor(db, sql) {
    dbSelect.value = db;
    editor.value = sql;
    syncGutter();
    activateTab(QUERY_TAB.id);
    runQuery();
}

let queryAbort = null;

// Heuristic: does this statement change the database catalog (so the explorer needs a refresh)?
function isSchemaChange(sql) {
    return /\b(create|alter|drop)\b\s+(or\s+(alter|replace)\s+)?(table|view|procedure|proc|function|database|schema)\b/i.test(sql);
}

async function runQuery() {
    const statement = editor.value.trim();
    if (!statement) return;
    // Abort any in-flight query before starting a new one.
    if (queryAbort) queryAbort.abort();
    queryAbort = new AbortController();

    runBtn.disabled = true;
    cancelBtn.style.display = "";
    cancelBtn.disabled = false;
    setStatus("Running…", "");
    const started = performance.now();
    try {
        const res = await fetch("/api/sql", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ statement, database: dbSelect.value || null }),
            signal: queryAbort.signal,
        });
        if (res.status === 401) {
            showLogin();
            return;
        }
        const data = await res.json();
        const elapsed = ((performance.now() - started) / 1000).toFixed(2);
        renderResult(data);
        if (data.success) {
            setStatus(`Completed in ${elapsed}s — ${data.rowCount} row(s)`, "ok");
            // Auto-refresh the explorer when the statement changed the catalog so new
            // tables/procedures/databases appear (and dropped ones disappear) immediately.
            if (isSchemaChange(statement)) await refreshExplorer();
        } else {
            setStatus("Query failed", "error");
        }
    } catch (err) {
        if (err.name === "AbortError") {
            renderMessages("Query cancelled.", false);
            setStatus("Cancelled", "");
        } else {
            renderMessages("Request failed: " + err.message, true);
            setStatus("Error", "error");
        }
    } finally {
        runBtn.disabled = false;
        cancelBtn.style.display = "none";
        queryAbort = null;
    }
}

function cancelQuery() {
    if (queryAbort) {
        queryAbort.abort();
        cancelBtn.disabled = true;
    }
}

function renderResult(data) {
    renderMessages(data.message || (data.success ? "Command(s) completed successfully." : "Failed."), !data.success);
    if (data.columns && data.columns.length > 0) {
        gridWrap.innerHTML = "";
        gridWrap.appendChild(dataGrid(data.columns, data.rows));
        showTab("results");
    } else {
        gridWrap.innerHTML = '<div class="empty">No result set. See Messages.</div>';
        showTab("messages");
    }
}

function dataGrid(columns, rows) {
    const table = document.createElement("table");
    table.className = "grid";
    const htr = document.createElement("tr");
    htr.appendChild(th(""));
    for (const c of columns) htr.appendChild(th(c));
    const thd = document.createElement("thead");
    thd.appendChild(htr);
    table.appendChild(thd);

    const tbody = document.createElement("tbody");
    rows.forEach((row, i) => {
        const tr = document.createElement("tr");
        const rn = document.createElement("td");
        rn.className = "rownum";
        rn.textContent = i + 1;
        tr.appendChild(rn);
        for (const c of columns) {
            const cell = document.createElement("td");
            const v = row[c];
            if (v === null || v === undefined) { cell.textContent = "NULL"; cell.className = "null"; }
            else cell.textContent = String(v);
            tr.appendChild(cell);
        }
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    return table;
}

// ---- small helpers -------------------------------------------------------------
function heading(text) {
    const h = document.createElement("h2");
    h.className = "ov-heading";
    h.textContent = text;
    return h;
}

function summaryCell(label, value) {
    const c = document.createElement("div");
    c.className = "summary-cell";
    c.innerHTML = `<div class="s-label"></div><div class="s-value"></div>`;
    c.querySelector(".s-label").textContent = label;
    c.querySelector(".s-value").textContent = value;
    return c;
}

function actionBtn(icon, label, onClick) {
    const b = document.createElement("button");
    b.className = "action-btn";
    b.innerHTML = `<span class="a-icon"></span><span class="a-label"></span>`;
    b.querySelector(".a-icon").textContent = icon;
    b.querySelector(".a-label").textContent = label;
    b.addEventListener("click", onClick);
    return b;
}

function th(text) { const el = document.createElement("th"); el.textContent = text; return el; }
function td(text) { const el = document.createElement("td"); el.textContent = text; return el; }

function renderMessages(text, isError) {
    messagesEl.textContent = text;
    messagesEl.classList.toggle("error", !!isError);
}

function setStatus(text, kind) {
    statusEl.textContent = text;
    statusEl.className = "status" + (kind ? " " + kind : "");
}

function formatBytes(n) {
    if (n < 1024) return `${n} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let v = n / 1024, i = 0;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}

function formatDate(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return iso;
    return d.toLocaleString();
}

function copyText(text) {
    if (navigator.clipboard) navigator.clipboard.writeText(text);
}

async function fetchJson(url) {
    const res = await fetch(url);
    if (res.status === 401) {
        showLogin();
        throw new Error("Not authenticated.");
    }
    if (!res.ok) {
        let msg = res.statusText;
        try { const b = await res.json(); if (b.message) msg = b.message; } catch {}
        throw new Error(msg);
    }
    return res.json();
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) =>
        ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

runBtn.addEventListener("click", runQuery);
cancelBtn.addEventListener("click", cancelQuery);
$("refreshBtn").addEventListener("click", () => { tabs = [QUERY_TAB]; activateTab(QUERY_TAB.id); loadDatabases(); });

// ---- draggable panel splitters -------------------------------------------------
// Generic pointer-driven resizer. `axis` is "x" (col-resize) or "y" (row-resize);
// `get` returns the current size in px and `set` applies a new (clamped) size.
function setupResizer(handle, axis, get, set) {
    if (!handle) return;
    handle.addEventListener("pointerdown", (e) => {
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        handle.classList.add("dragging");
        document.body.classList.add("resizing", axis === "x" ? "resizing-v" : "resizing-h");

        const startPos = axis === "x" ? e.clientX : e.clientY;
        const startSize = get();

        const onMove = (ev) => {
            const cur = axis === "x" ? ev.clientX : ev.clientY;
            set(startSize + (cur - startPos));
        };
        const onUp = () => {
            handle.classList.remove("dragging");
            document.body.classList.remove("resizing", "resizing-v", "resizing-h");
            window.removeEventListener("pointermove", onMove);
            window.removeEventListener("pointerup", onUp);
        };

        window.addEventListener("pointermove", onMove);
        window.addEventListener("pointerup", onUp);
    });
    // Double-click a splitter to reset that pane to its default size.
    handle.addEventListener("dblclick", () => set(NaN));
}

const explorerEl = document.querySelector(".explorer");
setupResizer($("vResizer"), "x",
    () => explorerEl.getBoundingClientRect().width,
    (w) => {
        if (isNaN(w)) { explorerEl.style.width = ""; return; }
        const clamped = Math.max(160, Math.min(w, window.innerWidth * 0.6));
        explorerEl.style.width = clamped + "px";
    });

const editorWrapEl = document.querySelector(".editor-wrap");
setupResizer($("hResizer"), "y",
    () => editorWrapEl.getBoundingClientRect().height,
    (h) => {
        if (isNaN(h)) { editorWrapEl.style.flex = ""; return; }
        const available = queryView.getBoundingClientRect().height;
        const clamped = Math.max(120, Math.min(h, available - 160));
        editorWrapEl.style.flex = "0 0 " + clamped + "px";
    });

// ---- authentication ------------------------------------------------------------
// The session lives in an HttpOnly cookie set by /api/login; the UI only tracks display state.
const loginOverlay = $("loginOverlay");
const loginForm = $("loginForm");
const loginUser = $("loginUser");
const loginPass = $("loginPass");
const loginError = $("loginError");
const userbox = $("userbox");
const userName = $("userName");
let authUser = null;
let authIsAdmin = false;

function showLogin() {
    authUser = null;
    userbox.style.display = "none";
    loginOverlay.style.display = "flex";
    // Clear any data from a previous session so a signed-out user sees nothing.
    treeEl.innerHTML = "";
    dbSelect.innerHTML = '<option value="">(none)</option>';
    tabs = [QUERY_TAB];
    activeTabId = QUERY_TAB.id;
    renderTabs();
    activateTab(QUERY_TAB.id);
    loginError.textContent = "";
    loginPass.value = "";
    setTimeout(() => loginUser.focus(), 0);
}

function showApp(login, isAdmin) {
    authUser = login;
    authIsAdmin = isAdmin;
    userName.textContent = isAdmin ? `${login} (admin)` : login;
    userbox.style.display = "flex";
    loginOverlay.style.display = "none";
    loadDatabases();
}

async function initAuth() {
    try {
        const me = await (await fetch("/api/me")).json();
        if (me && me.authenticated) {
            showApp(me.login, me.isAdmin);
            return;
        }
    } catch {}
    showLogin();
}

loginForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    loginError.textContent = "";
    const btn = $("loginSubmit");
    btn.disabled = true;
    try {
        const res = await fetch("/api/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username: loginUser.value.trim(), password: loginPass.value }),
        });
        if (!res.ok) {
            let msg = "Invalid login or password.";
            try { const b = await res.json(); if (b.message) msg = b.message; } catch {}
            loginError.textContent = msg;
            return;
        }
        const body = await res.json();
        showApp(body.login, body.isAdmin);
    } catch (err) {
        loginError.textContent = err.message || "Login failed.";
    } finally {
        btn.disabled = false;
    }
});

$("logoutBtn").addEventListener("click", async () => {
    try { await fetch("/api/logout", { method: "POST" }); } catch {}
    showLogin();
});

// ---- my access (effective permissions) -----------------------------------------
const accessModal = $("accessModal");
$("accessClose").addEventListener("click", () => { accessModal.style.display = "none"; });
$("accessBtn").addEventListener("click", showMyAccess);

async function showMyAccess() {
    const db = dbSelect.value;
    const title = $("accessTitle");
    const body = $("accessBody");
    accessModal.style.display = "flex";
    if (!db) {
        title.textContent = "My permissions";
        body.innerHTML = '<div class="empty">Select a database first to see your permissions in it.</div>';
        return;
    }
    title.textContent = `My permissions in [${db}]`;
    body.innerHTML = '<div class="empty">Loading…</div>';
    try {
        const perms = await fetchJson(`/api/me/permissions?database=${encodeURIComponent(db)}`);
        if (!perms.length) {
            body.innerHTML = '<div class="empty">You have no explicit permissions in this database.</div>';
            return;
        }
        let html = '<table class="access-table"><thead><tr>' +
            '<th>Grantee</th><th>Permission</th><th>Class</th><th>Securable</th><th>State</th>' +
            '</tr></thead><tbody>';
        for (const p of perms) {
            const cls = /DENY/i.test(p.state) ? "deny" : "grant";
            html += `<tr class="${cls}"><td>${escapeHtml(p.grantee)}</td><td>${escapeHtml(p.permission)}</td>` +
                `<td>${escapeHtml(p.class)}</td><td>${escapeHtml(p.securable)}</td><td>${escapeHtml(p.state)}</td></tr>`;
        }
        html += "</tbody></table>";
        body.innerHTML = html;
    } catch (err) {
        body.innerHTML = `<div class="empty">${escapeHtml(err.message)}</div>`;
    }
}

// init
renderTabs();
syncGutter();
initAuth();
