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

/** A free <dialog>: the second one is used when a dialog opens on top of another (e.g. member picker in a form). */
const dialog = () => {
  const first = document.getElementById("dialog");
  return first.open ? document.getElementById("dialog2") : first;
};

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

function promptDialog(title, initial) {
  return new Promise(resolve => {
    const d = dialog();
    const input = h("input", { value: initial, maxlength: "32", style: "width:100%" });
    const ok = () => { const v = input.value.trim(); d.close(); resolve(v || null); };
    input.addEventListener("keydown", e => { if (e.key === "Enter") ok(); });
    d.replaceChildren(h("h2", {}, title), input,
      h("div", { class: "actions" },
        h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(null); } }, "Cancel"),
        h("button", { class: "btn", onclick: ok }, "Save")));
    d.onclose = () => resolve(null);
    d.showModal();
    input.focus();
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

function roleSelect(placeholder = "Choose a role…", exclude = [], pool = state.roles) {
  return h("select", {},
    h("option", { value: "" }, placeholder),
    pool.filter(r => !exclude.includes(r.id)).map(r => h("option", { value: r.id }, `@${r.name}`)));
}

function channelSelect(kind, placeholder) {
  const list = state.channels[kind] || [];
  return h("select", {},
    h("option", { value: "" }, placeholder),
    list.map(c => h("option", { value: c.id, disabled: kind === "text" && !c.canPost },
      `${kind === "categories" ? "" : "#"}${c.name}${c.category ? `  ·  ${c.category}` : ""}${kind === "text" && !c.canPost ? "  (no access)" : ""}`)));
}

/** Editable list of roles: chips + "add" dropdown. Returns { el, value() }. */
function roleListEditor(initial, pool = state.roles) {
  let ids = initial.map(r => r.id);
  const known = new Map(initial.map(r => [r.id, r]));
  for (const r of pool) known.set(r.id, { ...r, exists: true });
  const el = h("div", {});
  const draw = () => {
    const sel = roleSelect("+ Add role…", ids, pool);
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
// [key, icon, label, required permission] — "member": role «участник» on the selected server; "admin": any admin right
// [key, icon, label, required permission, nav section]
// "member" = member role on the selected server; "admin" = any admin right; "content" = Panel texts or Posts right
const allTabs = [
  ["home", "🏠", "Home", "member", "Member"],
  ["play", "🎮", "Groups", "member", "Member"],
  ["chars", "🧙", "My characters", "member", "Member"],
  ["lfg", "📣", "LFG", "lfg", "Admin · Modules"],
  ["tickets", "🎫", "Tickets", "tickets", "Admin · Modules"],
  ["voice", "🔊", "Temp Voice", "voice", "Admin · Modules"],
  ["content", "📌", "Panels & posts", "content", "Admin · Content"],
  ["overview", "📊", "Dashboard", "admin", "Admin · System"],
  ["log", "📜", "Log", "log", "Admin · System"],
  ["settings", "⚙️", "Settings", "settings", "Admin · System"],
  ["access", "🔑", "Access", "access", "Admin · System"]
];
const currentGuild = () => state.guilds.find(g => g.id === state.guildId);
const isAdmin = () => !!(state.me && state.me.isAdmin);
const can = perm => {
  if (!perm) return true;
  if (!state.me) return false;
  if (perm === "member") return !!(currentGuild() && currentGuild().isMember);
  if (perm === "admin") return isAdmin();
  if (perm === "content") return state.me.permissions.includes("texts") || state.me.permissions.includes("posts");
  return state.me.permissions.includes(perm);
};
const MEMBER_TABS = ["home", "play", "chars"];
const visibleTabs = () => allTabs.filter(t => can(t[3]));
const PERM_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Temp Voice", texts: "📝 Panel texts", posts: "📌 Posts", log: "📜 Log", settings: "⚙️ Settings" };

function renderLogin(message) {
  const params = new URLSearchParams(location.search);
  const failed = params.get("login") === "failed";
  if (failed) history.replaceState(null, "", "/");
  document.getElementById("app").replaceChildren(
    h("div", { class: "card login" },
      h("img", { class: "logo-img", src: "/favicon.png", alt: "" }),
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
      h("p", {}, `You are logged in as ${me.name}, but this account has no access: you need the member role on the server or admin rights.`),
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
  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = (visibleTabs()[0] || ["overview"])[0];
  await selectGuild(state.guildId);
}

async function selectGuild(id) {
  state.guildId = id;
  if (id) store.set("gb.guild", id);
  state.roles = [];
  state.channels = { text: [], voice: [], categories: [] };
  if (id && isAdmin()) {
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

  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = (visibleTabs()[0] || ["overview"])[0];
  const navButton = ([key, icon, label]) =>
    h("button", { class: state.tab === key ? "active" : "", onclick: () => { state.tab = key; store.set("gb.tab", key); renderShell(); } }, icon, " ", label);
  const navItems = [];
  let section = null;
  for (const t of visibleTabs()) {
    if (t[4] !== section) { section = t[4]; navItems.push(h("div", { class: "nav-section" }, section)); }
    navItems.push(navButton(t));
  }
  const nav = h("nav", { class: "nav" }, navItems);

  const main = h("main", { class: "main" });

  document.getElementById("app").replaceChildren(
    h("header", { class: "topbar" },
      h("div", { class: "brand" }, h("span", { class: "brand-logo" },
        h("img", { src: "/favicon.png", alt: "" }), h("span", { class: `dot ${connected ? "ok" : "bad"}`, title: `Bot: ${state.connection || "unknown"}` })),
        isAdmin() ? "Greatness Bot · Admin" : "Greatness Bot"),
      state.guilds.length ? guildSel : h("span", { class: "muted" }, "The bot is on no servers"),
      h("div", { class: "user" },
        state.me.avatarUrl ? h("img", { class: "avatar", src: state.me.avatarUrl, alt: "" }) : null,
        h("span", {}, state.me.name),
        h("button", { class: "btn ghost", onclick: logout, title: "Log out" }, "Log out"))),
    h("div", { class: "layout" }, nav, main));

  if (!state.guildId) { main.append(empty("No servers available for your account.")); return; }
  if (!visibleTabs().length) { main.append(empty("You have no sections on this server. Ask an admin for the member role.")); return; }
  const page = { home: pageHome, content: pageContent, play: pagePlay, chars: pageChars, overview: pageOverview, lfg: pageLfg, tickets: pageTickets, voice: pageVoice, texts: pageTexts, posts: pagePosts, log: pageLog, settings: pageSettings, access: pageAccess }[state.tab];
  page(main);
  addGuideButton(main, state.tab);
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

  // Projects (games): activities and classes can belong to a project
  let lfgProjects = { projects: [], panels: [] };
  try { lfgProjects = await gApi("GET", "/lfg/projects"); } catch { /* shown in the card */ }
  const projOptions = (sel, value) => {
    const el = h("select", {}, h("option", { value: "" }, "🌐 All projects"), lfgProjects.projects.map(p => h("option", { value: p.key }, `🎮 ${p.name}`)));
    el.value = value || "";
    return el;
  };

  // --- Activities
  const actCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Activities")),
    h("p", { class: "muted small" }, "Members with the role are pinged and get a DM when a group for the activity is posted. ",
      "An activity of a project is offered only in that project's groups; «All projects» — everywhere."));
  const actBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  const nameIn = h("input", { placeholder: "Activity name, e.g. Fire Temple", maxlength: "100" });
  const roleIn = roleSelect("Role to ping…");
  const projIn = lfgProjects.projects.length ? projOptions() : null;
  const addBtn = h("button", { class: "btn" }, "➕ Add");
  addBtn.addEventListener("click", async () => {
    const ok = await act(addBtn, () => gApi("POST", "/lfg/activities",
      { name: nameIn.value, roleId: roleIn.value, projectKey: projIn ? projIn.value || null : null }), "Activity added.");
    if (ok !== undefined) refresh();
  });
  actCard.append(actBody, h("div", { class: "row", style: "margin-top:12px" }, h("div", { class: "grow" }, nameIn), roleIn, projIn, addBtn));

  // Channel for groups created by members on the site
  const chCard = h("div", { class: "card" }, h("h3", {}, "Channel for groups"),
    h("p", { class: "muted small" }, "Groups that members create on the site (Member → Groups) are always posted here — members can't pick the channel."));
  const chSel = channelSelect("text", "Not set — use the LFG panel channel");
  const chSave = h("button", { class: "btn" }, "💾 Save");
  const chInfo = h("div", { class: "muted small", style: "margin-top:6px" });
  chSave.addEventListener("click", async () => {
    if (await act(chSave, () => gApi("PUT", "/lfg/channel", { channelId: chSel.value || null }), "Channel saved.") !== undefined) refresh();
  });
  chCard.append(h("div", { class: "row" }, h("div", { class: "grow" }, chSel), chSave), chInfo);
  gApi("GET", "/lfg/channel").then(c => {
    if (c.channelId) chSel.value = c.channelId;
    chInfo.textContent = c.effective
      ? `Now: #${c.effective.name}${c.fromPanel ? " (the LFG panel's channel)" : ""}`
      : "Now: not set — members can't create groups until you pick a channel or post the LFG panel.";
  }).catch(e => { chInfo.textContent = e.message; });

  main.append(h("div", { class: "grid-main" }, actCard,
    h("div", {}, postPanelCard("LFG panel", "Posts the message with the “Create LFG” button.", "/lfg/panel"), chCard)));

  // --- Schedules (creating one-off groups is in Member → Groups)
  const recCard = h("div", { class: "card" });
  main.append(recCard);

  // --- Groups
  const groupsCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active groups")));
  const groupsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  groupsCard.append(groupsBody);
  main.append(groupsCard);

  let acts = [];
  try {
    acts = await gApi("GET", "/lfg/activities");
    actBody.replaceChildren(acts.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "Ping role"), lfgProjects.projects.length ? h("th", {}, "Project") : null, h("th", {}))),
      h("tbody", {}, acts.map(a => {
        const proj = lfgProjects.projects.length ? projOptions(null, a.projectKey) : null;
        if (proj) proj.addEventListener("change", () => act(proj,
          () => gApi("PUT", `/lfg/activities/${encodeURIComponent(a.key)}/project`, { projectKey: proj.value || null }), "Activity moved."));
        const rm = h("button", { class: "btn ghost" }, "Remove");
        rm.addEventListener("click", async () => {
          if (!await confirmDialog(`Remove ${a.name}?`, "Groups that are already posted are not affected.", "Remove")) return;
          if (await act(rm, () => gApi("DELETE", `/lfg/activities/${encodeURIComponent(a.key)}`), "Activity removed.") !== undefined) refresh();
        });
        return h("tr", {}, h("td", {}, a.name), h("td", {}, roleChip(a.role)), proj ? h("td", {}, proj) : null, h("td", { class: "actions" }, rm));
      })))) : empty("No activities yet."));
  } catch (e) { actBody.replaceChildren(empty(e.message)); }

  // Schedules
  let templates = [];
  try { templates = await gApi("GET", "/lfg/templates"); } catch { /* shown in the card */ }
  const projCard = h("div", { class: "card" });
  const classCard = h("div", { class: "card" });
  main.insertBefore(projCard, recCard);
  main.insertBefore(classCard, recCard);
  const lfgSettingsCard = h("div", { class: "card" });
  main.insertBefore(lfgSettingsCard, projCard);
  renderLfgSettings(lfgSettingsCard);
  renderProjects(projCard, lfgProjects);
  renderClasses(classCard, lfgProjects.projects);
  renderRecurring(recCard, acts, templates, lfgProjects.projects);
  const tplCard = h("div", { class: "card" });
  const statsCard = h("div", { class: "card" });
  main.insertBefore(tplCard, recCard);
  main.append(statsCard);
  renderTemplates(tplCard, acts, templates);
  renderStats(statsCard);

  try {
    const groups = await gApi("GET", "/lfg/groups");
    groupsBody.replaceChildren(groups.length ? h("div", { class: "grid2" }, groups.map(groupCard)) : empty("No active groups."));
  } catch (e) { groupsBody.replaceChildren(empty(e.message)); }
}

const SLOT_ROLES = [["Any", "🎲 All"], ["Dps", "⚔️ DD"], ["Tank", "🛡️ Tank"], ["Healer", "💚 Sup"]];
const DAYS = [[1, "Mon"], [2, "Tue"], [3, "Wed"], [4, "Thu"], [5, "Fri"], [6, "Sat"], [0, "Sun"]];

