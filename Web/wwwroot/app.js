// Greatness Bot — web admin panel (vanilla JS, no build step).
// All data comes from /api (same origin); Discord IDs are strings.
"use strict";

// ================= Helpers =================

/** Creates a DOM element. attrs: class, on* handlers, other attributes; children: nodes or strings. */
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
    else if (k === "value") el.value = v;
    else if (k === "style") el.style.cssText = v; // CSSOM, allowed by the CSP (style attributes are not)
    else if (v === true) el.setAttribute(k, "");
    else el.setAttribute(k, v);
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

class ApiError extends Error {
  constructor(message, status, data) { super(message); this.status = status; this.data = data; }
}

async function api(method, path, body) {
  const opts = { method, headers: { "X-Panel": "1" }, credentials: "same-origin" };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  const res = await fetch(path, opts);
  let data = null;
  const text = await res.text();
  if (text) { try { data = JSON.parse(text); } catch { data = null; } }
  if (!res.ok) {
    if (res.status === 401 && path !== "/api/me") { renderLogin(); throw new ApiError("Session expired — log in again.", 401); }
    throw new ApiError((data && data.error) || `Request failed (${res.status})`, res.status, data);
  }
  return data;
}

const gApi = (method, path, body) => api(method, `/api/guilds/${state.guildId}${path}`, body);

function toast(text, kind = "ok") {
  const el = h("div", { class: `toast ${kind}` }, text);
  document.getElementById("toasts").append(el);
  setTimeout(() => el.remove(), kind === "error" ? 7000 : 4000);
}

/** Runs an action, shows success/error toasts, disables the button while running. */
async function act(button, fn, okText) {
  if (button) button.disabled = true;
  try {
    const result = await fn();
    if (okText) toast(typeof okText === "function" ? okText(result) : okText);
    return result;
  } catch (e) {
    toast(e.message || String(e), "error");
    return undefined;
  } finally {
    if (button) button.disabled = false;
  }
}

const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } }
};

function fmtDate(ms) {
  if (!ms) return "no time";
  return new Date(ms).toLocaleString(undefined, { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}

function fmtRelative(ms) {
  const diff = Math.round((ms - Date.now()) / 60000);
  const abs = Math.abs(diff);
  const text = abs < 60 ? `${abs} min` : abs < 1440 ? `${Math.round(abs / 60)} h` : `${Math.round(abs / 1440)} d`;
  return diff >= 0 ? `in ${text}` : `${text} ago`;
}

function member(m) {
  if (!m) return h("span", { class: "muted" }, "—");
  return h("span", { class: "member", title: m.username ? `@${m.username}` : m.id },
    m.avatarUrl ? h("img", { class: "avatar sm", src: m.avatarUrl, alt: "" }) : h("span", { class: "avatar sm" }),
    m.name, m.onServer ? null : h("span", { class: "muted small" }, " (left)"));
}

function roleChip(r, onRemove) {
  return h("span", { class: `chip${r.exists === false ? " warn" : ""}` },
    h("span", { class: "swatch", style: r.color ? `background:${r.color}` : null }),
    `@${r.name}`,
    onRemove ? h("button", { title: "Remove", onclick: onRemove }, "×") : null);
}

function empty(text) { return h("div", { class: "empty" }, text); }

// ================= Dialogs =================

const dialog = () => document.getElementById("dialog");

function confirmDialog(title, text, okLabel = "Confirm", danger = true) {
  return new Promise(resolve => {
    const d = dialog();
    d.replaceChildren(
      h("h2", {}, title),
      h("p", { class: "muted" }, text),
      h("div", { class: "actions" },
        h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(false); } }, "Cancel"),
        h("button", { class: `btn ${danger ? "danger" : ""}`, onclick: () => { d.close(); resolve(true); } }, okLabel)));
    d.onclose = () => resolve(false);
    d.showModal();
  });
}

/** Member picker with search (for room ownership transfer). */
function pickMemberDialog(title) {
  return new Promise(resolve => {
    const d = dialog();
    const list = h("div", { class: "pick-list" });
    let timer = 0;
    const search = async q => {
      try {
        const members = await gApi("GET", `/members?q=${encodeURIComponent(q)}`);
        list.replaceChildren(...(members.length ? members.map(m =>
          h("button", { onclick: () => { d.close(); resolve(m); } }, member(m), h("span", { class: "muted small" }, m.username ? `@${m.username}` : ""))) :
          [empty("Nobody found.")]));
      } catch (e) { list.replaceChildren(empty(e.message)); }
    };
    const input = h("input", { placeholder: "Search by name…", oninput: e => { clearTimeout(timer); timer = setTimeout(() => search(e.target.value), 250); } });
    d.replaceChildren(h("h2", {}, title), input, list,
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(null); } }, "Cancel")));
    d.onclose = () => resolve(null);
    d.showModal();
    input.focus();
    search("");
  });
}

