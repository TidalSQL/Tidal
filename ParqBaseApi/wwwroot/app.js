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

// ---- context menu --------------------------------------------------------------
// A single floating context menu shared by the explorer. `items` is an array of
// { label, danger?, action } — action may be async; errors surface via setStatus.
let openMenuEl = null;

function closeContextMenu() {
    if (openMenuEl) { openMenuEl.remove(); openMenuEl = null; }
    document.removeEventListener("mousedown", onDocDownForMenu, true);
    document.removeEventListener("keydown", onKeyForMenu, true);
    window.removeEventListener("blur", closeContextMenu);
}

function onDocDownForMenu(e) {
    if (openMenuEl && !openMenuEl.contains(e.target)) closeContextMenu();
}

function onKeyForMenu(e) {
    if (e.key === "Escape") closeContextMenu();
}

function showContextMenu(x, y, items) {
    closeContextMenu();
    if (!items || items.length === 0) return;

    const menu = document.createElement("div");
    menu.className = "ctx-menu";
    for (const it of items) {
        const el = document.createElement("div");
        el.className = "ctx-item" + (it.danger ? " danger" : "");
        el.textContent = it.label;
        el.addEventListener("click", async () => {
            closeContextMenu();
            try {
                await it.action();
            } catch (err) {
                setStatus(err.message, "error");
            }
        });
        menu.appendChild(el);
    }

    document.body.appendChild(menu);
    // Keep the menu within the viewport.
    const rect = menu.getBoundingClientRect();
    menu.style.left = Math.min(x, window.innerWidth - rect.width - 4) + "px";
    menu.style.top = Math.min(y, window.innerHeight - rect.height - 4) + "px";
    openMenuEl = menu;

    setTimeout(() => {
        document.addEventListener("mousedown", onDocDownForMenu, true);
        document.addEventListener("keydown", onKeyForMenu, true);
        window.addEventListener("blur", closeContextMenu);
    }, 0);
}

// Attaches a right-click (and long-press-friendly) context menu to a node element. `itemsFn`
// is evaluated lazily so menus reflect current state.
function attachContextMenu(el, itemsFn) {
    el.classList.add("has-ctx");
    el.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        e.stopPropagation();
        showContextMenu(e.clientX, e.clientY, itemsFn());
    });
}

// ---- explorer tree -------------------------------------------------------------
// Databases the user has expanded in the tree. Tracked so the explorer can rebuild
// itself (e.g. after DDL) without losing which nodes were open.
const expandedDbs = new Set();

// Expansion state for the fixed SSMS-style folder nodes (Server, Databases, Security, …), so a
// rebuild after DDL preserves which folders were open. Server/Databases/Security default to open.
const expandedNodes = new Set(["server", "databases", "security"]);

// Builds the whole explorer as an SSMS-style tree: a single Server node containing Databases
// (the accessible database list) and Security (Logins, Server Roles). Keeps the name loadDatabases
// so existing callers (refresh button, post-DDL refresh, sign-in) need no changes.
async function loadDatabases() {
    treeEl.innerHTML = "";
    const previousSelection = dbSelect.value;
    dbSelect.innerHTML = '<option value="">(none)</option>';

    // Databases are fetched up front so the query-target dropdown is populated even when the
    // Databases folder is collapsed.
    let dbs = [];
    try {
        dbs = await fetchJson("/api/databases");
    } catch (err) {
        treeEl.innerHTML = `<li class="empty">Failed to load: ${escapeHtml(err.message)}</li>`;
        return;
    }
    for (const name of dbs) {
        const opt = document.createElement("option");
        opt.value = name;
        opt.textContent = name;
        dbSelect.appendChild(opt);
    }

    // Forget any expanded databases that no longer exist, and keep the selected database
    // in the dropdown if it is still available.
    for (const n of [...expandedDbs]) if (!dbs.includes(n)) expandedDbs.delete(n);
    if (dbs.includes(previousSelection)) dbSelect.value = previousSelection;

    makeServerNode(dbs);
}