/** LFG form: channel, activity, slots + (once) date/time or (recurring) days/time/post-before. */
function lfgForm(acts, opts) {
  const v = { ...(opts.initial || {}) };
  const tplMode = opts.mode === "template";
  // Members don't choose the channel: the admin sets it (LFG → Channel for groups)
  const channel = opts.fixedChannel
    ? { value: opts.fixedChannel.id, el: h("div", { class: "muted", style: "padding:7px 0" }, `#${opts.fixedChannel.name}`) }
    : channelSelect("text", "Channel…");
  const channelEl = channel.el || channel;
  if (v.channelId) channel.value = v.channelId;

  // Project (game): characters of this project are used; members need its role
  const projectSel = opts.projects && opts.projects.length
    ? h("select", {}, opts.projects.map(p => h("option", { value: p.key }, p.name)))
    : null;
  if (projectSel) {
    projectSel.value = v.projectKey || (opts.projects.find(p => p.isDefault) || opts.projects[0]).key;
    fillActivities(projectSel.value);
    if (!v.activityKey && !v.activity) {
      const first = [...activity.options].find(o => o.value);
      if (first) activity.value = first.value;
    }
    projectSel.addEventListener("change", () => { fillActivities(projectSel.value); syncCustom(); });
    if (opts.fixedChannel) {
      // The post goes to the channel of the project's LFG panel
      const syncChannel = () => {
        const p = opts.projects.find(x => x.key === projectSel.value);
        const c = (p && p.channel) || opts.fixedChannel;
        channel.value = c ? c.id : "";
        channel.el.textContent = c ? `#${c.name}` : "not set — ask an admin";
      };
      projectSel.addEventListener("change", syncChannel);
      syncChannel();
    }
  }
  // Admins: duplicate the post to other channels
  const mirrorBoxes = (opts.mirrorChannels || []).map(c => {
    const cb = h("input", { type: "checkbox" });
    return { id: c.id, cb, el: h("label", { class: "chip", style: "cursor:pointer" }, cb, `#${c.name}`) };
  });
  const voice = h("input", { type: "checkbox", checked: !!v.createVoice });
  const nameIn = h("input", { placeholder: "Template name, e.g. Dungeon 4: Tank/Sup/DD/DD", maxlength: "80", value: v.name || "" });

  const activity = h("select", {});
  // Only activities of the chosen project (+ the ones for all projects)
  const fillActivities = projectKey => {
    const keep = activity.value;
    activity.replaceChildren(h("option", { value: "" }, "✏️ Custom activity (no ping)"),
      ...acts.filter(a => !projectKey || !a.projectKey || a.projectKey === projectKey)
        .map(a => h("option", { value: a.key }, `${a.name}  ·  @${a.role.name}`)));
    if ([...activity.options].some(o => o.value === keep)) activity.value = keep;
  };
  fillActivities(null);
  const custom = h("input", { placeholder: "Activity name", maxlength: "100" });
  if (v.activityKey !== undefined) {
    activity.value = v.activityKey || "";
    custom.value = v.customActivity || "";
  } else if (v.activity) {
    const match = acts.find(a => a.name === v.activity);
    if (match) activity.value = match.key; else custom.value = v.activity;
  } else if (acts.length) activity.value = acts[0].key;
  const syncCustom = () => { custom.style.display = activity.value ? "none" : ""; };
  activity.addEventListener("change", syncCustom);
  syncCustom();

  // Slots
  let slots = v.slots ? v.slots.slice() : ["Any", "Any", "Any", "Any"];
  const size = h("select", {}, Array.from({ length: 24 }, (_, i) => i + 2).map(n => h("option", { value: n }, `${n} slots`)));
  const slotBox = h("div", { class: "row", style: "gap:6px" });
  const drawSlots = () => {
    size.value = String(slots.length);
    slotBox.replaceChildren(...slots.map((r, i) => {
      const sel = h("select", { title: `Slot ${i + 1}` }, SLOT_ROLES.map(([val, label]) => h("option", { value: val }, `${i + 1}. ${label}`)));
      sel.value = r;
      sel.addEventListener("change", () => { slots[i] = sel.value; });
      return sel;
    }));
  };
  size.addEventListener("change", () => {
    const n = Number(size.value);
    slots = slots.slice(0, n);
    while (slots.length < n) slots.push("Any");
    drawSlots();
  });
  drawSlots();

  // Requirements: one of these roles, minimum character GS — others go to the reserve
  const rolePool = opts.roles || state.roles;
  let req = roleListEditor(v.requiredRoles || [], rolePool);
  const reqBox = h("div", {}, req.el);
  const minGs = h("input", { type: "number", min: "0", max: "99999", placeholder: "none", value: v.minGs ?? "", style: "width:110px" });

  // Templates: prefill activity, slots and requirements
  const tplSel = opts.templates && opts.templates.length
    ? h("select", {}, h("option", { value: "" }, "— no template —"), opts.templates.map(t => h("option", { value: t.key }, t.name)))
    : null;
  if (tplSel) tplSel.addEventListener("change", () => {
    const t = opts.templates.find(x => x.key === tplSel.value);
    if (!t) return;
    activity.value = t.activityKey || "";
    custom.value = t.customActivity || "";
    syncCustom();
    slots = t.slots.slice();
    drawSlots();
    req = roleListEditor(t.requiredRoles || [], rolePool);
    reqBox.replaceChildren(req.el);
    minGs.value = t.minGs ?? "";
  });

  const dm = h("input", { type: "checkbox", checked: v.notifyByDm ?? true });
  const seat = h("input", { type: "checkbox", checked: v.seatOwner ?? true });
  const seatLabel = opts.hideSeat ? null : h("label", { class: "row" }, seat, "Organizer takes slot 1");

  const fields = [
    projectSel ? h("div", { class: "field" }, h("label", {}, "🎮 Project (game)"), projectSel) : null,
    tplSel ? h("div", { class: "field" }, h("label", {}, "📋 Template"), tplSel) : null,
    h("div", { class: "grid2" },
      tplMode ? h("div", { class: "field" }, h("label", {}, "Template name"), nameIn)
        : h("div", { class: "field" }, h("label", {}, opts.fixedChannel ? "Posted in" : "Channel"), channelEl),
      h("div", { class: "field" }, h("label", {}, "Activity"), activity, custom)),
    h("div", { class: "field" }, h("label", {}, "Slots"), h("div", { class: "row", style: "margin-bottom:6px" }, size), slotBox),
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, "Required role (any of)"), reqBox),
      h("div", { class: "field" }, h("label", {}, "Minimum GS"), minGs,
        h("span", { class: "muted small" }, "Players without the role / GS go to the reserve; the organizer can still move them in.")))
  ];

  let extra;
  if (tplMode) {
    extra = () => ({ name: nameIn.value, customActivity: activity.value ? null : custom.value });
  } else if (opts.mode === "once") {
    const date = h("input", { type: "date" });
    const time = h("input", { type: "time" });
    fields.push(h("div", { class: "field" }, h("label", {}, "Start (server time zone, leave empty for no time)"),
      h("div", { class: "row" }, date, time)));
    extra = () => ({ date: date.value || null, time: time.value || null });
  } else {
    const days = DAYS.map(([d, label]) => {
      const cb = h("input", { type: "checkbox", checked: (v.days || []).includes(d) });
      return { d, cb, el: h("label", { class: "chip", style: "cursor:pointer" }, cb, label) };
    });
    const time = h("input", { type: "time", value: v.time || "20:00" });
    const before = h("input", { type: "number", min: "0", max: "168", style: "width:90px", value: v.postHoursBefore ?? 24 });
    const enabled = h("input", { type: "checkbox", checked: v.enabled ?? true });
    let ownerId = v.owner ? v.owner.id : null;
    const ownerView = h("span", {}, v.owner ? member(v.owner) : "you");
    const ownerBtn = h("button", { class: "btn ghost", type: "button" }, "Change");
    ownerBtn.addEventListener("click", async () => {
      const m = await pickMemberDialog("Organizer of the scheduled groups");
      if (m) { ownerId = m.id; ownerView.replaceChildren(member(m)); }
    });
    fields.push(
      h("div", { class: "field" }, h("label", {}, "Days"), h("div", {}, days.map(x => x.el))),
      h("div", { class: "row", style: "margin-bottom:12px" },
        h("span", { class: "muted" }, "Starts at"), time, h("span", { class: "muted" }, "· post"), before, h("span", { class: "muted" }, "hours before")),
      h("div", { class: "row", style: "margin-bottom:12px" }, h("span", { class: "muted" }, "Organizer:"), ownerView, ownerBtn),
      h("label", { class: "row", style: "margin-bottom:8px" }, enabled, "Enabled"));
    extra = () => ({
      days: days.filter(x => x.cb.checked).map(x => x.d), time: time.value, postHoursBefore: Number(before.value || 0),
      enabled: enabled.checked, ownerId
    });
  }

  if (!tplMode) fields.push(h("div", { class: "row", style: "margin-bottom:12px" },
    h("label", { class: "row" }, dm, "DM members with the activity role"),
    opts.mode === "once" ? h("label", { class: "row" }, voice, "🔊 Create a voice room (everyone listens, the group speaks)") : null,
    seatLabel));
  if (mirrorBoxes.length) fields.push(h("details", { style: "margin-bottom:12px" },
    h("summary", { class: "muted" }, "📢 Also post in other channels (optional)"),
    h("div", { style: "margin-top:6px" }, mirrorBoxes.map(x => x.el))));

  return {
    el: h("div", {}, fields),
    value: () => ({
      channelId: channel.value || null, activityKey: activity.value || null, customName: activity.value ? null : custom.value,
      slots: slots.slice(), notifyByDm: dm.checked, seatOwner: seat.checked,
      requiredRoleIds: req.value(), minGs: minGs.value === "" ? null : Number(minGs.value),
      projectKey: projectSel ? projectSel.value : null, createVoice: voice.checked,
      mirrorChannelIds: mirrorBoxes.filter(x => x.cb.checked).map(x => x.id), ...extra()
    })
  };
}

async function renderRecurring(card, acts, templates = [], projects = []) {
  const add = h("button", { class: "btn" }, "➕ Add schedule");
  add.addEventListener("click", () => recurringDialog(acts, null, templates, projects));
  const body = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "🔁 Recurring groups"), add),
    h("p", { class: "muted small" }, "Posted automatically on the chosen days, the set number of hours before the start."), body);

  let list;
  try { list = await gApi("GET", "/lfg/recurring"); } catch (e) { body.replaceChildren(empty(e.message)); return; }
  if (!list.length) { body.replaceChildren(empty("No schedules yet.")); return; }

  const dayNames = d => DAYS.filter(([n]) => d.includes(n)).map(([, l]) => l).join(", ");
  body.replaceChildren(h("div", { class: "table-wrap" }, h("table", { class: "table" },
    h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "When"), h("th", {}, "Next post"), h("th", {}, "Status"), h("th", {}))),
    h("tbody", {}, list.map(r => {
      const edit = h("button", { class: "btn secondary" }, "Edit");
      edit.addEventListener("click", () => recurringDialog(acts, r, templates, projects));
      const rm = h("button", { class: "btn ghost" }, "Delete");
      rm.addEventListener("click", async () => {
        if (!await confirmDialog(`Delete the ${r.activity} schedule?`, "Groups that are already posted stay.", "Delete")) return;
        if (await act(rm, () => gApi("DELETE", `/lfg/recurring/${r.id}`), "Schedule deleted.") !== undefined) refresh();
      });
      return h("tr", {},
        h("td", {}, r.activity, h("div", { class: "muted small" }, r.channel ? `#${r.channel}` : "deleted channel", ` · ${r.slots.length} slots`)),
        h("td", {}, `${dayNames(r.days)} · ${r.time}`, h("div", { class: "muted small" }, `post ${r.postHoursBefore} h before`)),
        h("td", { class: "small" }, r.enabled && r.nextPost ? fmtDate(r.nextPost) : "—",
          r.nextStart ? h("div", { class: "muted small" }, `start ${fmtDate(r.nextStart)}`) : null),
        h("td", {}, r.enabled ? h("span", { class: "badge ok" }, "on") : h("span", { class: "badge lock" }, "off"),
          r.lastError ? h("div", { class: "error-text small", title: r.lastError }, "last post failed") : null),
        h("td", { class: "actions" }, edit, " ", rm));
    })))));
}

function recurringDialog(acts, r, templates = [], projects = []) {
  const d = dialog();
  const form = lfgForm(acts, { mode: "recurring", initial: r || {}, templates, projects });
  const save = h("button", { class: "btn" }, r ? "💾 Save" : "➕ Add");
  save.addEventListener("click", async () => {
    const res = await act(save, () => r ? gApi("PUT", `/lfg/recurring/${r.id}`, form.value()) : gApi("POST", "/lfg/recurring", form.value()),
      r ? "Schedule saved." : "Schedule added.");
    if (res !== undefined) { d.close(); refresh(); }
  });
  d.classList.add("wide");
  d.replaceChildren(h("h2", {}, r ? `Edit: ${r.activity}` : "New recurring group"), form.el,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.onclose = () => d.classList.remove("wide");
  d.showModal();
}

/** LFG settings: time zone, auto-delete, reminder (moved from Settings). */
async function renderLfgSettings(card) {
  card.replaceChildren(h("h2", {}, "⚙️ LFG settings"), h("div", { class: "muted" }, "Loading…"));
  let s;
  try { s = await gApi("GET", "/lfg/settings"); } catch (e) { card.replaceChildren(h("h2", {}, "⚙️ LFG settings"), empty(e.message)); return; }

  const zones = h("datalist", { id: "zones" }, s.zones.map(z => h("option", { value: z })));
  const zone = h("input", { list: "zones", placeholder: s.defaults.timeZone, value: s.timeZone || "", style: "width:260px" });
  const hours = h("input", { type: "number", min: "1", max: "72", placeholder: String(s.defaults.autoDeleteHours), value: s.autoDeleteHours ?? "", style: "width:120px" });
  const reminder = h("input", { type: "number", min: "0", max: "180", placeholder: String(s.defaults.reminderMinutes), value: s.reminderMinutes ?? "", style: "width:120px" });
  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    const res = await act(save, () => gApi("PUT", "/lfg/settings", {
      timeZone: zone.value.trim() || null,
      autoDeleteHours: hours.value === "" ? null : Number(hours.value),
      reminderMinutes: reminder.value === "" ? null : Number(reminder.value)
    }));
    if (res === undefined) return;
    toast(`Saved: ${res.timeZone}, auto-delete ${res.autoDeleteHours} h, reminder ${res.reminderMinutes ? res.reminderMinutes + " min" : "off"}.`);
    refresh();
  });

  card.replaceChildren(h("h2", {}, "⚙️ LFG settings"),
    h("p", { class: "muted small" }, "Empty field = the value from appsettings.json (shown as the placeholder). Changes apply immediately."),
    zones,
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, "Server time zone"), zone,
        h("span", { class: "muted small" }, `Now: ${s.current.timeZone}. Used for start times and recurring groups (IANA name, e.g. Europe/Kyiv).`)),
      h("div", {},
        h("div", { class: "field" }, h("label", {}, "Delete LFG posts after (hours)"), hours,
          h("span", { class: "muted small" }, `Now: ${s.current.autoDeleteHours} h after the start (or creation if no time). Applies to new groups.`)),
        h("div", { class: "field" }, h("label", {}, "Reminder before the start (minutes, 0 = off)"), reminder,
          h("span", { class: "muted small" }, `Now: ${s.current.reminderMinutes ? s.current.reminderMinutes + " min" : "off"}.`)))),
    save);
}