// ================= Form controls =================

function roleSelect(placeholder = "Choose a role…", exclude = []) {
  return h("select", {},
    h("option", { value: "" }, placeholder),
    state.roles.filter(r => !exclude.includes(r.id)).map(r => h("option", { value: r.id }, `@${r.name}`)));
}

function channelSelect(kind, placeholder) {
  const list = state.channels[kind] || [];
  return h("select", {},
    h("option", { value: "" }, placeholder),
    list.map(c => h("option", { value: c.id, disabled: kind === "text" && !c.canPost },
      `${kind === "categories" ? "" : "#"}${c.name}${c.category ? `  ·  ${c.category}` : ""}${kind === "text" && !c.canPost ? "  (no access)" : ""}`)));
}

/** Editable list of roles: chips + "add" dropdown. Returns { el, value() }. */
function roleListEditor(initial) {
  let ids = initial.map(r => r.id);
  const known = new Map(initial.map(r => [r.id, r]));
  for (const r of state.roles) known.set(r.id, { ...r, exists: true });
  const el = h("div", {});
  const draw = () => {
    const sel = roleSelect("+ Add role…", ids);
    sel.addEventListener("change", () => { if (sel.value) { ids.push(sel.value); draw(); } });
    el.replaceChildren(
      h("div", {}, ids.length ? ids.map(id => roleChip(known.get(id) || { id, name: "deleted role", exists: false }, () => { ids = ids.filter(x => x !== id); draw(); }))
                              : h("span", { class: "muted" }, "not set")),
      h("div", { style: "margin-top:6px" }, sel));
  };
  draw();
  return { el, value: () => ids.slice() };
}

/** "Post panel in channel" control. */
function postPanelCard(title, hint, path) {
  const sel = channelSelect("text", "Choose a text channel…");
  const btn = h("button", { class: "btn" }, "📌 Post");
  btn.addEventListener("click", () => {
    if (!sel.value) return toast("Choose a channel first.", "warn");
    act(btn, () => gApi("POST", path, { channelId: sel.value }), "Panel posted.");
  });
  return h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h3", {}, title)),
    h("p", { class: "muted small" }, hint),
    h("div", { class: "row" }, h("div", { class: "grow" }, sel), btn));
}

// ================= State & shell =================

const state = {
  me: null,
  guilds: [],
  bot: null,
  connection: "",
  guildId: null,
  roles: [],
  channels: { text: [], voice: [], categories: [] },
  tab: store.get("gb.tab") || "overview"
};

// [key, icon, label, required permission (null = everyone with access)]
const allTabs = [
  ["overview", "🏠", "Overview", null],
  ["lfg", "📣", "LFG", "lfg"],
  ["tickets", "🎫", "Tickets", "tickets"],
  ["voice", "🔊", "Temp Voice", "voice"],
  ["texts", "📝", "Panel texts", "texts"],
  ["access", "🔑", "Access", "access"]
];
const can = perm => !perm || (state.me && state.me.permissions.includes(perm));
const visibleTabs = () => allTabs.filter(t => can(t[3]));
const PERM_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Temp Voice", texts: "📝 Panel texts" };

function renderLogin(message) {
  const params = new URLSearchParams(location.search);
  const failed = params.get("login") === "failed";
  if (failed) history.replaceState(null, "", "/");
  document.getElementById("app").replaceChildren(
    h("div", { class: "card login" },
      h("div", { class: "logo" }, "🛠️"),
      h("h1", {}, "Greatness Bot"),
      h("p", {}, "Admin panel. Log in with your Discord account."),
      failed ? h("p", { class: "error-text" }, "Login was cancelled or failed. Try again.") : null,
      message ? h("p", { class: "error-text" }, message) : null,
      h("a", { class: "btn lg", href: "/login" }, "Login with Discord")));
}

function renderDenied(me) {
  document.getElementById("app").replaceChildren(
    h("div", { class: "card login" },
      h("div", { class: "logo" }, "⛔"),
      h("h1", {}, "No access"),
      h("p", {}, `You are logged in as ${me.name}, but this account can't use the admin panel.`),
      h("button", { class: "btn secondary", onclick: logout }, "Log out")));
}