// A generic, lazily-loaded collapsible folder. Records its open/closed state in expandedNodes
// (keyed by `key`) so it survives a rebuild, and invokes `loader(childUl)` the first time it
// opens. Appends itself to `parentUl` and returns the children <ul>.
function lazyFolder(parentUl, key, icon, label, loader, decorate) {
    const li = document.createElement("li");
    const open = expandedNodes.has(key);
    const node = rowNode(open ? "▼" : "▶", icon, label);
    if (decorate) decorate(node);
    const ul = document.createElement("ul");
    ul.className = "children" + (open ? "" : " collapsed");
    let loaded = false;

    async function expand() {
        ul.classList.remove("collapsed");
        node.querySelector(".twisty").textContent = "▼";
        expandedNodes.add(key);
        if (!loaded) {
            loaded = true;
            ul.innerHTML = "";
            ul.appendChild(loadingNode());
            await loader(ul);
        }
    }

    function collapse() {
        ul.classList.add("collapsed");
        node.querySelector(".twisty").textContent = "▶";
        expandedNodes.delete(key);
    }

    node.addEventListener("click", () =>
        ul.classList.contains("collapsed") ? expand() : collapse());

    li.appendChild(node);
    li.appendChild(ul);
    parentUl.appendChild(li);

    if (open) expand();
    return ul;
}

// Builds the root Server node with its Databases and Security branches.
function makeServerNode(dbs) {
    const label = authUser ? `${authUser} (ParqBase)` : "ParqBase";
    lazyFolder(treeEl, "server", "🖥", label, async (serverUl) => {
        serverUl.innerHTML = "";

        lazyFolder(serverUl, "databases", "📁", "Databases", async (dbUl) => {
            dbUl.innerHTML = "";
            if (dbs.length === 0) {
                dbUl.innerHTML = '<li class="empty">No databases.</li>';
                return;
            }
            for (const name of dbs) dbUl.appendChild(makeDatabaseNode(name));
        });

        lazyFolder(serverUl, "security", "📁", "Security", async (secUl) => {
            secUl.innerHTML = "";
            lazyFolder(secUl, "logins", "📁", "Logins", loadLogins, (node) => {
                attachContextMenu(node, () => [
                    {
                        label: "New login…",
                        action: createLogin,
                    },
                ]);
            });
            lazyFolder(secUl, "serverroles", "📁", "Server Roles", loadServerRoles);
        });
    });
}

// Opens the "New login" modal dialog (name + password in one form). Shared by the Logins
// folder's "New login…" context-menu item. Submission is wired in the auth section.
function createLogin() {
    openNewLoginModal();
}

// Login multi-selection state (SSMS-style: click, Ctrl/Cmd-click to toggle, Shift-click for a
// range). Selection is cleared whenever the Logins list is (re)loaded.
const selectedLogins = new Set();
let lastLoginClickIndex = null;

function applyLoginSelection(rows) {
    for (const r of rows) r.el.classList.toggle("selected", selectedLogins.has(r.name));
}

function handleLoginClick(e, name, index, rows) {
    if (e.shiftKey && lastLoginClickIndex !== null) {
        if (!(e.ctrlKey || e.metaKey)) selectedLogins.clear();
        const [a, b] = [lastLoginClickIndex, index].sort((x, y) => x - y);
        for (let i = a; i <= b; i++) selectedLogins.add(rows[i].name);
    } else if (e.ctrlKey || e.metaKey) {
        if (selectedLogins.has(name)) selectedLogins.delete(name);
        else selectedLogins.add(name);
        lastLoginClickIndex = index;
    } else {
        selectedLogins.clear();
        selectedLogins.add(name);
        lastLoginClickIndex = index;
    }
    applyLoginSelection(rows);
}

// Deletes one or more logins, reporting per-login failures (e.g. self-drop / last-sysadmin guards).
async function deleteLogins(names) {
    if (names.length === 0) return;
    const prompt = names.length > 1
        ? `Delete ${names.length} logins? This cannot be undone.`
        : `Delete login "${names[0]}"? This cannot be undone.`;
    if (!confirm(prompt)) return;

    let ok = 0;
    const errors = [];
    for (const n of names) {
        try {
            await sendJson(`/api/server/logins/${encodeURIComponent(n)}`, "DELETE");
            ok++;
        } catch (err) {
            errors.push(`${n}: ${err.message}`);
        }
    }
    selectedLogins.clear();

    if (errors.length) {
        setStatus(`Deleted ${ok} of ${names.length} login(s). Failed — ${errors.join("; ")}`, "error");
    } else {
        setStatus(`Deleted ${ok} login(s).`, "ok");
    }
    await refreshExplorer();
}