// ----- LFG projects (games) & classes (admin) -----

function renderProjects(card, data) {
  const add = h("button", { class: "btn" }, "➕ Add project");
  add.addEventListener("click", () => projectDialog(null));
  const panelRows = data.panels.map(p => {
    const sel = h("select", {}, h("option", { value: "" }, "Default project"), data.projects.map(x => h("option", { value: x.key }, x.name)));
    sel.value = p.projectKey || "";
    sel.addEventListener("change", () => act(sel, () => gApi("PUT", `/lfg/panels/${p.messageId}/project`, { projectKey: sel.value || null }),
      "Panel linked."));
    return h("tr", {}, h("td", {}, p.channel ? `#${p.channel}` : h("span", { class: "chip warn" }, "deleted channel")),
      h("td", {}, sel), h("td", { class: "actions" }, h("a", { class: "btn ghost", href: p.jumpUrl, target: "_blank", rel: "noopener" }, "Open")));
  });
  card.replaceChildren(
    h("div", { class: "card-head" }, h("h2", {}, "🎮 Projects (games)"), add),
    h("p", { class: "muted small" }, "Each project has its own characters. Members need one of the project's roles to add or pick its characters and to sign up for its groups. ",
      "Characters created before projects belong to the default project."),
    data.projects.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" }, h("tbody", {}, data.projects.map(p => {
      const edit = h("button", { class: "btn secondary", onclick: () => projectDialog(p) }, "Edit");
      const rm = h("button", { class: "btn ghost" }, "Delete");
      rm.addEventListener("click", async () => {
        if (!await confirmDialog(`Delete the project ${p.name}?`, "Its groups and characters stay in the data, but the project can't be chosen anymore.", "Delete")) return;
        if (await act(rm, () => gApi("DELETE", `/lfg/projects/${p.key}`), "Project deleted.") !== undefined) refresh();
      });
      return h("tr", {}, h("td", {}, h("b", {}, p.name), p.isDefault ? h("span", { class: "badge ok", style: "margin-left:6px" }, "default") : null),
        h("td", {}, p.roles.length ? p.roles.map(r => roleChip(r)) : h("span", { class: "muted small" }, "open to everyone")),
        h("td", { class: "actions" }, edit, " ", rm));
    })))) : empty("No projects yet — everything uses one shared character list."),
    h("h3", { style: "margin-top:14px" }, "LFG panels → project"),
    h("p", { class: "muted small" }, "Groups created with «Create LFG» on a panel belong to its project. Older panels: use Find posted panels (Panels & posts) first."),
    data.panels.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" }, h("tbody", {}, panelRows))) : empty("No known LFG panels."));
}

function projectDialog(p) {
  const d = dialog();
  const name = h("input", { maxlength: "60", placeholder: "e.g. Aion 2", value: p ? p.name : "" });
  const roles = roleListEditor(p ? p.roles : []);
  const def = h("input", { type: "checkbox", checked: p ? p.isDefault : false });
  const save = h("button", { class: "btn" }, p ? "💾 Save" : "➕ Add");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("PUT", `/lfg/projects/${p ? p.key : "new"}`, { name: name.value, roleIds: roles.value(), isDefault: def.checked }),
      "Project saved.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, p ? `Edit project: ${p.name}` : "New project"),
    h("div", { class: "field" }, h("label", {}, "Name"), name),
    h("div", { class: "field" }, h("label", {}, "Required role (any of) — empty = everyone"), roles.el),
    h("label", { class: "row", style: "margin-bottom:6px" }, def, "Default project (older characters and panels without a project)"),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
}

async function renderClasses(card, projectList = [], projectKey = store.get("gb.classProject") || "") {
  if (projectKey && !projectList.some(p => p.key === projectKey)) projectKey = "";
  card.replaceChildren(h("h2", {}, "🎭 Classes"), h("div", { class: "muted" }, "Loading…"));
  let data;
  const q = projectKey ? `?project=${encodeURIComponent(projectKey)}` : "";
  try { data = await gApi("GET", `/lfg/classes${q}`); } catch (e) { card.replaceChildren(h("h2", {}, "🎭 Classes"), empty(e.message)); return; }
  // Common classes + each project's own (a project without its own uses the common list)
  const tabs = projectList.length ? h("div", { class: "row", style: "margin-bottom:10px" },
    [["", "🌐 Common"], ...projectList.map(p => [p.key, `🎮 ${p.name}`])].map(([k, label]) =>
      h("button", { class: `btn secondary${k === projectKey ? " active-tab" : ""}`,
        onclick: () => { store.set("gb.classProject", k); renderClasses(card, projectList, k); } }, label))) : null;
  const roleIcons = data.roleIcons || {};
  let rows = data.classes.map(c => ({ roleId: c.role.id, role: c.role, icon: c.icon }));
  const list = h("div", {});
  const preview = icon => classMark({ classIcon: icon }) || h("span", { class: "muted small" }, "—");
  // The role's own icon (Discord role settings → Role icon)
  const roleIconEl = ri => ri.url ? h("img", { src: ri.url, alt: "", style: "width:18px;height:18px" }) : h("span", {}, ri.emoji);
  // Role icon → class icon: an emoji is used as is, an image is uploaded as the bot's emoji
  const importIcons = async (btn, targets) => {
    if (!targets.length) { toast("These roles have no role icon in Discord.", "warn"); return; }
    const res = await act(btn, () => gApi("POST", "/lfg/classes/role-icons", { roleIds: targets.map(r => r.roleId) }));
    if (!res) return;
    let n = 0;
    for (const r of targets) if (res.icons[r.roleId]) { r.icon = res.icons[r.roleId]; n++; }
    draw();
    toast(n ? `Icons taken from ${n} role(s) — press Save classes.` : "Nothing imported.", n ? "ok" : "warn");
    if (res.skipped.length) toast(`Skipped: ${res.skipped.join("; ")}`, "warn");
  };
  const draw = () => {
    list.replaceChildren(...rows.map((r, i) => {
      const icon = h("input", { value: r.icon || "", placeholder: "emoji or <:name:id>", style: "width:180px" });
      const prev = h("span", {}, preview(r.icon));
      icon.addEventListener("input", () => { r.icon = icon.value; prev.replaceChildren(preview(icon.value)); });
      const pick = h("select", {}, h("option", { value: "" }, "pick emoji…"),
        data.emojis.map(e => h("option", { value: e.code }, `:${e.name}:${e.app ? "  (bot)" : ""}`)));
      pick.addEventListener("change", () => { if (pick.value) { r.icon = icon.value = pick.value; prev.replaceChildren(preview(pick.value)); } });
      const ri = roleIcons[r.roleId];
      const useRole = ri ? h("button", { class: "btn secondary", title: "Use this role's icon from Discord" }, roleIconEl(ri), "Role icon") : null;
      if (useRole) useRole.addEventListener("click", () => importIcons(useRole, [r]));
      return h("div", { class: "row question-row" }, roleChip(r.role), prev, icon, pick, useRole,
        h("button", { class: "btn ghost", onclick: () => { rows.splice(i, 1); draw(); } }, "✖"));
    }));
  };
  const addSel = roleSelect("+ Add class role…");
  addSel.addEventListener("change", () => {
    const role = state.roles.find(r => r.id === addSel.value);
    if (role && !rows.some(r => r.roleId === role.id)) {
      const ri = roleIcons[role.id];
      rows.push({ roleId: role.id, role: { ...role, exists: true }, icon: ri && ri.emoji ? ri.emoji : "" });
    }
    addSel.value = "";
    draw();
  });
  const fromRoles = h("button", { class: "btn secondary", title: "Fill empty icons with the roles' own icons from Discord (if none are empty — all)" }, "🎨 Use role icons");
  // Empty icons first; if none are empty — all rows whose role has an icon
  fromRoles.addEventListener("click", () => {
    const withIcon = rows.filter(r => roleIcons[r.roleId]);
    const emptyOnes = withIcon.filter(r => !r.icon);
    importIcons(fromRoles, emptyOnes.length ? emptyOnes : withIcon);
  });
  const save = h("button", { class: "btn" }, "💾 Save classes");
  save.addEventListener("click", () => act(save, () => gApi("PUT", `/lfg/classes${q}`, { classes: rows.map(r => ({ roleId: r.roleId, icon: r.icon })) }),
    "Classes saved. New signups use the icons; existing posts update when someone changes their class."));
  draw();
  card.replaceChildren(h("h2", {}, "🎭 Classes"), tabs,
    h("p", { class: "muted small" }, "Roles that count as classes and their icon shown next to the player's name. If the list is not empty, players pick any of these as their class (they don't need the role); if empty — only roles they have. ",
      projectKey ? "This project's own classes — if empty, the common list is used." : "Common classes — used by every project that has no classes of its own. ",
      "Use a unicode emoji, a server emoji, or 🎨 Use role icons — the roles' own icons from Discord (image icons are uploaded as the bot's emojis, no server emoji slots used)."),
    list, h("div", { class: "row", style: "margin-top:8px" }, addSel, fromRoles, save));
}

// ----- LFG templates (admin) -----

function renderTemplates(card, acts, templates) {
  const add = h("button", { class: "btn" }, "➕ Add template");
  add.addEventListener("click", () => templateDialog(acts, null));
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "📋 Templates"), add),
    h("p", { class: "muted small" }, "Activity + slots + requirements in one click — offered in /lfg (Step 1) and in Create a group on the site."),
    templates.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("tbody", {}, templates.map(t => {
        const act = acts.find(a => a.key === t.activityKey);
        const edit = h("button", { class: "btn secondary", onclick: () => templateDialog(acts, t) }, "Edit");
        const rm = h("button", { class: "btn ghost" }, "Delete");
        rm.addEventListener("click", async () => {
          if (!await confirmDialog(`Delete the template ${t.name}?`, "Posted groups are not affected.", "Delete")) return;
          if (await act(rm, () => gApi("DELETE", `/lfg/templates/${t.key}`), "Template deleted.") !== undefined) refresh();
        });
        return h("tr", {},
          h("td", {}, h("b", {}, t.name), h("div", { class: "muted small" }, act ? act.name : (t.customActivity || "custom"))),
          h("td", { class: "small" }, t.slots.map(x => (SLOT_ROLES.find(r => r[0] === x) || [x, x])[1]).join(" · ")),
          h("td", { class: "small" }, t.requiredRoles.map(r => roleChip(r)), t.minGs ? h("span", { class: "chip" }, `GS ${t.minGs}+`) : null),
          h("td", { class: "actions" }, edit, " ", rm));
      })))) : empty("No templates yet."));
}

function templateDialog(acts, t) {
  const d = dialog();
  const form = lfgForm(acts, { mode: "template", initial: t || {} });
  const save = h("button", { class: "btn" }, t ? "💾 Save" : "➕ Add");
  save.addEventListener("click", async () => {
    const v = form.value();
    const res = await act(save, () => gApi("PUT", `/lfg/templates/${t ? t.key : "new"}`, {
      name: v.name, activityKey: v.activityKey, customActivity: v.customActivity, slots: v.slots,
      requiredRoleIds: v.requiredRoleIds, minGs: v.minGs
    }), "Template saved.");
    if (res !== undefined) { d.close(); refresh(); }
  });
  d.classList.add("wide");
  d.replaceChildren(h("h2", {}, t ? `Edit template: ${t.name}` : "New template"), form.el,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.onclose = () => d.classList.remove("wide");
  d.showModal();
}

// ----- LFG statistics (admin) -----

async function renderStats(card) {
  const period = h("select", {}, [[7, "Last 7 days"], [30, "Last 30 days"], [90, "Last 90 days"], [0, "All time"]]
    .map(([v, l]) => h("option", { value: v, selected: v === 30 }, l)));
  const body = h("div", {});
  const load = async () => {
    body.replaceChildren(h("div", { class: "muted" }, "Loading…"));
    let st;
    try { st = await gApi("GET", `/lfg/stats?days=${period.value}`); } catch (e) { body.replaceChildren(empty(e.message)); return; }
    const table = (title, rows, cols) => h("div", {}, h("h3", {}, title), rows.length
      ? h("table", { class: "table" }, h("tbody", {}, rows.map(r => h("tr", {}, cols.map(c => h("td", {}, c(r)))))))
      : empty("—"));
    const rel = p => p.reliability === null ? "—" : `${p.reliability}%`;
    body.replaceChildren(
      h("div", { class: "stats" },
        h("div", { class: "stat" }, h("div", { class: "label" }, "Groups"), h("div", { class: "value" }, st.groups)),
        h("div", { class: "stat" }, h("div", { class: "label" }, "Attendance marks"), h("div", { class: "value" }, st.attendanceMarked))),
      h("div", { class: "grid2" },
        table("By activity", st.activities, [a => a.name, a => `${a.groups} groups`, a => `avg ${a.avgFilled} players`]),
        table("Top players", st.topPlayers, [p => member(p.member), p => `${p.groups} groups`, p => `came ${p.attended}`, rel]),
        table("Top organizers", st.topOrganizers, [p => member(p.member), p => `${p.organized} organized`]),
        table("Most no-shows", st.noShows, [p => member(p.member), p => `${p.noShows} no-show`, rel])));
  };
  period.addEventListener("change", load);
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "📈 Statistics"), period),
    h("p", { class: "muted small" }, "From closed and expired groups. Reliability = came / (came + no-show) of marked attendance."), body);
  load();
}