async function logout() {
  try { await api("POST", "/logout"); } catch { /* ignore */ }
  location.href = "/";
}

async function boot() {
  let me;
  try {
    me = await api("GET", "/api/me");
  } catch (e) {
    if (e.status === 401) return renderLogin(e.data && e.data.loginConfigured === false
      ? "Discord login is not configured on the server yet (Web__ClientSecret)." : undefined);
    return renderLogin(e.message);
  }
  if (!me.allowed) return renderDenied(me);
  state.me = me;

  try {
    const data = await api("GET", "/api/guilds");
    state.guilds = data.guilds;
    state.bot = data.bot;
    state.connection = data.connection;
  } catch (e) {
    toast(e.message, "error");
  }

  const saved = store.get("gb.guild");
  state.guildId = state.guilds.some(g => g.id === saved) ? saved : (state.guilds[0] && state.guilds[0].id) || null;
  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = "overview";
  await selectGuild(state.guildId);
}

async function selectGuild(id) {
  state.guildId = id;
  if (id) store.set("gb.guild", id);
  state.roles = [];
  state.channels = { text: [], voice: [], categories: [] };
  if (id) {
    try {
      const [roles, channels] = await Promise.all([gApi("GET", "/roles"), gApi("GET", "/channels")]);
      state.roles = roles;
      state.channels = channels;
    } catch (e) { toast(e.message, "error"); }
  }
  renderShell();
}

function renderShell() {
  const guildSel = h("select", { onchange: e => selectGuild(e.target.value) },
    state.guilds.map(g => h("option", { value: g.id, selected: g.id === state.guildId }, g.name)));
  const connected = state.connection === "Connected";

  const nav = h("nav", { class: "nav" }, visibleTabs().map(([key, icon, label]) =>
    h("button", { class: state.tab === key ? "active" : "", onclick: () => { state.tab = key; store.set("gb.tab", key); renderShell(); } }, icon, " ", label)));

  const main = h("main", { class: "main" });

  document.getElementById("app").replaceChildren(
    h("header", { class: "topbar" },
      h("div", { class: "brand" }, h("span", { class: `dot ${connected ? "ok" : "bad"}`, title: `Bot: ${state.connection || "unknown"}` }), "Greatness Bot · Admin"),
      state.guilds.length ? guildSel : h("span", { class: "muted" }, "The bot is on no servers"),
      h("div", { class: "user" },
        state.me.avatarUrl ? h("img", { class: "avatar", src: state.me.avatarUrl, alt: "" }) : null,
        h("span", {}, state.me.name),
        h("button", { class: "btn ghost", onclick: logout, title: "Log out" }, "Log out"))),
    h("div", { class: "layout" }, nav, main));

  if (!state.guildId) { main.append(empty("The bot is not on any server yet.")); return; }
  const page = { overview: pageOverview, lfg: pageLfg, tickets: pageTickets, voice: pageVoice, texts: pageTexts, access: pageAccess }[state.tab];
  page(main);
}

/** Re-renders the current page only (keeps the shell). */
function refresh() { renderShell(); }

// ================= Overview =================

async function pageOverview(main) {
  main.append(h("h1", {}, "Overview"));
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);
  try {
    const o = await gApi("GET", "/overview");
    const stat = (label, value, sub, tab) => (tab && !can(tab)) ? null : h("div", { class: "stat", style: tab ? "cursor:pointer" : null,
      onclick: tab ? () => { state.tab = tab; store.set("gb.tab", tab); renderShell(); } : null },
      h("div", { class: "label" }, label), h("div", { class: "value" }, value), h("div", { class: "sub" }, sub));
    holder.replaceChildren(
      h("div", { class: "stats" },
        stat("Server", o.guild.members, `${o.guild.name} · members`),
        stat("📣 LFG groups", o.lfg.groups, `${o.lfg.activities} activities`, "lfg"),
        stat("🎫 Open tickets", o.tickets.open, `${o.tickets.projects} projects`, "tickets"),
        stat("🔊 Active rooms", o.voice.rooms, `${o.voice.creators} 'join to create' channels`, "voice")),
      h("div", { class: "card" },
        h("h3", {}, "Bot"),
        h("p", { class: "muted" }, `Status: ${state.connection || "unknown"}`, state.bot ? ` · ${state.bot.name}` : ""),
        h("p", { class: "muted small" }, "Changes made here apply immediately — the same data the bot uses in Discord.")));
  } catch (e) { holder.replaceChildren(empty(e.message)); }
}

// ================= LFG =================