async function loadLogins(container) {
    container.innerHTML = "";
    selectedLogins.clear();
    lastLoginClickIndex = null;

    let logins = [];
    try {
        logins = await fetchJson("/api/server/logins");
    } catch (err) {
        container.innerHTML = `<li class="empty">${escapeHtml(err.message)}</li>`;
        return;
    }
    if (logins.length === 0) {
        container.innerHTML = '<li class="empty">No logins (or not authorized).</li>';
        return;
    }

    // Rows in display order, so Shift-click can select a contiguous range.
    const rows = [];
    logins.forEach((l, index) => {
        const li = document.createElement("li");
        const label = l.disabled ? `${l.name} (disabled)` : l.name;
        const ln = rowNode("", l.isSysadmin ? "🛡" : "👤", label);

        ln.addEventListener("click", (e) => handleLoginClick(e, l.name, index, rows));

        attachContextMenu(ln, () => {
            // Right-clicking a row outside the current selection resets the selection to just it.
            if (!selectedLogins.has(l.name)) {
                selectedLogins.clear();
                selectedLogins.add(l.name);
                lastLoginClickIndex = index;
                applyLoginSelection(rows);
            }
            const names = [...selectedLogins];
            return [
                {
                    label: names.length > 1 ? `Delete ${names.length} logins` : `Delete login "${names[0]}"`,
                    danger: true,
                    action: () => deleteLogins(names),
                },
            ];
        });

        li.appendChild(ln);
        container.appendChild(li);
        rows.push({ name: l.name, el: ln });
    });
}

async function loadServerRoles(container) {
    container.innerHTML = "";
    let roles = [];
    try {
        roles = await fetchJson("/api/server/roles");
    } catch (err) {
        container.innerHTML = `<li class="empty">${escapeHtml(err.message)}</li>`;
        return;
    }
    if (roles.length === 0) {
        container.innerHTML = '<li class="empty">No server roles (or not authorized).</li>';
        return;
    }
    for (const r of roles) {
        // Each role is a collapsible node whose children are its login members. Right-click the
        // role to add a member; right-click a member to remove it.
        lazyFolder(container, `role:${r.name}`, "🛡", r.name, async (memberUl) => {
            memberUl.innerHTML = "";
            if (!r.members || r.members.length === 0) {
                memberUl.innerHTML = '<li class="empty">No members.</li>';
                return;
            }
            for (const m of r.members) {
                const li = document.createElement("li");
                const mn = rowNode("", "👤", m);
                attachContextMenu(mn, () => [
                    {
                        label: `Remove "${m}" from ${r.name}`,
                        danger: true,
                        action: async () => {
                            const res = await sendJson(
                                `/api/server/roles/${encodeURIComponent(r.name)}/members/${encodeURIComponent(m)}`,
                                "DELETE");
                            setStatus(res.message || `Removed "${m}" from ${r.name}.`, "ok");
                            await refreshExplorer();
                        },
                    },
                ]);
                li.appendChild(mn);
                memberUl.appendChild(li);
            }
        }, (node) => {
            attachContextMenu(node, () => [
                {
                    label: `Add member to ${r.name}…`,
                    action: async () => {
                        const login = (prompt(`Add which login to server role "${r.name}"?`) || "").trim();
                        if (!login) return;
                        const res = await sendJson(
                            `/api/server/roles/${encodeURIComponent(r.name)}/members`,
                            "POST", { login });
                        setStatus(res.message || `Added "${login}" to ${r.name}.`, "ok");
                        await refreshExplorer();
                    },
                },
            ]);
        });
    }
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

    attachContextMenu(node, () => [
        {
            label: "Grant read-only access to login…",
            action: () => grantDatabaseAccess(name, "readonly"),
        },
        {
            label: "Grant read/write access to login…",
            action: () => grantDatabaseAccess(name, "readwrite"),
        },
    ]);

    // Re-expand automatically if this database was open before a refresh.
    if (expandedDbs.has(name)) { expand(); }

    return li;
}