// ----- Reschedule & attendance (admin and organizer) -----

function rescheduleDialog(g, url) {
  const d = dialog();
  const date = h("input", { type: "date" });
  const time = h("input", { type: "time" });
  if (g.startsAt) {
    const t = new Date(g.startsAt);
    const pad = n => String(n).padStart(2, "0");
    date.value = `${t.getFullYear()}-${pad(t.getMonth() + 1)}-${pad(t.getDate())}`;
    time.value = `${pad(t.getHours())}:${pad(t.getMinutes())}`;
  }
  const save = h("button", { class: "btn" }, "🕒 Change time");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("POST", url, { date: date.value || null, time: time.value || null }),
      "Time changed — everyone in the group got a DM.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, `Change time: ${g.activity}`),
    h("p", { class: "muted small" }, "Server time zone (Settings). Leave both empty for «no time». Signed-up members get a DM."),
    h("div", { class: "row" }, date, time),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
}

/** Attendance toggle for a seated member: not marked → came → no-show → not marked. */
function attendanceButton(s, url) {
  const next = s.attended === null || s.attended === undefined ? true : s.attended ? false : null;
  const label = s.attended === true ? "✅" : s.attended === false ? "❌" : "◻️";
  const title = s.attended === true ? "Came — click: no-show" : s.attended === false ? "No-show — click: clear" : "Not marked — click: came";
  return h("button", { class: "btn ghost", title, style: "padding:2px 6px",
    onclick: () => act(null, () => gApi("POST", url, { userId: s.member.id, attended: next })).then(r => { if (r !== undefined) refresh(); }) }, label);
}

function requirementsLine(req) {
  if (!req) return null;
  return h("div", { class: "muted small" }, "📋 Requires: ", req.roles.map(r => roleChip(r)), req.minGs ? h("span", { class: "chip" }, `GS ${req.minGs}+`) : null);
}

/** Class of a character: the role's emoji, otherwise a small role chip. */
function classMark(e) {
  if (!e.classIcon) return null;
  // Custom server emoji <:name:id> → image; unicode → text
  const m = /^<a?:([^:]+):(\d+)>$/.exec(e.classIcon);
  const title = e.classRole ? `@${e.classRole.name}` : "";
  return m ? h("img", { src: `https://cdn.discordapp.com/emojis/${m[2]}.webp?size=32`, alt: m[1], title, style: "width:18px;height:18px" })
    : h("span", { title }, e.classIcon);
}

function groupCard(g) {
  const close = h("button", { class: "btn danger" }, "🗑️ Force close");
  close.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The group is closed and its post is deleted in Discord.", "Close & delete")) return;
    if (await act(close, () => gApi("POST", `/lfg/groups/${encodeURIComponent(g.id)}/close`), "Group closed.") !== undefined) refresh();
  });

  const roster = (action, m, okText, extra) => act(null,
    () => gApi("POST", `/lfg/groups/${encodeURIComponent(g.id)}/roster`, { action, userId: m.id, ...(extra || {}) }), okText)
    .then(r => { if (r !== undefined) refresh(); });
  const tool = (icon, title, fn) => h("button", { class: "btn ghost", title, onclick: fn, style: "padding:2px 6px" }, icon);
  const rename = (m, current) => async () => {
    const name = await promptDialog(`Character of ${m.name}`, current || "");
    if (name) roster("character", m, "Character changed.", { character: name });
  };

  return h("div", { class: "card", style: "margin:0" },
    h("div", { class: "card-head" }, h("h3", {}, g.activity), h("span", { class: "badge ok" }, `${g.filled}/${g.size}`)),
    h("div", { class: "muted small" }, "Organizer: ", member(g.owner)),
    h("div", { class: "muted small" }, `Start: ${fmtDate(g.startsAt)}`, g.channel ? ` · #${g.channel}` : ""),
    h("div", { class: "muted small" }, `Auto-delete ${fmtRelative(g.expiresAt)}`),
    requirementsLine(g.requirements),
    h("div", { style: "margin-bottom:8px" }),
    h("ul", { class: "slots" }, g.slots.map((s, i) => h("li", {},
      h("span", { class: "num" }, i + 1), h("span", {}, s.emoji),
      s.member ? h("span", {}, h("span", { class: "row", style: "gap:4px" }, member(s.member), classMark(s)),
        s.comment ? h("div", { class: "muted small" }, `💬 ${s.comment}`) : null) : h("span", { class: "free" }, `free (${s.slotRole})`),
      h("span", { class: "char" }, s.character || "", s.gs ? ` · GS ${s.gs}` : ""),
      s.member ? h("span", {},
        g.started ? attendanceButton(s, `/lfg/groups/${encodeURIComponent(g.id)}/attendance`) : null,
        tool("✏️", "Change character", rename(s.member, s.character)),
        s.member.id !== g.owner.id ? tool("⬇️", "Move to reserve", () => roster("toReserve", s.member, "Moved to the reserve.")) : null,
        s.member.id !== g.owner.id ? tool("✖️", "Remove from the group", async () => {
          if (await confirmDialog(`Remove ${s.member.name}?`, "Their slot becomes free.", "Remove")) roster("remove", s.member, "Removed.");
        }) : null) : null))),
    g.reserve.length ? h("div", { style: "margin-top:8px" }, h("div", { class: "muted small" }, `Reserve (${g.reserve.length})`),
      h("ul", { class: "slots" }, g.reserve.map(r => h("li", {}, h("span", {}, r.emoji),
        h("span", {}, h("span", { class: "row", style: "gap:4px" }, member(r.member), classMark(r)),
          r.comment ? h("div", { class: "muted small" }, `💬 ${r.comment}`) : null),
        h("span", { class: "char" }, r.character || ""),
        h("span", {},
          tool("⬆️", "Move into the group (sends a DM)", () => roster("promote", r.member, "Moved into the group, DM sent.")),
          tool("✏️", "Change character", rename(r.member, r.character)),
          tool("✖️", "Remove from the reserve", () => roster("removeReserve", r.member, "Removed from the reserve."))))))) : null,
    h("div", { class: "row", style: "margin-top:12px" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, "Open in Discord"),
      h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/lfg/groups/${encodeURIComponent(g.id)}/time`) }, "🕒 Time"),
      close));
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
    h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Open tickets")), openBody),
    ticketArchiveCard());

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

  const form = questionEditor(p);
  const autoClose = h("input", { type: "number", min: "2", max: "90", placeholder: "off", value: p.autoCloseDays ?? "", style: "width:100px" });

  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    const res = await act(save, () => gApi("PUT", `/tickets/projects/${encodeURIComponent(p.key)}`, {
      staffRoleIds: staff.value(), memberRoleIds: members.value(), categoryId: cat.value || null, customText: text.value,
      formEnabled: form.enabled(), questions: form.value(),
      autoCloseDays: autoClose.value === "" ? null : Number(autoClose.value)
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
    form.el,
    h("div", { class: "field" }, h("label", {}, "Auto-close inactive tickets after (days)"),
      h("div", { class: "row" }, autoClose, h("span", { class: "muted small" }, "Empty = never. The bot warns 24 h before closing; any message resets the timer."))),
    h("div", { class: "row" }, save, remove));
}

/** Application form editor: on/off + up to 5 questions (Discord form limit). */
function questionEditor(p) {
  let qs = (p.questions || []).map(q => ({ ...q }));
  const enabled = h("input", { type: "checkbox", checked: p.formEnabled });
  const list = h("div", {});
  const add = h("button", { class: "btn secondary", type: "button" }, "➕ Add question");
  const draw = () => {
    list.replaceChildren(...qs.map((q, i) => {
      const label = h("input", { maxlength: "45", placeholder: "Question, e.g. Character name", value: q.label || "", style: "flex:2 1 220px" });
      label.addEventListener("input", () => { q.label = label.value; });
      const hint = h("input", { maxlength: "100", placeholder: "Hint (optional)", value: q.placeholder || "", style: "flex:2 1 180px" });
      hint.addEventListener("input", () => { q.placeholder = hint.value; });
      const long = h("input", { type: "checkbox", checked: !!q.long });
      long.addEventListener("change", () => { q.long = long.checked; });
      const req = h("input", { type: "checkbox", checked: q.required !== false });
      req.addEventListener("change", () => { q.required = req.checked; });
      const max = h("input", { type: "number", min: "1", max: "4000", value: q.maxLength ?? (q.long ? 1000 : 200), style: "width:80px", title: "Max length" });
      max.addEventListener("input", () => { q.maxLength = Number(max.value || 0) || null; });
      const up = h("button", { class: "btn ghost", type: "button", title: "Up", disabled: i === 0, onclick: () => { [qs[i - 1], qs[i]] = [qs[i], qs[i - 1]]; draw(); } }, "↑");
      const del = h("button", { class: "btn ghost", type: "button", title: "Remove", onclick: () => { qs.splice(i, 1); draw(); } }, "✖");
      return h("div", { class: "row question-row" }, h("span", { class: "muted" }, `${i + 1}.`), label, hint,
        h("label", { class: "row small" }, long, "long"), h("label", { class: "row small" }, req, "required"), max, up, del);
    }));
    add.disabled = qs.length >= 5;
  };
  add.addEventListener("click", () => { qs.push({ label: "", placeholder: "", long: false, required: true, maxLength: 200 }); draw(); });
  draw();
  return {
    el: h("div", { class: "field form-editor" },
      h("label", {}, "Application form"),
      h("label", { class: "row", style: "margin-bottom:6px" }, enabled, "Ask these questions before the ticket is created (Discord form, up to 5)"),
      list, h("div", { class: "row", style: "margin-top:6px" }, add,
        h("span", { class: "muted small" }, "Answers appear in the ticket, on the site and in the archive."))),
    enabled: () => enabled.checked,
    value: () => qs.filter(q => (q.label || "").trim()).map(q => ({
      id: q.id || null, label: q.label.trim(), placeholder: q.placeholder || null, long: !!q.long, required: q.required !== false, maxLength: q.maxLength || null
    }))
  };
}

/** Archive of closed tickets with search. */
function ticketArchiveCard() {
  const OUT = { approved: ["approved", "ok"], denied: ["denied", "lock"], closed: ["closed", ""], "auto-closed": ["auto-closed", "wait"] };
  const outcome = h("select", {}, h("option", { value: "" }, "All outcomes"), Object.keys(OUT).map(k => h("option", { value: k }, k)));
  const q = h("input", { placeholder: "Search: name, project, reason, answers…", style: "flex:1 1 220px" });
  const tbody = h("tbody", {});
  const more = h("button", { class: "btn secondary", style: "display:none" }, "Load more");
  let skip = 0;
  const load = async reset => {
    if (reset) { skip = 0; tbody.replaceChildren(); }
    const params = new URLSearchParams({ skip: String(skip), take: "50" });
    if (outcome.value) params.set("outcome", outcome.value);
    if (q.value.trim()) params.set("q", q.value.trim());
    const res = await act(more, () => gApi("GET", `/tickets/archive?${params}`));
    if (res === undefined) return;
    skip += res.entries.length;
    for (const e of res.entries) {
      const [label, cls] = OUT[e.outcome] || [e.outcome, ""];
      const details = h("tr", { style: "display:none" }, h("td", { colspan: "5" },
        e.answers.length ? e.answers.map(a => h("div", { style: "margin-bottom:6px" }, h("b", {}, a.question), h("div", {}, a.answer)))
          : h("span", { class: "muted" }, "No form answers.")));
      const toggle = h("button", { class: "btn ghost", onclick: () => { details.style.display = details.style.display === "none" ? "" : "none"; } }, "Form");
      tbody.append(h("tr", {},
        h("td", {}, h("b", {}, e.userName), h("div", { class: "muted small" }, e.project)),
        h("td", {}, h("span", { class: `badge ${cls}` }, label), e.byName ? h("div", { class: "muted small" }, `by ${e.byName}`) : null),
        h("td", { class: "small" }, e.reason || ""),
        h("td", { class: "muted small", style: "white-space:nowrap" }, new Date(e.openedAt).toLocaleDateString(), " → ", new Date(e.closedAt).toLocaleString()),
        h("td", { class: "actions" }, toggle)), details);
    }
    if (!tbody.children.length) tbody.append(h("tr", {}, h("td", { colspan: "5" }, empty("Nothing found."))));
    more.style.display = res.more ? "" : "none";
  };
  let timer = 0;
  outcome.addEventListener("change", () => load(true));
  q.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => load(true), 300); });
  more.addEventListener("click", () => load(false));
  load(true);
  return h("div", { class: "card" }, h("h2", {}, "🗄️ Archive"),
    h("div", { class: "row", style: "margin-bottom:12px" }, outcome, q),
    h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Applicant"), h("th", {}, "Outcome"), h("th", {}, "Reason"), h("th", {}, "Opened → closed"), h("th", {}))), tbody)),
    h("div", { class: "row", style: "margin-top:12px" }, more));
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

// ================= Posts: panels and bot messages =================

const KIND_LABELS = {
  "panel:lfg": ["LFG panel", "ok"], "panel:tickets": ["Ticket panel", "ok"], "panel:rooms": ["Room panel", "ok"],
  "lfg-group": ["LFG group", "wait"], "ticket": ["Ticket", "wait"], "other": ["Message", ""]
};

async function pagePosts(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📌 Posts"),
    h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));

  // --- Panels
  const discover = h("button", { class: "btn secondary" }, "🔍 Find posted panels");
  discover.addEventListener("click", async () => {
    const res = await act(discover, () => gApi("POST", "/texts/discover"));
    if (res === undefined) return;
    const total = Object.values(res).reduce((a, b) => a + b, 0);
    toast(total ? `Found ${total} panel message(s).` : "No new panels found.");
    refresh();
  });
  const panelsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h2", {}, "Panels"), discover),
    h("p", { class: "muted small" }, "Panel messages the bot knows about. Deleting removes the message in Discord."),
    panelsBody));

  // --- Bot messages in a channel
  const chSel = channelSelect("text", "Choose a text channel…");
  const limitSel = h("select", {}, [50, 100, 200, 500].map(n => h("option", { value: n, selected: n === 100 }, `last ${n} messages`)));
  const loadBtn = h("button", { class: "btn" }, "Load");
  const msgBody = h("div", {}, empty("Choose a channel to see the bot's messages there."));
  loadBtn.addEventListener("click", () => {
    if (!chSel.value) return toast("Choose a channel first.", "warn");
    loadMessages(chSel.value, limitSel.value, msgBody, loadBtn);
  });
  main.append(h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h2", {}, "Bot messages in a channel")),
    h("p", { class: "muted small" }, "Everything the bot posted: panels, LFG groups, reminders, ticket messages and so on. ",
      "Deleting an LFG group post closes the group."),
    h("div", { class: "row", style: "margin-bottom:12px" }, h("div", { class: "grow" }, chSel), limitSel, loadBtn),
    msgBody));

  try {
    const panels = await gApi("GET", "/posts/panels");
    panelsBody.replaceChildren(...panels.map(panelGroup));
  } catch (e) { panelsBody.replaceChildren(empty(e.message)); }
}

function panelGroup(p) {
  const upd = h("button", { class: "btn ghost" }, "↻ Update with current text");
  upd.addEventListener("click", () => act(upd, () => gApi("POST", `/posts/panels/${p.key}/refresh`),
    r => `Updated ${r.panelsUpdated} of ${r.panelsTotal}.`));
  return h("div", { style: "margin-bottom:14px" },
    h("div", { class: "row" }, h("h3", { style: "margin:0" }, p.name), h("span", { class: "muted small" }, `${p.messages.length} posted`),
      p.messages.length ? upd : null),
    p.messages.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("tbody", {}, p.messages.map(m => {
        const del = h("button", { class: "btn danger" }, "Delete");
        del.addEventListener("click", async () => {
          if (!await confirmDialog(`Delete this ${p.name}?`, `The message in #${m.channel || "unknown"} will be deleted.`, "Delete")) return;
          if (await act(del, () => gApi("POST", `/posts/channels/${m.channelId}/delete`, { messageIds: [m.messageId] }),
            r => r.deleted ? "Panel deleted." : "The message was already gone.") !== undefined) refresh();
        });
        return h("tr", {},
          h("td", {}, m.channel ? `#${m.channel}` : h("span", { class: "chip warn" }, "deleted channel")),
          h("td", { class: "muted small" }, fmtDate(m.createdAt)),
          h("td", { class: "actions" }, h("a", { class: "btn ghost", href: m.jumpUrl, target: "_blank", rel: "noopener" }, "Open"), " ", del));
      })))) : h("div", { class: "empty small" }, "None known. Post it from the module tab or use “Find posted panels”."));
}