async function pageLfg(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📣 LFG"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));

  // --- Activities
  const actCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Activities")),
    h("p", { class: "muted small" }, "Members with the role are pinged and get a DM when a group for the activity is posted."));
  const actBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  const nameIn = h("input", { placeholder: "Activity name, e.g. Fire Temple", maxlength: "100" });
  const roleIn = roleSelect("Role to ping…");
  const addBtn = h("button", { class: "btn" }, "➕ Add");
  addBtn.addEventListener("click", async () => {
    const ok = await act(addBtn, () => gApi("POST", "/lfg/activities", { name: nameIn.value, roleId: roleIn.value }), "Activity added.");
    if (ok !== undefined) refresh();
  });
  actCard.append(actBody, h("div", { class: "row", style: "margin-top:12px" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn));

  main.append(h("div", { class: "grid2" }, actCard,
    postPanelCard("LFG panel", "Posts the message with the “Create LFG” button.", "/lfg/panel")));

  // --- Groups
  const groupsCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active groups")));
  const groupsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  groupsCard.append(groupsBody);
  main.append(groupsCard);

  try {
    const acts = await gApi("GET", "/lfg/activities");
    actBody.replaceChildren(acts.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "Ping role"), h("th", {}))),
      h("tbody", {}, acts.map(a => {
        const rm = h("button", { class: "btn ghost" }, "Remove");
        rm.addEventListener("click", async () => {
          if (!await confirmDialog(`Remove ${a.name}?`, "Groups that are already posted are not affected.", "Remove")) return;
          if (await act(rm, () => gApi("DELETE", `/lfg/activities/${encodeURIComponent(a.key)}`), "Activity removed.") !== undefined) refresh();
        });
        return h("tr", {}, h("td", {}, a.name), h("td", {}, roleChip(a.role)), h("td", { class: "actions" }, rm));
      })))) : empty("No activities yet."));
  } catch (e) { actBody.replaceChildren(empty(e.message)); }

  try {
    const groups = await gApi("GET", "/lfg/groups");
    groupsBody.replaceChildren(groups.length ? h("div", { class: "grid2" }, groups.map(groupCard)) : empty("No active groups."));
  } catch (e) { groupsBody.replaceChildren(empty(e.message)); }
}

function groupCard(g) {
  const close = h("button", { class: "btn danger" }, "🗑️ Force close");
  close.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The group is closed and its post is deleted in Discord.", "Close & delete")) return;
    if (await act(close, () => gApi("POST", `/lfg/groups/${encodeURIComponent(g.id)}/close`), "Group closed.") !== undefined) refresh();
  });

  return h("div", { class: "card", style: "margin:0" },
    h("div", { class: "card-head" }, h("h3", {}, g.activity), h("span", { class: "badge ok" }, `${g.filled}/${g.size}`)),
    h("div", { class: "muted small" }, "Organizer: ", member(g.owner)),
    h("div", { class: "muted small" }, `Start: ${fmtDate(g.startsAt)}`, g.channel ? ` · #${g.channel}` : ""),
    h("div", { class: "muted small", style: "margin-bottom:8px" }, `Auto-delete ${fmtRelative(g.expiresAt)}`),
    h("ul", { class: "slots" }, g.slots.map((s, i) => h("li", {},
      h("span", { class: "num" }, i + 1), h("span", {}, s.emoji),
      s.member ? member(s.member) : h("span", { class: "free" }, `free (${s.slotRole})`),
      s.character ? h("span", { class: "char" }, s.character) : null))),
    g.reserve.length ? h("div", { style: "margin-top:8px" }, h("div", { class: "muted small" }, `Reserve (${g.reserve.length})`),
      h("ul", { class: "slots" }, g.reserve.map(r => h("li", {}, h("span", {}, r.emoji), member(r.member),
        r.character ? h("span", { class: "char" }, r.character) : null)))) : null,
    h("div", { class: "row", style: "margin-top:12px" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, "Open in Discord"), close));
}

// ================= Tickets =================