// Prompts for a login and grants it read-only or read/write access to one database
// via POST /api/databases/{db}/access, then refreshes the explorer.
async function grantDatabaseAccess(database, level) {
    const login = (prompt(
        `Grant ${level === "readwrite" ? "read/write" : "read-only"} access on "${database}" to which login?`) || "").trim();
    if (!login) return;
    try {
        const res = await sendJson(
            `/api/databases/${encodeURIComponent(database)}/access`, "POST", { login, level });
        setStatus(res.message || `Granted ${level} access on ${database} to ${login}.`, "ok");
        await refreshExplorer();
    } catch (err) {
        setStatus(err.message || `Failed to grant access on ${database}.`, "error");
    }
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
    summary.appendChild(summaryCell(
        "Storage",
        info.isMultiPart ? `Multi-part (${info.partCount} parts)` : "Single file"));
    summary.appendChild(summaryCell("Created", formatDateOrNever(info.createdAt)));
    summary.appendChild(summaryCell("Last data change", formatDateOrNever(info.lastDataChange)));
    summary.appendChild(summaryCell("Last schema change", formatDateOrNever(info.lastSchemaChange, "—")));
    summary.appendChild(summaryCell("Last query run", formatDateOrNever(info.lastQueryRun, "Never")));
    wrap.appendChild(summary);

    wrap.appendChild(heading("Security and permissions"));
    wrap.appendChild(permissionsSection(info.permissions || []));

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

// Renders the table's GRANT/DENY permissions (table-, schema-, and database-scoped) as a grid.
function permissionsSection(perms) {
    const card = document.createElement("div");
    card.className = "columns-card";

    if (!perms.length) {
        card.innerHTML = '<div class="empty">No explicit permissions. Only sysadmins and the db_owner role can access this table until access is granted.</div>';
        return card;
    }

    const table = document.createElement("table");
    table.className = "grid";
    const htr = document.createElement("tr");
    for (const h of ["Grantee", "Permission", "Access", "Scope"]) htr.appendChild(th(h));
    const thd = document.createElement("thead");
    thd.appendChild(htr);
    table.appendChild(thd);

    const tbody = document.createElement("tbody");
    for (const p of perms) {
        const tr = document.createElement("tr");
        tr.appendChild(td(p.grantee));
        tr.appendChild(td(p.permission));

        const access = td("");
        const badge = document.createElement("span");
        badge.className = p.state === "DENY" ? "perm-deny" : "perm-grant";
        badge.textContent = p.state;
        access.appendChild(badge);
        tr.appendChild(access);

        tr.appendChild(td(p.scope));
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);

    const wrap = document.createElement("div");
    wrap.className = "grid-wrap";
    wrap.appendChild(table);
    card.appendChild(wrap);
    return card;
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

// Like formatDate but renders a friendly placeholder when the timestamp is missing (null).
function formatDateOrNever(iso, placeholder) {
    if (iso === null || iso === undefined) return placeholder || "—";
    return formatDate(iso);
}

function copyText(text) {
    if (navigator.clipboard) navigator.clipboard.writeText(text);
}

async function fetchJson(url, options) {
    const res = await fetch(url, options);
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

// POSTs/DELETEs JSON to a mutating endpoint and returns the parsed body. Reuses fetchJson's 401 and
// error-message handling.
function sendJson(url, method, body) {
    const options = { method };
    if (body !== undefined) {
        options.headers = { "Content-Type": "application/json" };
        options.body = JSON.stringify(body);
    }
    return fetchJson(url, options);
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

// ---- new-login modal -----------------------------------------------------------
const newLoginModal = $("newLoginModal");
const newLoginForm = $("newLoginForm");
const newLoginUser = $("newLoginUser");
const newLoginPass = $("newLoginPass");
const newLoginError = $("newLoginError");

function openNewLoginModal() {
    newLoginError.textContent = "";
    newLoginUser.value = "";
    newLoginPass.value = "";
    newLoginModal.style.display = "flex";
    setTimeout(() => newLoginUser.focus(), 0);
}

function closeNewLoginModal() {
    newLoginModal.style.display = "none";
}

$("newLoginCancel").addEventListener("click", closeNewLoginModal);
// Dismiss when clicking the backdrop (but not the card) or pressing Escape.
newLoginModal.addEventListener("mousedown", (e) => {
    if (e.target === newLoginModal) closeNewLoginModal();
});
document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && newLoginModal.style.display === "flex") closeNewLoginModal();
});

newLoginForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    newLoginError.textContent = "";
    const name = newLoginUser.value.trim();
    const password = newLoginPass.value;
    if (!name) { newLoginError.textContent = "A login name is required."; return; }
    if (!password) { newLoginError.textContent = "A password is required."; return; }

    const btn = $("newLoginSubmit");
    btn.disabled = true;
    try {
        const res = await sendJson("/api/server/logins", "POST", { login: name, password });
        closeNewLoginModal();
        setStatus(res.message || `Login "${name}" created.`, "ok");
        await refreshExplorer();
    } catch (err) {
        newLoginError.textContent = err.message || "Failed to create login.";
    } finally {
        btn.disabled = false;
    }
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