async function loadMessages(channelId, limit, body, btn) {
  body.replaceChildren(h("div", { class: "muted" }, "Loading…"));
  const list = await act(btn, () => gApi("GET", `/posts/channels/${channelId}/messages?limit=${limit}`));
  if (list === undefined) { body.replaceChildren(empty("Couldn't load messages.")); return; }
  if (!list.length) { body.replaceChildren(empty("The bot has no messages among the loaded ones.")); return; }

  const checks = [];
  const delSel = h("button", { class: "btn danger" }, "🗑️ Delete selected");
  const all = h("input", { type: "checkbox", title: "Select all" });
  all.addEventListener("change", () => checks.forEach(c => { c.cb.checked = all.checked; }));
  delSel.addEventListener("click", async () => {
    const ids = checks.filter(c => c.cb.checked).map(c => c.id);
    if (!ids.length) return toast("Nothing selected.", "warn");
    if (!await confirmDialog(`Delete ${ids.length} message(s)?`, "This can't be undone. LFG group posts close their groups.", "Delete")) return;
    const res = await act(delSel, () => gApi("POST", `/posts/channels/${channelId}/delete`, { messageIds: ids }));
    if (res === undefined) return;
    toast(`Deleted ${res.deleted}${res.failed ? `, failed ${res.failed}` : ""}.`, res.failed ? "warn" : "ok");
    loadMessages(channelId, limit, body, btn);
  });

  const rows = list.map(m => {
    const cb = h("input", { type: "checkbox" });
    checks.push({ cb, id: m.messageId });
    const [label, cls] = KIND_LABELS[m.kind] || [m.kind, ""];
    const del = h("button", { class: "btn ghost" }, "Delete");
    del.addEventListener("click", async () => {
      if (!await confirmDialog("Delete this message?", m.kind === "lfg-group" ? "The LFG group will be closed." : "This can't be undone.", "Delete")) return;
      if (await act(del, () => gApi("POST", `/posts/channels/${channelId}/delete`, { messageIds: [m.messageId] }), "Deleted.") !== undefined)
        loadMessages(channelId, limit, body, btn);
    });
    return h("tr", {},
      h("td", {}, cb),
      h("td", { class: "muted small", style: "white-space:nowrap" }, fmtDate(m.createdAt)),
      h("td", {}, h("span", { class: `badge ${cls}` }, label)),
      h("td", {}, m.title ? h("div", {}, m.title) : null, h("div", { class: "muted small" }, m.preview || "—")),
      h("td", { class: "actions" }, h("a", { class: "btn ghost", href: m.jumpUrl, target: "_blank", rel: "noopener" }, "Open"), " ", del));
  });

  body.replaceChildren(
    h("div", { class: "row", style: "margin-bottom:8px" }, h("span", { class: "muted small" }, `${list.length} bot message(s)`), delSel),
    h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, all), h("th", {}, "Posted"), h("th", {}, "Type"), h("th", {}, "Content"), h("th", {}))),
      h("tbody", {}, rows))));
}

// ================= Log =================

const MODULE_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Voice", texts: "📝 Texts", posts: "📌 Posts",
  access: "🔑 Access", settings: "⚙️ Settings" };
const SOURCE_LABELS = { web: "🌐 site", discord: "💬 Discord", system: "🤖 bot" };

async function pageLog(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📜 Log"),
    h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted" }, "What was done on the site and in the bot: who, when and with what. The last 5000 actions are kept."));

  const actor = h("select", {}, h("option", { value: "" }, "Everyone"));
  const module = h("select", {}, h("option", { value: "" }, "All modules"),
    Object.entries(MODULE_LABELS).map(([k, l]) => h("option", { value: k }, l)));
  const q = h("input", { placeholder: "Search…", style: "width:200px" });
  const tbody = h("tbody", {});
  const more = h("button", { class: "btn secondary", style: "display:none" }, "Load more");
  let skip = 0;
  let actorsLoaded = false;

  const load = async (reset) => {
    if (reset) { skip = 0; tbody.replaceChildren(); }
    const params = new URLSearchParams({ skip: String(skip), take: "100" });
    if (actor.value) params.set("actor", actor.value);
    if (module.value) params.set("module", module.value);
    if (q.value.trim()) params.set("q", q.value.trim());
    const res = await act(more, () => gApi("GET", `/log?${params}`));
    if (res === undefined) return;
    if (!actorsLoaded) {
      actor.append(...res.actors.map(a => h("option", { value: a.id }, `${a.name} (${a.count})`)));
      actorsLoaded = true;
    }
    skip += res.entries.length;
    tbody.append(...res.entries.map(e => h("tr", {},
      h("td", { class: "muted small", style: "white-space:nowrap" }, new Date(e.at).toLocaleString()),
      h("td", {}, e.actorName, h("div", { class: "muted small" }, SOURCE_LABELS[e.source] || e.source)),
      h("td", { class: "small" }, MODULE_LABELS[e.module] || e.module),
      h("td", {}, e.action, e.details ? h("div", { class: "muted small", style: "word-break:break-word" }, e.details) : null))));
    if (!tbody.children.length) tbody.append(h("tr", {}, h("td", { colspan: "4" }, empty("Nothing found."))));
    more.style.display = res.more ? "" : "none";
  };

  let timer = 0;
  actor.addEventListener("change", () => load(true));
  module.addEventListener("change", () => load(true));
  q.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => load(true), 300); });
  more.addEventListener("click", () => load(false));

  main.append(h("div", { class: "card" },
    h("div", { class: "row", style: "margin-bottom:12px" }, actor, module, q),
    h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "When"), h("th", {}, "Who"), h("th", {}, "Module"), h("th", {}, "Action"))), tbody)),
    h("div", { class: "row", style: "margin-top:12px" }, more)));
  load(true);
}

// ================= Settings =================

async function pageSettings(main) {
  main.append(h("h1", {}, "⚙️ Settings"));
  const holder = h("div", {});
  main.append(holder);
  holder.append(h("p", { class: "muted" }, "Time zone, auto-delete and reminder of LFG groups are in LFG → LFG settings."));

  // Backup (owner only)
  if (can("access")) {
    const download = h("a", { class: "btn", href: "/api/backup", download: "" }, "⬇️ Download backup");
    const file = h("input", { type: "file", accept: ".zip,application/zip" });
    const restore = h("button", { class: "btn danger" }, "⬆️ Restore");
    restore.addEventListener("click", async () => {
      const f = file.files && file.files[0];
      if (!f) return toast("Choose a backup .zip first.", "warn");
      if (!await confirmDialog("Restore data from this backup?",
        "Current data is saved to data/backups first. The bot restarts (about 15 seconds) to load the restored data.", "Restore")) return;
      restore.disabled = true;
      try {
        const res = await fetch("/api/backup/restore", { method: "POST", headers: { "X-Panel": "1", "Content-Type": "application/zip" }, body: f, credentials: "same-origin" });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(data.error || `Restore failed (${res.status})`);
        toast(`Restored ${data.files} file(s). The bot is restarting — reload the page in ~20 seconds.`);
      } catch (e) { toast(e.message, "error"); }
      finally { restore.disabled = false; }
    });
    holder.append(h("div", { class: "card" }, h("h2", {}, "💾 Backup"),
      h("p", { class: "muted small" }, "All bot data (data/*.json): activities, groups, characters, tickets, archive, settings, access… Site login keys are not included."),
      h("div", { class: "row", style: "margin-bottom:10px" }, download),
      h("div", { class: "row" }, file, restore)));
  }

  // Member roles of the selected server
  const card = h("div", { class: "card" }, h("h2", {}, "👥 Member roles"),
    h("p", { class: "muted small" }, "Everyone with one of these roles on this server can log in and use the Member section (Groups, My characters). Admin sections still need rights from Access."));
  holder.append(card);
  try {
    const current = await gApi("GET", "/member-roles");
    const editor = roleListEditor(current);
    const saveRoles = h("button", { class: "btn" }, "💾 Save member roles");
    saveRoles.addEventListener("click", () => act(saveRoles, () => gApi("PUT", "/member-roles", { roleIds: editor.value() }), "Member roles saved."));
    card.append(editor.el, h("div", { class: "row", style: "margin-top:12px" }, saveRoles));
  } catch (e) { card.append(empty(e.message)); }
}

// ================= Member: home =================