async function pageTickets(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎫 Tickets"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));

  const nameIn = h("input", { placeholder: "Project name, e.g. Aion 2", maxlength: "100" });
  const roleIn = roleSelect("Role given on Approve…");
  const addBtn = h("button", { class: "btn" }, "➕ Add project");
  addBtn.addEventListener("click", async () => {
    if (await act(addBtn, () => gApi("POST", "/tickets/projects", { name: nameIn.value, memberRoleId: roleIn.value }),
      "Project added. Now set its leadership roles.") !== undefined) refresh();
  });

  main.append(h("div", { class: "grid2" },
    h("div", { class: "card" }, h("h3", {}, "New project"),
      h("p", { class: "muted small" }, "Applicants pick the project in the ticket panel."),
      h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn)),
    postPanelCard("Ticket panel", "Posts the message with the button to open a ticket.", "/tickets/panel")));

  const projectsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  const openBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(h("h2", {}, "Projects"), projectsBody,
    h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Open tickets")), openBody));

  try {
    const projects = await gApi("GET", "/tickets/projects");
    projectsBody.replaceChildren(...(projects.length ? projects.map(projectCard) : [h("div", { class: "card" }, empty("No projects yet."))]));
  } catch (e) { projectsBody.replaceChildren(empty(e.message)); }

  try {
    const open = await gApi("GET", "/tickets/open");
    openBody.replaceChildren(open.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Project"), h("th", {}, "Applicant"), h("th", {}, "Status"), h("th", {}, "Opened"), h("th", {}))),
      h("tbody", {}, open.map(t => {
        const close = h("button", { class: "btn danger" }, "Force close");
        close.addEventListener("click", async () => {
          if (!await confirmDialog("Close this ticket?", `The ticket channels of ${t.applicant.name} will be deleted.`, "Close ticket")) return;
          if (await act(close, () => gApi("POST", `/tickets/open/${encodeURIComponent(t.id)}/close`), "Ticket closed.") !== undefined) refresh();
        });
        return h("tr", {},
          h("td", {}, t.project),
          h("td", {}, member(t.applicant)),
          h("td", {}, t.approvedBy ? h("span", { class: "badge ok" }, "approved") : h("span", { class: "badge wait" }, "pending")),
          h("td", { title: new Date(t.createdAt).toLocaleString() }, fmtRelative(t.createdAt)),
          h("td", { class: "actions" }, h("a", { class: "btn ghost", href: t.jumpUrl, target: "_blank", rel: "noopener" }, "Open"), " ", close));
      })))) : empty("No open tickets."));
  } catch (e) { openBody.replaceChildren(empty(e.message)); }
}

function projectCard(p) {
  const staff = roleListEditor(p.staffRoles);
  const members = roleListEditor(p.memberRoles);
  const cat = channelSelect("categories", "Default category “Tickets”");
  if (p.category) cat.value = p.category.id;
  const text = h("textarea", { maxlength: "4000", placeholder: "Markdown works. {applicant} = applicant mention, {project} = project name" });
  text.value = p.customText || "";
  const counter = h("span", { class: "muted small" }, `${text.value.length}/4000`);
  text.addEventListener("input", () => { counter.textContent = `${text.value.length}/4000`; });

  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    const res = await act(save, () => gApi("PUT", `/tickets/projects/${encodeURIComponent(p.key)}`, {
      staffRoleIds: staff.value(), memberRoleIds: members.value(), categoryId: cat.value || null, customText: text.value
    }));
    if (res === undefined) return;
    toast(`Project ${p.name} saved.`);
    if (res.warning) toast(res.warning, "warn");
    if (res.ticketsTotal) toast(`Updated ${res.ticketsUpdated} of ${res.ticketsTotal} open tickets.`, res.ticketsUpdated === res.ticketsTotal ? "ok" : "warn");
  });
  const remove = h("button", { class: "btn danger" }, "🗑️ Remove");
  remove.addEventListener("click", async () => {
    if (!await confirmDialog(`Remove ${p.name}?`, "New tickets for this project can't be created. Open tickets stay until they are closed.", "Remove project")) return;
    if (await act(remove, () => gApi("DELETE", `/tickets/projects/${encodeURIComponent(p.key)}`), "Project removed.") !== undefined) refresh();
  });

  return h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h3", {}, p.name), h("span", { class: "badge wait" }, `${p.open} open`)),
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, "Leadership (see tickets, can Approve)"), staff.el),
      h("div", { class: "field" }, h("label", {}, "Roles given on Approve"), members.el)),
    h("div", { class: "field" }, h("label", {}, "Category for ticket channels"), cat),
    h("div", { class: "field" }, h("label", {}, "Ticket text (under the system info, above the buttons)"), text, counter),
    h("div", { class: "row" }, save, remove));
}

// ================= Temp Voice =================