async function pageHome(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🏠 Home"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  let d;
  try { d = await gApi("GET", "/me/home"); } catch (e) { holder.replaceChildren(empty(e.message)); return; }
  const go = tab => () => { state.tab = tab; store.set("gb.tab", tab); renderShell(); };
  const STATUS = { organizer: ["organizer", "ok"], group: ["in the group", "ok"], reserve: ["reserve", "wait"] };

  // My groups
  const groups = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "🎮 My groups"),
    h("button", { class: "btn secondary", onclick: go("play") }, "All groups →")),
    d.groups.length ? h("ul", { class: "slots" }, d.groups.map(g => h("li", {},
      h("span", {}, h("b", {}, g.activity), h("div", { class: "muted small" }, `${fmtDate(g.startsAt)} · ${g.filled}/${g.size}${g.character ? ` · ${g.character}` : ""}`)),
      h("span", { class: "char" }, h("span", { class: `badge ${STATUS[g.status][1]}` }, STATUS[g.status][0])),
      h("a", { class: "btn ghost", href: g.jumpUrl, target: "_blank", rel: "noopener" }, "Open"))))
      : empty("You're not in any group. Find one in Groups."));

  // My applications
  const apply = h("button", { class: "btn" }, "📝 Apply");
  apply.addEventListener("click", () => applyDialog());
  const tickets = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "🎫 My applications"), apply),
    d.tickets.length ? h("ul", { class: "slots" }, d.tickets.map(t => h("li", {},
      h("span", {}, h("b", {}, t.project), h("div", { class: "muted small" }, `opened ${fmtRelative(t.createdAt)}`)),
      h("span", { class: "char" }, t.approved ? h("span", { class: "badge ok" }, "approved") : h("span", { class: "badge wait" }, "waiting")),
      h("a", { class: "btn ghost", href: t.jumpUrl, target: "_blank", rel: "noopener" }, "Open"))))
      : empty("No open applications."));

  // My voice room
  const room = h("div", { class: "card" }, h("h2", {}, "🔊 My voice room"));
  if (!d.room) room.append(empty("You don't own a temporary room. Join a «join to create» channel in Discord to get one."));
  else {
    const r = d.room;
    const name = h("input", { value: r.name, maxlength: "100" });
    const limit = h("input", { type: "number", min: "0", max: "99", value: r.limit ?? 0, style: "width:90px" });
    const call = (btn, body) => act(btn, () => gApi("POST", "/me/room", body), res => res.message).then(x => { if (x !== undefined) refresh(); });
    const rn = h("button", { class: "btn secondary" }, "Rename");
    rn.addEventListener("click", () => call(rn, { action: "rename", name: name.value }));
    const lm = h("button", { class: "btn secondary" }, "Set limit");
    lm.addEventListener("click", () => call(lm, { action: "limit", limit: Number(limit.value || 0) }));
    const lk = h("button", { class: "btn" }, r.locked ? "🔓 Unlock" : "🔒 Lock");
    lk.addEventListener("click", () => call(lk, { action: r.locked ? "unlock" : "lock" }));
    room.append(
      h("p", { class: "muted small" }, `${r.inside} inside${r.locked ? " · locked" : ""}. Permit / kick members in Discord (Room controls or /room).`),
      h("div", { class: "row", style: "margin-bottom:8px" }, h("div", { class: "grow" }, name), rn),
      h("div", { class: "row" }, limit, lm, lk));
  }

  // Room defaults: applied every time my temporary room is created
  const prefsCard = h("div", { class: "card" }, h("h2", {}, "⚙️ My room defaults"),
    h("p", { class: "muted small" }, "Used whenever your temporary room is created. Same as /room defaults in Discord."));
  gApi("GET", "/me/room-prefs").then(pr => {
    const pn = h("input", { placeholder: "Room name, {user} = your name (empty = channel default)", maxlength: "100", value: pr.name || "" });
    const pl = h("input", { type: "number", min: "0", max: "99", placeholder: "limit", value: pr.limit ?? "", style: "width:90px" });
    const pk = h("input", { type: "checkbox", checked: pr.locked });
    const ps = h("button", { class: "btn secondary" }, "Save");
    ps.addEventListener("click", () => act(ps, () => gApi("PUT", "/me/room-prefs",
      { name: pn.value || null, limit: pl.value === "" ? null : Number(pl.value), locked: pk.checked }), "Room defaults saved."));
    prefsCard.append(h("div", { class: "field" }, pn), h("div", { class: "row" }, pl, h("label", { class: "row" }, pk, "Lock right away"), ps));
  }).catch(e => prefsCard.append(empty(e.message)));

  // My stats
  const statsCard = h("div", { class: "card" }, h("h2", {}, "📈 My stats"));
  gApi("GET", "/me/stats").then(st => {
    statsCard.append(
      h("div", { class: "stats" },
        h("div", { class: "stat" }, h("div", { class: "label" }, "Groups"), h("div", { class: "value" }, st.groups)),
        h("div", { class: "stat" }, h("div", { class: "label" }, "Organized"), h("div", { class: "value" }, st.organized)),
        h("div", { class: "stat" }, h("div", { class: "label" }, "Reliability"), h("div", { class: "value" }, st.reliability === null ? "—" : `${st.reliability}%`),
          h("div", { class: "sub" }, `came ${st.attended} · no-show ${st.noShows}`))),
      st.recent.length ? h("ul", { class: "slots" }, st.recent.map(r => h("li", {},
        h("span", {}, h("b", {}, r.activity), h("div", { class: "muted small" }, fmtDate(r.at))),
        h("span", { class: "char" }, r.organizer ? h("span", { class: "badge ok" }, "organizer")
          : r.attended === true ? h("span", { class: "badge ok" }, "came") : r.attended === false ? h("span", { class: "badge lock" }, "no-show") : "")))) : null);
  }).catch(e => statsCard.append(empty(e.message)));

  holder.replaceChildren(groups, h("div", { class: "grid2" }, tickets, room), h("div", { class: "grid2" }, statsCard, prefsCard),
    h("div", { class: "card" }, h("h2", {}, "🧙 Characters"),
      h("p", { class: "muted small" }, "Your characters, their class role and note, and group DMs."),
      h("button", { class: "btn secondary", onclick: go("chars") }, "My characters →")));
}

/** Apply for a project from the site (with the project's form, if enabled). */
async function applyDialog() {
  let projects;
  try { projects = await gApi("GET", "/me/tickets/projects"); } catch (e) { return toast(e.message, "error"); }
  if (!projects.length) return toast("There are no projects to apply to.", "warn");

  const d = dialog();
  const sel = h("select", {}, projects.map(p => h("option", { value: p.key }, p.openTicketUrl ? `${p.name} (already open)` : p.name)));
  const fields = h("div", {});
  let inputs = [];
  const draw = () => {
    const p = projects.find(x => x.key === sel.value);
    inputs = [];
    if (p.openTicketUrl) {
      fields.replaceChildren(h("p", {}, "You already have an open ticket for this project. ",
        h("a", { href: p.openTicketUrl, target: "_blank", rel: "noopener" }, "Open it in Discord")));
      return;
    }
    fields.replaceChildren(h("div", {}, (p.questions || []).map(q => {
      const input = q.long
        ? h("textarea", { maxlength: String(q.maxLength), placeholder: q.placeholder || "", style: "min-height:80px" })
        : h("input", { maxlength: String(q.maxLength), placeholder: q.placeholder || "" });
      inputs.push({ q, input });
      return h("div", { class: "field" }, h("label", {}, q.label + (q.required ? " *" : "")), input);
    }), p.questions ? null : h("p", { class: "muted small" }, "No form for this project — the ticket opens right away.")));
  };
  sel.addEventListener("change", draw);
  draw();

  const send = h("button", { class: "btn" }, "📨 Send application");
  send.addEventListener("click", async () => {
    const answers = {};
    for (const { q, input } of inputs) {
      if (q.required && !input.value.trim()) return toast(`Answer: ${q.label}`, "warn");
      answers[q.id] = input.value;
    }
    const res = await act(send, () => gApi("POST", "/me/tickets", { projectKey: sel.value, answers }));
    if (res === undefined) return;
    d.close();
    toast("Application sent — your private ticket channel is ready.");
    window.open(res.jumpUrl, "_blank", "noopener");
    refresh();
  });

  d.classList.add("wide");
  d.replaceChildren(h("h2", {}, "📝 Apply"),
    h("div", { class: "field" }, h("label", {}, "Project"), sel), fields,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), send));
  d.onclose = () => d.classList.remove("wide");
  d.showModal();
}

// ================= Admin: panels & posts =================

async function pageContent(main) {
  const sub = store.get("gb.content") === "posts" && can("posts") ? "posts" : can("texts") ? "texts" : "posts";
  const tabsRow = h("div", { class: "row", style: "margin-bottom:16px" },
    can("texts") ? h("button", { class: `btn secondary${sub === "texts" ? " active-tab" : ""}`, onclick: () => { store.set("gb.content", "texts"); refresh(); } }, "📝 Panel texts") : null,
    can("posts") ? h("button", { class: `btn secondary${sub === "posts" ? " active-tab" : ""}`, onclick: () => { store.set("gb.content", "posts"); refresh(); } }, "📌 Posts & messages") : null);
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📌 Panels & posts")), tabsRow);
  const inner = h("div", {});
  main.append(inner);
  await (sub === "texts" ? pageTexts(inner) : pagePosts(inner));
  // The section's own heading becomes a sub-heading
  const h1 = inner.querySelector("h1");
  if (h1) h1.replaceWith(h("h2", {}, h1.textContent));
}

// ================= Member: groups =================

const ROLE_CHOICES = [["Dps", "⚔️ DD"], ["Tank", "🛡️ Tank"], ["Healer", "💚 Sup"], ["Any", "🎲 All"]];

async function pagePlay(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎮 Groups"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  // Create a group (like /lfg) — collapsed by default
  const createBody = h("div", { style: "display:none" });
  const toggle = h("button", { class: "btn" }, "➕ Create a group");
  const createCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Create a group"), toggle),
    h("p", { class: "muted small" }, "Posted like /lfg in Discord. You are the organizer and take slot 1 with your last used character."),
    createBody);
  let formLoaded = false;
  toggle.addEventListener("click", async () => {
    const open = createBody.style.display === "none";
    createBody.style.display = open ? "" : "none";
    toggle.textContent = open ? "✕ Hide" : "➕ Create a group";
    if (!open || formLoaded) return;
    formLoaded = true;
    createBody.replaceChildren(h("div", { class: "muted" }, "Loading…"));
    try {
      const opt = await gApi("GET", "/me/create-options");
      if (!opt.channel && !(opt.projects || []).some(p => p.channel)) { createBody.replaceChildren(empty("The channel for groups isn't set yet — ask an admin.")); return; }
      const form = lfgForm(opt.activities, { mode: "once", fixedChannel: opt.channel || { id: "", name: "" }, hideSeat: true,
        templates: opt.templates, roles: opt.roles, projects: opt.projects, mirrorChannels: opt.mirrorChannels });
      const publish = h("button", { class: "btn" }, "📣 Publish");
      publish.addEventListener("click", async () => {
        const res = await act(publish, () => gApi("POST", "/me/create", form.value()));
        if (res === undefined) return;
        toast(res.dmsStarted ? "Group published. Sending DMs to the activity role…" : "Group published.");
        refresh();
      });
      createBody.replaceChildren(form.el, h("div", { class: "row" }, publish));
    } catch (e) { createBody.replaceChildren(empty(e.message)); }
  });
  main.insertBefore(createCard, holder);

  let data;
  try { data = await gApi("GET", "/me/groups"); } catch (e) { holder.replaceChildren(empty(e.message)); return; }
  holder.replaceChildren(h("h2", {}, "Active groups"), data.groups.length
    ? h("div", { class: "grid2" }, data.groups.map(g => playCard(g)))
    : h("div", { class: "card" }, empty("No active groups right now.")));
}