async function pageVoice(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔊 Temp Voice"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));

  // New channel
  const nName = h("input", { placeholder: "➕ Create room", maxlength: "100" });
  const nCat = channelSelect("categories", "No category");
  const nTpl = h("input", { placeholder: "Room name, {user} = member (empty = auto)", maxlength: "100" });
  const nLim = h("input", { type: "number", min: "0", max: "99", placeholder: "Limit (0 = none)", style: "width:140px" });
  const nBtn = h("button", { class: "btn" }, "➕ Create channel");
  nBtn.addEventListener("click", async () => {
    if (await act(nBtn, () => gApi("POST", "/voice/creators/new", {
      name: nName.value, categoryId: nCat.value || null, template: nTpl.value, limit: nLim.value === "" ? null : Number(nLim.value)
    }), "Channel created. Rooms copy its permissions.") !== undefined) refresh();
  });

  // Existing channel
  const eCh = channelSelect("voice", "Choose a voice channel…");
  const eTpl = h("input", { placeholder: "Room name, {user} = member (empty = auto)", maxlength: "100" });
  const eLim = h("input", { type: "number", min: "0", max: "99", placeholder: "Limit (0 = none)", style: "width:140px" });
  const eBtn = h("button", { class: "btn" }, "🔁 Use as 'join to create'");
  eBtn.addEventListener("click", async () => {
    if (!eCh.value) return toast("Choose a voice channel first.", "warn");
    if (await act(eBtn, () => gApi("PUT", `/voice/creators/${eCh.value}`, {
      template: eTpl.value, limit: eLim.value === "" ? null : Number(eLim.value)
    }), "Channel is now 'join to create'.") !== undefined) refresh();
  });

  main.append(
    h("div", { class: "grid2" },
      h("div", { class: "card" }, h("h3", {}, "Create a 'join to create' channel"),
        h("div", { class: "field" }, h("label", {}, "Channel name"), nName),
        h("div", { class: "field" }, h("label", {}, "Category"), nCat),
        h("div", { class: "field" }, h("label", {}, "Room name"), nTpl),
        h("div", { class: "row" }, nLim, nBtn)),
      h("div", { class: "card" }, h("h3", {}, "Use an existing voice channel"),
        h("p", { class: "muted small" }, "Everyone who joins it is moved to a new room — don't pick a channel people normally sit in."),
        h("div", { class: "field" }, h("label", {}, "Voice channel"), eCh),
        h("div", { class: "field" }, h("label", {}, "Room name"), eTpl),
        h("div", { class: "row" }, eLim, eBtn))),
    postPanelCard("Room control panel", "Posts the panel members use to manage their own room.", "/voice/panel"));

  const creatorsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  const roomsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(
    h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "'Join to create' channels")), creatorsBody),
    h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active rooms")), roomsBody));

  try {
    const creators = await gApi("GET", "/voice/creators");
    creatorsBody.replaceChildren(creators.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Channel"), h("th", {}, "Room name"), h("th", {}, "Limit"), h("th", {}))),
      h("tbody", {}, creators.map(creatorRow)))) : empty("None yet."));
  } catch (e) { creatorsBody.replaceChildren(empty(e.message)); }

  try {
    const rooms = await gApi("GET", "/voice/rooms");
    roomsBody.replaceChildren(rooms.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Room"), h("th", {}, "Owner"), h("th", {}, "Inside"), h("th", {}))),
      h("tbody", {}, rooms.map(roomRow)))) : empty("No active rooms."));
  } catch (e) { roomsBody.replaceChildren(empty(e.message)); }
}

function creatorRow(c) {
  const tpl = h("input", { placeholder: "auto", maxlength: "100", value: c.template || "" });
  const lim = h("input", { type: "number", min: "0", max: "99", style: "width:80px", value: c.limit ?? "" });
  const save = h("button", { class: "btn secondary" }, "Save");
  save.addEventListener("click", () => act(save, () => gApi("PUT", `/voice/creators/${c.channelId}`, {
    template: tpl.value, limit: lim.value === "" ? null : Number(lim.value)
  }), "Saved."));
  const rm = h("button", { class: "btn ghost" }, "Remove");
  rm.addEventListener("click", async () => {
    if (!await confirmDialog("Stop using this channel?", "The channel itself is not deleted. Existing rooms keep working until they are empty.", "Remove")) return;
    if (await act(rm, () => gApi("DELETE", `/voice/creators/${c.channelId}`), "Removed.") !== undefined) refresh();
  });
  return h("tr", {},
    h("td", {}, c.exists ? `🔊 ${c.name}` : h("span", { class: "chip warn" }, "deleted channel"), c.category ? h("div", { class: "muted small" }, c.category) : null),
    h("td", {}, tpl), h("td", {}, lim), h("td", { class: "actions" }, save, " ", rm));
}