function playCard(g) {
  const characters = g.characters || [];
  // Organizer tools (like ⚙️ Manage in Discord): group ⇄ reserve
  const roster$ = (action, m, okText) => act(null,
    () => gApi("POST", `/me/groups/${encodeURIComponent(g.id)}/roster`, { action, userId: m.id }), okText)
    .then(r => { if (r !== undefined) refresh(); });
  const tool = (icon, title, fn) => h("button", { class: "btn ghost", title, onclick: fn, style: "padding:2px 6px" }, icon);
  const org = g.isOrganizer;

  const roster = h("ul", { class: "slots" }, g.slots.map((s, i) => h("li", {},
    h("span", { class: "num" }, i + 1), h("span", {}, s.emoji),
    s.member ? h("span", { class: "row", style: "gap:4px" }, member(s.member), classMark(s)) : h("span", { class: "free" }, `free (${s.slotRole})`),
    h("span", { class: "char" }, s.character || "", s.gs ? ` · GS ${s.gs}` : ""),
    org && s.member && g.started ? attendanceButton(s, `/me/groups/${encodeURIComponent(g.id)}/attendance`) : null,
    org && s.member && s.member.id !== g.owner.id ? tool("⬇️", "Move to reserve", () => roster$("toReserve", s.member, "Moved to the reserve.")) : null)));
  const reserve = g.reserve.length ? h("div", { style: "margin-top:8px" }, h("div", { class: "muted small" }, `Reserve (${g.reserve.length})`),
    h("ul", { class: "slots" }, g.reserve.map(r => h("li", {}, h("span", {}, r.emoji),
      h("span", { class: "row", style: "gap:4px" }, member(r.member), classMark(r)),
      h("span", { class: "char" }, r.character || "", r.gs ? ` · GS ${r.gs}` : ""),
      org ? h("span", {},
        tool("⬆️", "Move into the group (sends a DM)", () => roster$("promote", r.member, "Moved into the group, DM sent.")),
        tool("✖️", "Remove from the reserve", () => roster$("removeReserve", r.member, "Removed from the reserve."))) : null)))) : null;
  const closeBtn = org ? h("button", { class: "btn danger" }, "🗑️ Close group") : null;
  if (closeBtn) closeBtn.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The post is deleted in Discord. Copy the invites first if you need them.", "Close & delete")) return;
    if (await act(closeBtn, () => gApi("POST", `/me/groups/${encodeURIComponent(g.id)}/close`), "Group closed.") !== undefined) refresh();
  });

  // Sign-up form: role + character (saved or new)
  const free = v => g.canJoin[v] || (g.me && g.me.status === "group" && g.me.role === v);
  const role = h("select", {}, ROLE_CHOICES.map(([v, l]) => h("option", { value: v }, free(v) ? l : `${l} (reserve)`)));
  const charSel = h("select", {}, characters.map(c => h("option", { value: c }, c)), h("option", { value: "" }, "➕ New character…"));
  const newChar = h("input", { placeholder: "Character name", maxlength: "32", style: "width:160px" });
  const sync = () => { newChar.style.display = charSel.value ? "none" : ""; };
  charSel.addEventListener("change", sync);
  if (g.me) {
    role.value = g.me.role;
    if (g.me.character && characters.includes(g.me.character)) charSel.value = g.me.character;
  }
  sync();

  const join = h("button", { class: "btn" }, g.me ? "🔁 Switch" : "✅ Sign up");
  join.addEventListener("click", async () => {
    const character = charSel.value || newChar.value.trim();
    if (!character) return toast("Pick or enter a character.", "warn");
    const res = await act(join, () => gApi("POST", `/me/groups/${encodeURIComponent(g.id)}/join`, { role: role.value, character }));
    if (res === undefined) return;
    toast(res.message);
    refresh();
  });
  const leave = h("button", { class: "btn secondary" }, "Leave");
  leave.addEventListener("click", async () => {
    if (await act(leave, () => gApi("POST", `/me/groups/${encodeURIComponent(g.id)}/leave`), "You left the group.") !== undefined) refresh();
  });

  const status = org ? h("span", { class: "badge ok" }, "organizer")
    : g.me ? h("span", { class: `badge ${g.me.status === "group" ? "ok" : "wait"}` }, g.me.status === "group" ? "you're in" : "reserve")
    : null;

  return h("div", { class: "card", style: "margin:0" },
    h("div", { class: "card-head" }, h("h3", {}, g.activity), status, h("span", { class: "badge ok" }, `${g.filled}/${g.size}`)),
    g.project ? h("div", { class: "muted small" }, `🎮 ${g.project}`) : null,
    h("div", { class: "muted small" }, "Organizer: ", member(g.owner)),
    h("div", { class: "muted small" }, `Start: ${fmtDate(g.startsAt)}`, g.channel ? ` · #${g.channel}` : ""),
    requirementsLine(g.requirements),
    h("div", { style: "margin-bottom:8px" }),
    roster, reserve,
    g.closed ? h("div", { class: "muted", style: "margin-top:10px" }, "Signups are closed.") :
      g.blocked && !g.me ? h("div", { class: "muted small", style: "margin-top:10px" }, `🔒 ${g.blocked}`) :
      h("div", { class: "row", style: "margin-top:12px" }, role, charSel, newChar, join, g.me && !org ? leave : null),
    h("div", { class: "row", style: "margin-top:8px" },
      h("a", { class: "btn ghost", href: g.jumpUrl, target: "_blank", rel: "noopener" }, "Open in Discord"),
      org ? h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/me/groups/${encodeURIComponent(g.id)}/time`) }, "🕒 Time") : null,
      closeBtn));
}

// ================= Member: characters =================

async function pageChars(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🧙 My characters"), h("button", { class: "btn secondary", onclick: refresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  let data;
  const wanted = store.get("gb.charProject") || "";
  try { data = await gApi("GET", `/me/characters?project=${encodeURIComponent(wanted)}`); } catch (e) { holder.replaceChildren(empty(e.message)); return; }
  const pq = data.project ? `?project=${encodeURIComponent(data.project)}` : "";

  // Projects (games): each has its own characters; locked without the project role
  const tabsRow = data.projects.length ? h("div", { class: "row", style: "margin-bottom:12px" },
    data.projects.map(p => h("button", {
      class: `btn secondary${p.key === data.project ? " active-tab" : ""}`, disabled: !p.allowed,
      title: p.allowed ? "" : `Needs ${p.roles.map(r => "@" + r.name).join(" or ")}`,
      onclick: () => { store.set("gb.charProject", p.key); refresh(); }
    }, p.allowed ? p.name : `🔒 ${p.name}`))) : null;

  const dm = h("input", { type: "checkbox", checked: data.dmEnabled });
  dm.addEventListener("change", () => act(null, () => gApi("PUT", "/me/dm", { enabled: dm.checked }),
    dm.checked ? "Group DMs turned on." : "Group DMs turned off."));

  const nameIn = h("input", { placeholder: "Character name", maxlength: "32" });
  const add = h("button", { class: "btn" }, "➕ Add character");
  add.addEventListener("click", async () => {
    if (await act(add, () => gApi("POST", `/me/characters${pq}`, { name: nameIn.value }), "Character added.") !== undefined) refresh();
  });

  const rows = data.characters.map(c => {
    // <option> can't show images: unicode icons go in the text, server/bot emojis are shown next to the list
    const isCustom = icon => /^<a?:[^:]+:\d+>$/.test(icon || "");
    const cls = h("select", {}, h("option", { value: "" }, "No class"),
      data.classRoles.map(r => h("option", { value: r.id }, `${r.icon && !isCustom(r.icon) ? r.icon + " " : ""}${r.name}`)));
    if (c.classRole) cls.value = c.classRole.id;
    const clsIcon = h("span", { style: "display:inline-flex;width:20px;justify-content:center" });
    const syncIcon = () => {
      const r = data.classRoles.find(x => x.id === cls.value);
      clsIcon.replaceChildren(...[r ? classMark({ classIcon: r.icon, classRole: r }) : null].filter(Boolean));
    };
    cls.addEventListener("change", syncIcon);
    syncIcon();
    const note = h("input", { maxlength: String(data.maxComment), placeholder: "Note, e.g. can lead", value: c.comment || "", style: "width:100%" });
    const gs = h("input", { type: "number", min: "0", max: "99999", placeholder: "GS", value: c.gs ?? "", style: "width:90px" });
    const save = h("button", { class: "btn secondary" }, "Save");
    save.addEventListener("click", () => act(save,
      () => gApi("PUT", `/me/characters/${encodeURIComponent(c.name)}${pq}`, { classRoleId: cls.value || null, comment: note.value, gs: gs.value === "" ? null : Number(gs.value) }),
      r => r.groupsUpdated ? `Saved. Updated ${r.groupsUpdated} active group(s).` : "Saved."));
    const rm = h("button", { class: "btn ghost" }, "Remove");
    rm.addEventListener("click", async () => {
      if (!await confirmDialog(`Remove ${c.name}?`, "The character, its class and note are deleted.", "Remove")) return;
      if (await act(rm, () => gApi("DELETE", `/me/characters/${encodeURIComponent(c.name)}${pq}`), "Removed.") !== undefined) refresh();
    });
    return h("tr", {}, h("td", {}, h("b", {}, c.name)), h("td", {}, h("div", { class: "row", style: "flex-wrap:nowrap" }, clsIcon, cls)), h("td", {}, gs), h("td", { style: "min-width:220px" }, note),
      h("td", { class: "actions" }, save, " ", rm));
  });

  holder.replaceChildren(
    h("div", { class: "card" },
      h("label", { class: "row" }, dm, "Get a DM when a group is posted for an activity whose role I have"),
      h("p", { class: "muted small" }, "Same as 🔔 / 🔕 under the LFG panel or /lfg-dm.")),
    h("div", { class: "card" }, tabsRow,
      h("p", { class: "muted small" }, "Each game (project) has its own characters. Class — any class of this project (no role needed); its icon is shown next to your name in group posts. GS is checked by group requirements. The note is only for you — you see it when picking a character."),
      data.characters.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
        h("thead", {}, h("tr", {}, h("th", {}, "Character"), h("th", {}, "Class"), h("th", {}, "GS"), h("th", {}, "Note"), h("th", {}))),
        h("tbody", {}, rows))) : empty("No characters yet."),
      h("div", { class: "row", style: "margin-top:12px" }, nameIn, add)));
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

// ================= Guides (❔ button on every tab) =================
// Each guide: features / discord / panel → list of sections [title, [lines]]. `code` and **bold** are formatted.

const HELP = {
  home: {
    title: "Home",
    features: [["What you see here", [
      "**My groups** — groups you organize, play in or wait in the reserve, with the start time.",
      "**My applications** — your open tickets and whether they are approved.",
      "**My voice room** — rename, set a limit, lock/unlock the temporary room you own; **My room defaults** for new rooms.",
      "**📝 Apply** — open an application (ticket) with the project's form.",
      "**📈 My stats** — groups, organized, reliability, recent groups."
    ]]],
    discord: [["The same in Discord", [
      "Group posts in the LFG channel, your ticket channels, and the Room controls panel or `/room …` for your voice room."
    ]]],
    panel: [["Using it", [
      "**Open** jumps to the post or channel in Discord. Room changes apply at once (Discord allows 2 renames per 10 minutes)."
    ]]]
  },
  content: {
    title: "Panels & posts",
    features: [], discord: [], panel: []
  },
  play: {
    title: "Groups",
    features: [["What you can do here", [
      "See all active groups on the server with their roster, class roles and notes.",
      "Sign up with a role and a character, switch role or character, or leave — the Discord post updates at once.",
      "Create your own group and manage it as the organizer (reserve, close).",
      "A role marked **(reserve)** has no free slot: you'll be put on the reserve list."
    ]]],
    discord: [["The same in Discord", [
      "Click a role button under the group post, then pick a character or **New character**.",
      "Under the reply pick your class role and add a note. **Leave** frees your slot.",
      "📋 **Invite to group** / 🏰 **Invite to dungeon** show the names for the game."
    ]]],
    panel: [
      ["Sign up", [
        "In a group card choose the role and the character (or ➕ New character and type the name) → **Sign up**.",
        "Already in? **Switch** changes role/character, **Leave** takes you out."
      ]],
      ["Create and organize", [
        "**➕ Create a group** — activity, slots, date/time → **Publish**. It is posted in the server's LFG channel; you are the organizer and take slot 1.",
        "In your own groups: ⬇️ move a member to the reserve, ⬆️ move someone from the reserve into the group (they get a DM), ✖️ remove from the reserve, **Close group**."
      ]]
    ]
  },
  chars: {
    title: "My characters",
    features: [["What you can do here", [
      "Keep your list of characters for signups.",
      "Give each character a **class** (one of your server roles), a **GS** and a **note** — shown in group posts; GS is checked by group requirements.",
      "Turn group DMs on or off."
    ]]],
    discord: [["The same in Discord", [
      "`/character list`, `/character add`, `/character class name role`, `/character note name text`, `/character remove`.",
      "🔔 / 🔕 under the LFG panel or `/lfg-dm` for DMs."
    ]]],
    panel: [["Using it", [
      "Pick the class and type the note → **Save**. Active groups where you play this character are updated.",
      "**Add character** adds a new name; **Remove** deletes it with its class and note."
    ]]]
  },
  overview: {
    title: "Overview",
    features: [["The admin panel", [
      "One place to manage **Greatness Bot**: LFG groups, tickets, temporary voice rooms, panel texts, bot messages, the action log and settings.",
      "Everything works on the same data as the bot — a change here is visible in Discord immediately, and the other way round.",
      "Pick the server in the top bar. The green dot means the bot is connected to Discord."
    ]]],
    discord: [["Commands overview", [
      "`/lfg`, `/lfg-dm`, `/character list|add|class|note|remove` — groups and characters (everyone).",
      "`/room rename|limit|lock|unlock|permit|kick|claim|panel` — your temporary voice room (everyone).",
      "`/admin` — the in-Discord admin panel (bot admins).",
      "`/lfg-panel`, `/ticket-panel`, `/tempvoice …`, `/activity …`, `/ticket-project …` — setup commands (bot admins)."
    ]]],
    panel: [["Using the panel", [
      "Tabs on the left show only the sections you have access to.",
      "The cards on this page show counters; click one to open its section.",
      "❔ **Guide** on every tab explains that module: what it can do, how to use it in Discord and here."
    ]]]
  },
  lfg: {
    title: "LFG — group & raid finder",
    features: [
      ["What it can do", [
        "Groups of 2–25 slots with roles ⚔️ DD / 🛡️ Tank / 💚 Sup / 🎲 All per slot.",
        "**Activities** — a name linked to a server role: the role is pinged and its members get a DM when a group is posted.",
        "Start time shown to everyone in their own time zone; ⏰ reminder before the start; the post is removed automatically after the start.",
        "**Characters**: members sign up with a saved character; each character can have a **class role** (one of the member's roles, shown next to the name) and a **note** (shown under the name).",
        "**Reserve** when the needed slots are full; the organizer moves people between the group and the reserve (with a DM).",
        "📋 **Invite to group** — ready `/invite Name` commands; 🏰 **Invite to dungeon** — just the names.",
        "**Templates** (admin): activity + slots + requirements, picked in one click in /lfg and on the site.",
        "**Projects (games)**: each has its own characters; a project's groups and characters need its role. LFG panels are linked to a project.",
        "**Classes**: the admin picks which roles are classes and their icons (emoji or server emoji) — only the icon is shown next to the name.",
        "**Duplicate the post** to other channels (admins), copies stay in sync.",
        "**Requirements**: a required role and/or minimum character **GS** — others go to the reserve.",
        "🔊 **Voice room** for a group — only if turned on when creating: everyone can join and listen, only group members can speak; deleted with the group.",
        "**Change time** of a posted group — everyone signed up gets a DM.",
        "**Attendance** after the start → **statistics** and player reliability.",
        "Admin tab: activities, the LFG panel, **recurring groups** on a schedule, any group's roster, force-close.",
        "Member tab **Groups**: everyone with the member role signs up, creates groups and manages their own groups."
      ]]
    ],
    discord: [
      ["Create a group", [
        "Click **Create LFG** on the LFG panel (or use `/lfg`). A menu only you can see opens.",
        "Choose the size → the activity (or «Custom activity», no ping) → select slots and set their roles → **Next**.",
        "Pick the date and time (or «No time»), turn 🔊 **Voice room** on if needed → **Publish**. You take slot 1 with your last used character.",
        "Admins: after publishing, pick channels in «📢 Also post this group in…» to duplicate the post."
      ]],
      ["Sign up", [
        "Click a role button under the group post, then pick a saved character or **New character**.",
        "Under the reply you can pick your **class role** and **add a note** — saved for that character and shown in the post.",
        "Later: `/character class name role`, `/character gs name 1450`, `/character note name text` (empty = remove), add `project:` for another game. `/character list` shows all.",
        "The note is only for you — it is shown when you pick a character, not in the post.",
        "Click the same role again to switch character, another role to switch role. **Leave** frees your slot.",
        "Grey role button = no free slot → you go to the **reserve**."
      ]],
      ["Organizer", [
        "⚙️ **Manage** — move members between the group and the reserve (moved-in members get a DM), mark **attendance** after the start, 🕒 **Change time**.",
        "📋 **Invite to group** — `/invite` commands, 🏰 **Invite to dungeon** — plain names (anyone in the group can use both).",
        "**Close** — shows the group invites, then deletes the post."
      ]],
      ["DMs and admin commands", [
        "🔔 / 🔕 under the panel or `/lfg-dm` — turn group DMs on or off for yourself.",
        "Admins: `/lfg-panel` posts the panel, `/activity add|remove|list` manages activities, `/admin` → LFG."
      ]]
    ],
    panel: [
      ["Activities, panel & channel", [
        "**Channel for groups** — where groups created by members on the site are posted (members can't choose). Empty = the LFG panel's channel.",
        "**Activities** — add a name + the role to ping, or remove one. Posted groups are not affected.",
        "**LFG panel** — pick a text channel and **Post** the message with the Create LFG button."
      ]],
      ["Creating groups", [
        "One-off groups are created in **Member → Groups → Create a group** (as an admin you are a member too)."
      ]],
      ["LFG settings", [
        "**⚙️ LFG settings** — server time zone (start times, recurring groups), delete posts after N hours, reminder N minutes before the start (0 = off)."
      ]],
      ["Projects & classes", [
        "**🎮 Projects** — name + required role(s), one is the default (older characters belong to it). Link each LFG panel to its project.",
        "**🎭 Classes** — common list and per project (a project without its own uses the common list): class roles and an icon for each. **🎨 Use role icons** takes the roles' own icons from Discord (image icons become the bot's emojis).",
        "**Activities** can belong to a project (offered only in its groups) or to «All projects»."
      ]],
      ["Templates & statistics", [
        "**📋 Templates** — name, activity, slots, required role, minimum GS. Shown in /lfg Step 1 and in Create a group.",
        "**📈 Statistics** — groups per activity, top players and organizers, no-shows; period filter."
      ]],
      ["Recurring groups", [
        "**Add schedule**: the same form + days of the week, start time, how many hours before the start to post, the organizer, on/off.",
        "The table shows the next post and start. If a post fails (no access to the channel) that occurrence is skipped and the error is shown."
      ]],
      ["Active groups", [
        "Each card shows the roster with class and notes. Member tools: ✏️ change character, ⬇️ to reserve, ✖️ remove.",
        "Reserve tools: ⬆️ into the group (DM), ✏️, ✖️. **Force close** deletes the post. Every change updates the Discord post at once.",
        "After the start: ◻️/✅/❌ attendance per member. 🕒 **Time** moves the group and DMs everyone."
      ]]
    ]
  },
  tickets: {
    title: "Tickets — applications",
    features: [["What it can do", [
      "A panel with **Create ticket**; the applicant picks a **project** (e.g. WWM, Aion 2).",
      "The bot creates a private **text + voice** channel visible to the applicant and the project's **leadership roles**.",
      "**Approve** (leadership) gives the project's roles, DMs the applicant and closes the ticket after 15 seconds.",
      "**Close** (anyone in the ticket) deletes both channels. One open ticket per project per member.",
      "Per project: leadership roles, roles given on Approve, the category for channels and a custom text in the ticket message.",
      "**Application form** (per project, optional): up to 5 questions asked before the ticket opens; answers go into the ticket.",
      "**Deny** with a reason sent to the applicant by DM. **Archive** of every closed ticket with search.",
      "**Auto-close** inactive tickets after N days (warning 24 h before).",
      "Members can also **apply from the site** (Member → Home → Apply)."
    ]]],
    discord: [
      ["Applicant", [
        "Click **Create ticket** on the ticket panel → choose the project → fill in the form if the project has one.",
        "Talk with the leadership in the private channels. **Close** ends the ticket."
      ]],
      ["Leadership", [
        "**Approve** gives the roles and closes the ticket. If a role can't be given (bot role too low), the ticket stays open.",
        "**Deny** asks for a reason (optional), DMs the applicant and closes the ticket."
      ]],
      ["Admin commands", [
        "`/ticket-panel` — post the panel.",
        "`/ticket-project add|remove|staff-add|staff-remove|member-role-add|member-role-remove|category|list`.",
        "`/admin` → Tickets — the same in a menu."
      ]]
    ],
    panel: [
      ["Projects", [
        "**New project** — name + the role given on Approve. Then set leadership roles, approve roles, the category and the ticket text, and **Save**.",
        "Saving a new ticket text updates the message in open tickets of that project.",
        "**Application form**: tick «Ask these questions», add up to 5 questions (text, hint, long/short, required, max length), **Save**.",
        "**Auto-close**: number of days without messages (2–90), empty = never."
      ]],
      ["Panel & open tickets", [
        "**Ticket panel** — post it into a text channel.",
        "**Open tickets** — status, link, **Force close** (deletes the channels).",
        "**🗄️ Archive** — filter by outcome, search by name, project, reason or answers; **Form** shows the answers."
      ]]
    ]
  },
  voice: {
    title: "Temp Voice — personal voice rooms",
    features: [["What it can do", [
      "Join a **«join to create»** channel → the bot creates your own room and moves you there.",
      "The room copies the permissions of its «join to create» channel and is deleted when empty.",
      "The owner can rename, set a user limit, lock/unlock, permit or kick members; anyone can **claim** a room whose owner left.",
      "**Room defaults** per member: name, limit, locked — applied every time their room is created."
    ]]],
    discord: [
      ["Members", [
        "Join the «➕ Create room» channel → you get your room.",
        "Use the **Room controls** panel or `/room rename|limit|lock|unlock|permit|kick|claim`; `/room panel` shows the controls just for you.",
        "`/room defaults` saves your current room's name, limit and lock for next time (`reset: true` forgets them).",
        "Discord allows 2 renames per 10 minutes."
      ]],
      ["Admin commands", [
        "`/tempvoice create|add|remove|panel|list`, or `/admin` → Temp Voice."
      ]]
    ],
    panel: [[ "Managing", [
      "**Create a 'join to create' channel** (name, category, room name template with `{user}`, limit) or **use an existing** voice channel.",
      "Edit the room name template and limit of each channel, or remove it (the channel itself stays).",
      "**Room control panel** — post it into a text channel.",
      "**Active rooms** — owner, who is inside, 👑 transfer ownership, delete the room."
    ]]]
  },
  texts: {
    title: "Panel texts",
    features: [["What it can do", [
      "Edit the title, text and button labels of the **LFG**, **Ticket** and **Room controls** panels.",
      "Saving updates the panels already posted in Discord. **Reset to default** restores the original text.",
      "LFG panel placeholders: `{hours}` — auto-delete hours, `{reminder}` — reminder minutes."
    ]]],
    discord: [["Where it shows", [
      "Panels posted by `/lfg-panel`, `/ticket-panel`, `/tempvoice panel`, `/admin` or this site use your text; `/room panel` too.",
      "Discord markdown works: **bold**, *italic*, `code`, lists, `-#` small text."
    ]]],
    panel: [["Editing", [
      "Change the fields of a panel → **Save & update panels**. Empty field = default text.",
      "Panels posted before the bot started remembering them: click **Find posted panels** once."
    ]]]
  },
  posts: {
    title: "Posts — bot messages",
    features: [["What it can do", [
      "**Panels** — every known panel message: link, update with the current text, delete.",
      "**Bot messages in a channel** — the last 50–500 messages of the bot with their type (panel, LFG group, ticket, other); delete one or many."
    ]]],
    discord: [["In Discord", [
      "Deleting here deletes the message in Discord. Deleting an LFG group post closes the group properly (like Force close)."
    ]]],
    panel: [["Using it", [
      "Pick a channel and the number of messages → **Load**. Tick messages → **Delete selected**.",
      "**Find posted panels** adds older panel messages to the Panels list."
    ]]]
  },
  log: {
    title: "Log",
    features: [["What is logged", [
      "Every change made on this site — who, what and with which data.",
      "In Discord: groups created/closed, roster changes, character class/note, tickets opened/approved/closed, force-closes via `/admin`.",
      "By the bot itself: expired groups, recurring groups posted or failed. The last 5000 actions are kept."
    ]]],
    discord: [["In Discord", ["Nothing to do — actions are logged automatically."]]],
    panel: [["Filters", [
      "Filter by person, module or search text. **Load more** shows older entries."
    ]]]
  },
  settings: {
    title: "Settings",
    features: [["What you can change", [
      "Time zone, auto-delete and reminder of LFG groups are in **LFG → LFG settings**.",
      "**Member roles** — who sees the Member section.",
      "**💾 Backup** (owner): download all data as a .zip or restore it (the bot restarts)."
    ]]],
    discord: [["Effect in Discord", [
      "Applies immediately to new groups and reminders; LFG panels are re-rendered with the new numbers.",
      "Already posted groups keep their auto-delete time."
    ]]],
    panel: [["Using it", ["Empty field = the value from appsettings.json (shown as the placeholder). **Save**."]]]
  },
  access: {
    title: "Access",
    features: [["What it can do", [
      "Only the bot owner sees this tab. Give people access to this panel — per section (LFG, Tickets, Temp Voice, Panel texts, Posts, Log, Settings).",
      "Changes apply at once, no re-login needed. Access itself can't be given to anyone.",
      "Server members with a **member role** (Settings → Member roles) don't need anything here — they see the Member section (Groups, My characters)."
    ]]],
    discord: [["In Discord", [
      "Panel access doesn't change anything in Discord. Discord admin commands depend on Manage Server or the bot admin roles (`/admin` → Bot admins)."
    ]]],
    panel: [["Using it", [
      "**Pick a member** or paste a Discord user ID, tick sections → **Give access**.",
      "In the list: change the ticks → **Save**, or **Remove**."
    ]]]
  }
};