function roomRow(r) {
  const owner = h("button", { class: "btn secondary" }, "👑 Transfer");
  owner.addEventListener("click", async () => {
    const m = await pickMemberDialog(`New owner of ${r.name}`);
    if (!m) return;
    if (await act(owner, () => gApi("POST", `/voice/rooms/${r.channelId}/owner`, { userId: m.id }), `${m.name} now owns the room.`) !== undefined) refresh();
  });
  const del = h("button", { class: "btn danger" }, "Delete");
  del.addEventListener("click", async () => {
    if (!await confirmDialog(`Delete ${r.name}?`, "Everyone inside is disconnected.", "Delete room")) return;
    if (await act(del, () => gApi("POST", `/voice/rooms/${r.channelId}/delete`), "Room deleted.") !== undefined) refresh();
  });
  return h("tr", {},
    h("td", {}, r.name, " ", r.locked ? h("span", { class: "badge lock" }, "locked") : null,
      h("div", { class: "muted small" }, r.createdFrom ? `from ${r.createdFrom}` : "", r.limit ? ` · limit ${r.limit}` : "")),
    h("td", {}, member(r.owner)),
    h("td", {}, r.inside.length ? r.inside.map(m => h("div", {}, member(m))) : h("span", { class: "muted" }, "nobody")),
    h("td", { class: "actions" }, owner, " ", del));
}

// ================= Panel texts =================

async function pageTexts(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📝 Panel texts"),
    h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted" },
    "Edit the text of the panels the bot posts. Saving updates the panels already posted on the server. ",
    "Panels posted before this feature are unknown to the bot — click “Find posted panels” once to pick them up."));

  const discover = h("button", { class: "btn secondary" }, "🔍 Find posted panels");
  discover.addEventListener("click", async () => {
    const res = await act(discover, () => gApi("POST", "/texts/discover"));
    if (res === undefined) return;
    const total = Object.values(res).reduce((a, b) => a + b, 0);
    toast(total ? `Found ${total} panel message(s).` : "No new panels found (only the last 50 messages of each channel are checked).");
    refresh();
  });
  main.append(h("div", { class: "row", style: "margin-bottom:16px" }, discover));

  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);
  try {
    const panels = await gApi("GET", "/texts");
    holder.replaceChildren(...panels.map(textCard));
  } catch (e) { holder.replaceChildren(empty(e.message)); }
}

function textCard(p) {
  const title = h("input", { maxlength: String(p.maxTitle), placeholder: p.defaults.title, value: p.title || p.defaults.title });
  const desc = h("textarea", { maxlength: String(p.maxDescription), style: "min-height:260px" });
  desc.value = p.description || p.defaults.description;
  const counter = h("span", { class: "muted small" }, `${desc.value.length}/${p.maxDescription}`);
  desc.addEventListener("input", () => { counter.textContent = `${desc.value.length}/${p.maxDescription}`; });

  const labelInputs = p.labels.map(l => {
    const input = h("input", { maxlength: String(l.max), placeholder: l.default, value: l.value || l.default });
    return { l, input, el: h("div", { class: "field" }, h("label", {}, l.name), input) };
  });

  const resultToast = (res, what) => {
    if (!res) return;
    toast(res.panelsTotal
      ? `${what} Updated ${res.panelsUpdated} of ${res.panelsTotal} posted panel(s).`
      : `${what} No posted panels to update yet — post the panel from its module tab.`,
      res.panelsUpdated === res.panelsTotal ? "ok" : "warn");
  };

  const save = h("button", { class: "btn" }, "💾 Save & update panels");
  save.addEventListener("click", async () => {
    const labels = {};
    for (const { l, input } of labelInputs) labels[l.id] = input.value;
    const res = await act(save, () => gApi("PUT", `/texts/${p.key}`, { title: title.value, description: desc.value, labels }));
    resultToast(res, "Saved.");
  });
  const reset = h("button", { class: "btn secondary" }, "↩️ Reset to default");
  reset.addEventListener("click", async () => {
    if (!await confirmDialog(`Reset ${p.name}?`, "The default text is restored and posted panels are updated.", "Reset", false)) return;
    const res = await act(reset, () => gApi("DELETE", `/texts/${p.key}`));
    if (res === undefined) return;
    resultToast(res, "Reset.");
    refresh();
  });

  const custom = p.title || p.description || p.labels.some(l => l.value);
  return h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h2", {}, p.name),
      custom ? h("span", { class: "badge wait" }, "custom") : h("span", { class: "badge ok" }, "default"),
      h("span", { class: "muted small" }, `${p.posted} posted`)),
    h("div", { class: "field" }, h("label", {}, "Title"), title),
    h("div", { class: "field" }, h("label", {}, "Text (Discord markdown)"), desc,
      h("div", { class: "row" }, counter, p.placeholders ? h("span", { class: "muted small" }, `· ${p.placeholders}`) : null)),
    labelInputs.length ? h("div", { class: "grid2" }, labelInputs.map(x => x.el)) : null,
    h("div", { class: "row" }, save, reset));
}