// Panels & posts = Panel texts + Posts guides together
for (const k of ["features", "discord", "panel"]) HELP.content[k] = [...HELP.texts[k], ...HELP.posts[k]];

/** Text with `code` and **bold** → DOM nodes (our own content only). */
function rich(text) {
  return text.split(/(`[^`]+`|\*\*[^*]+\*\*)/).filter(Boolean).map(part =>
    part.startsWith("`") ? h("code", {}, part.slice(1, -1))
      : part.startsWith("**") ? h("b", {}, part.slice(2, -2))
      : part);
}

function guideDialog(key) {
  const guide = HELP[key];
  if (!guide) return;
  const d = dialog();
  const body = h("div", { class: "guide-body" });
  const tabsDef = [["features", "✨ What it can do"], ["discord", "💬 In Discord"], ["panel", "🖥️ In this panel"]];
  const buttons = tabsDef.map(([k, label]) => h("button", { class: "btn secondary", onclick: () => show(k) }, label));
  const show = k => {
    buttons.forEach((b, i) => b.classList.toggle("active-tab", tabsDef[i][0] === k));
    body.replaceChildren(...guide[k].map(([title, lines]) => h("div", { class: "guide-section" },
      h("h3", {}, title), h("ul", {}, lines.map(line => h("li", {}, rich(line)))))));
  };
  d.classList.add("wide");
  d.replaceChildren(
    h("div", { class: "card-head" }, h("h2", {}, `❔ ${guide.title}`), h("button", { class: "btn ghost", onclick: () => d.close() }, "✕")),
    h("div", { class: "row", style: "margin-bottom:12px" }, buttons), body);
  d.onclose = () => d.classList.remove("wide");
  show("features");
  d.showModal();
}

/** Puts the ❔ Guide button into the page heading. */
function addGuideButton(main, key) {
  if (!HELP[key]) return;
  const btn = h("button", { class: "btn secondary", onclick: () => guideDialog(key) }, "❔ Guide");
  const first = main.firstElementChild;
  if (first && first.classList.contains("card-head")) first.append(btn);
  else if (first && first.tagName === "H1") {
    const head = h("div", { class: "card-head" });
    first.replaceWith(head);
    head.append(first, btn);
  } else main.prepend(h("div", { class: "row", style: "justify-content:flex-end" }, btn));
}

// Adaptive tables: each cell gets its column name (shown as a label when a narrow card stacks the rows)
function labelTables() {
  for (const t of document.querySelectorAll("table.table")) {
    const heads = [...t.querySelectorAll("thead th")].map(th => th.textContent.trim());
    if (!heads.some(Boolean)) continue;
    for (const tr of t.querySelectorAll("tbody tr"))
      [...tr.children].forEach((td, i) => { if (heads[i] && td.dataset.label !== heads[i]) td.dataset.label = heads[i]; });
  }
}
let labelQueued = false;
new MutationObserver(() => {
  if (labelQueued) return;
  labelQueued = true;
  requestAnimationFrame(() => { labelQueued = false; labelTables(); });
}).observe(document.body, { childList: true, subtree: true });

boot();