// ================= Access (owner only) =================

async function pageAccess(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔑 Access"),
    h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted" },
    "Who can log in to this panel and which sections they see. Only you (the bot owner) can open this page. ",
    "Changes apply immediately — no re-login needed."));

  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  let data;
  try { data = await api("GET", "/api/access"); } catch (e) { holder.replaceChildren(empty(e.message)); return; }

  const permChecks = (selected) => {
    const boxes = data.permissions.map(p => {
      const cb = h("input", { type: "checkbox", checked: selected.includes(p) });
      return { p, cb, el: h("label", { class: "chip", style: "cursor:pointer" }, cb, PERM_LABELS[p] || p) };
    });
    return { el: h("div", {}, boxes.map(b => b.el)), value: () => boxes.filter(b => b.cb.checked).map(b => b.p) };
  };

  // --- Add person
  let picked = null;
  const who = h("span", { class: "muted" }, "nobody selected");
  const pickBtn = h("button", { class: "btn secondary" }, "👤 Pick a member");
  pickBtn.addEventListener("click", async () => {
    const m = await pickMemberDialog("Give panel access to…");
    if (!m) return;
    picked = m.id;
    idIn.value = "";
    who.replaceChildren(member(m));
  });
  const idIn = h("input", { placeholder: "…or paste a Discord user ID", inputmode: "numeric", style: "width:240px" });
  idIn.addEventListener("input", () => { picked = null; who.replaceChildren(document.createTextNode(idIn.value ? `ID ${idIn.value}` : "nobody selected")); });
  const newPerms = permChecks(["lfg", "tickets", "voice"]);
  const addBtn = h("button", { class: "btn" }, "➕ Give access");
  addBtn.addEventListener("click", async () => {
    const id = picked || idIn.value.trim();
    if (!/^\d{15,21}$/.test(id)) return toast("Pick a member or paste a valid Discord user ID.", "warn");
    const perms = newPerms.value();
    if (!perms.length) return toast("Tick at least one section.", "warn");
    if (await act(addBtn, () => api("PUT", `/api/access/${id}`, { permissions: perms }), "Access granted.") !== undefined) refresh();
  });

  const addCard = h("div", { class: "card" }, h("h3", {}, "Give access"),
    h("div", { class: "row", style: "margin-bottom:10px" }, pickBtn, idIn, who),
    h("div", { class: "field" }, h("label", {}, "Sections"), newPerms.el),
    addBtn);

  // --- List
  const rows = data.grants.map(g => {
    const perms = permChecks(g.permissions);
    const save = h("button", { class: "btn secondary" }, "Save");
    save.addEventListener("click", async () => {
      const value = perms.value();
      if (!value.length) return toast("No sections ticked — use Remove to take away access.", "warn");
      await act(save, () => api("PUT", `/api/access/${g.user.id}`, { permissions: value }), "Saved.");
    });
    const rm = h("button", { class: "btn danger" }, "Remove");
    rm.addEventListener("click", async () => {
      if (!await confirmDialog(`Remove access for ${g.user.name}?`, "They won't be able to use the panel anymore.", "Remove")) return;
      if (await act(rm, () => api("DELETE", `/api/access/${g.user.id}`), "Access removed.") !== undefined) refresh();
    });
    return h("tr", {},
      h("td", {}, member(g.user), h("div", { class: "muted small" }, g.user.id)),
      h("td", {}, perms.el),
      h("td", { class: "muted small" }, new Date(g.grantedAt).toLocaleDateString()),
      h("td", { class: "actions" }, save, " ", rm));
  });

  holder.replaceChildren(addCard,
    h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "People with access")),
      h("div", { class: "table-wrap" }, h("table", { class: "table" },
        h("thead", {}, h("tr", {}, h("th", {}, "Member"), h("th", {}, "Sections"), h("th", {}, "Since"), h("th", {}))),
        h("tbody", {},
          h("tr", {}, h("td", {}, member(data.owner)), h("td", {}, h("span", { class: "badge ok" }, "owner · everything")), h("td", {}), h("td", {})),
          rows)))));
}

boot();
