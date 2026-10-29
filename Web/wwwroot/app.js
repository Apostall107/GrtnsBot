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
  ["posts", "📌", "Posts", "posts"],
  ["log", "📜", "Log", "log"],
  ["settings", "⚙️", "Settings", "settings"],
  ["access", "🔑", "Access", "access"]
];
const can = perm => !perm || (state.me && state.me.permissions.includes(perm));
const visibleTabs = () => allTabs.filter(t => can(t[3]));
const PERM_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Temp Voice", texts: "📝 Panel texts", posts: "📌 Posts", log: "📜 Log", settings: "⚙️ Settings" };

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
  const page = { overview: pageOverview, lfg: pageLfg, tickets: pageTickets, voice: pageVoice, texts: pageTexts, posts: pagePosts, log: pageLog, settings: pageSettings, access: pageAccess }[state.tab];
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

  // --- Create group + schedules
  const createCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "➕ Create a group")),
    h("p", { class: "muted small" }, "Posted like a normal /lfg group. You are the organizer."));
  const createBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  createCard.append(createBody);
  const recCard = h("div", { class: "card" });
  main.append(createCard, recCard);

  // --- Groups
  const groupsCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active groups")));
  const groupsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  groupsCard.append(groupsBody);
  main.append(groupsCard);

  let acts = [];
  try {
    acts = await gApi("GET", "/lfg/activities");
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

  // Create form
  const form = lfgForm(acts, { mode: "once" });
  const publish = h("button", { class: "btn" }, "📣 Publish");
  publish.addEventListener("click", async () => {
    const res = await act(publish, () => gApi("POST", "/lfg/create", form.value()));
    if (res === undefined) return;
    toast(res.dmsStarted ? "Group published. Sending DMs to the activity role…" : "Group published.");
    refresh();
  });
  createBody.replaceChildren(form.el, h("div", { class: "row" }, publish));

  // Schedules
  renderRecurring(recCard, acts);

  try {
    const groups = await gApi("GET", "/lfg/groups");
    groupsBody.replaceChildren(groups.length ? h("div", { class: "grid2" }, groups.map(groupCard)) : empty("No active groups."));
  } catch (e) { groupsBody.replaceChildren(empty(e.message)); }
}

const SLOT_ROLES = [["Any", "🎲 All"], ["Dps", "⚔️ DD"], ["Tank", "🛡️ Tank"], ["Healer", "💚 Sup"]];
const DAYS = [[1, "Mon"], [2, "Tue"], [3, "Wed"], [4, "Thu"], [5, "Fri"], [6, "Sat"], [0, "Sun"]];

/** LFG form: channel, activity, slots + (once) date/time or (recurring) days/time/post-before. */
function lfgForm(acts, opts) {
  const v = opts.initial || {};
  const channel = channelSelect("text", "Channel…");
  if (v.channelId) channel.value = v.channelId;

  const activity = h("select", {},
    h("option", { value: "" }, "✏️ Custom activity (no ping)"),
    acts.map(a => h("option", { value: a.key }, `${a.name}  ·  @${a.role.name}`)));
  const custom = h("input", { placeholder: "Activity name", maxlength: "100" });
  if (v.activity) {
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

  const dm = h("input", { type: "checkbox", checked: v.notifyByDm ?? true });
  const seat = h("input", { type: "checkbox", checked: v.seatOwner ?? true });

  const fields = [
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, "Channel"), channel),
      h("div", { class: "field" }, h("label", {}, "Activity"), activity, custom)),
    h("div", { class: "field" }, h("label", {}, "Slots"), h("div", { class: "row", style: "margin-bottom:6px" }, size), slotBox)
  ];

  let extra;
  if (opts.mode === "once") {
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

  fields.push(h("div", { class: "row", style: "margin-bottom:12px" },
    h("label", { class: "row" }, dm, "DM members with the activity role"),
    h("label", { class: "row" }, seat, "Organizer takes slot 1")));

  return {
    el: h("div", {}, fields),
    value: () => ({
      channelId: channel.value || null, activityKey: activity.value || null, customName: activity.value ? null : custom.value,
      slots: slots.slice(), notifyByDm: dm.checked, seatOwner: seat.checked, ...extra()
    })
  };
}

async function renderRecurring(card, acts) {
  const add = h("button", { class: "btn" }, "➕ Add schedule");
  add.addEventListener("click", () => recurringDialog(acts, null));
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
      edit.addEventListener("click", () => recurringDialog(acts, r));
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

function recurringDialog(acts, r) {
  const d = dialog();
  const form = lfgForm(acts, { mode: "recurring", initial: r || {} });
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

/** Class of a character: the role's emoji, otherwise a small role chip. */
function classMark(e) {
  if (!e.classRole) return null;
  if (e.classIcon) return h("span", { title: `@${e.classRole.name}` }, e.classIcon);
  return roleChip(e.classRole);
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
    h("div", { class: "muted small", style: "margin-bottom:8px" }, `Auto-delete ${fmtRelative(g.expiresAt)}`),
    h("ul", { class: "slots" }, g.slots.map((s, i) => h("li", {},
      h("span", { class: "num" }, i + 1), h("span", {}, s.emoji),
      s.member ? h("span", {}, h("span", { class: "row", style: "gap:4px" }, classMark(s), member(s.member)),
        s.comment ? h("div", { class: "muted small" }, `💬 ${s.comment}`) : null) : h("span", { class: "free" }, `free (${s.slotRole})`),
      s.character ? h("span", { class: "char" }, s.character) : h("span", { class: "char" }),
      s.member ? h("span", {},
        tool("✏️", "Change character", rename(s.member, s.character)),
        s.member.id !== g.owner.id ? tool("⬇️", "Move to reserve", () => roster("toReserve", s.member, "Moved to the reserve.")) : null,
        s.member.id !== g.owner.id ? tool("✖️", "Remove from the group", async () => {
          if (await confirmDialog(`Remove ${s.member.name}?`, "Their slot becomes free.", "Remove")) roster("remove", s.member, "Removed.");
        }) : null) : null))),
    g.reserve.length ? h("div", { style: "margin-top:8px" }, h("div", { class: "muted small" }, `Reserve (${g.reserve.length})`),
      h("ul", { class: "slots" }, g.reserve.map(r => h("li", {}, h("span", {}, r.emoji),
        h("span", {}, h("span", { class: "row", style: "gap:4px" }, classMark(r), member(r.member)),
          r.comment ? h("div", { class: "muted small" }, `💬 ${r.comment}`) : null),
        h("span", { class: "char" }, r.character || ""),
        h("span", {},
          tool("⬆️", "Move into the group (sends a DM)", () => roster("promote", r.member, "Moved into the group, DM sent.")),
          tool("✏️", "Change character", rename(r.member, r.character)),
          tool("✖️", "Remove from the reserve", () => roster("removeReserve", r.member, "Removed from the reserve."))))))) : null,
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
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  let s;
  try { s = await api("GET", "/api/settings"); } catch (e) { holder.replaceChildren(empty(e.message)); return; }

  const zones = h("datalist", { id: "zones" }, s.zones.map(z => h("option", { value: z })));
  const zone = h("input", { list: "zones", placeholder: s.defaults.timeZone, value: s.timeZone || "", style: "width:260px" });
  const hours = h("input", { type: "number", min: "1", max: "72", placeholder: String(s.defaults.autoDeleteHours), value: s.autoDeleteHours ?? "", style: "width:120px" });
  const reminder = h("input", { type: "number", min: "0", max: "180", placeholder: String(s.defaults.reminderMinutes), value: s.reminderMinutes ?? "", style: "width:120px" });
  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    const res = await act(save, () => api("PUT", "/api/settings", {
      timeZone: zone.value.trim() || null,
      autoDeleteHours: hours.value === "" ? null : Number(hours.value),
      reminderMinutes: reminder.value === "" ? null : Number(reminder.value)
    }));
    if (res === undefined) return;
    toast(`Saved: ${res.timeZone}, auto-delete ${res.autoDeleteHours} h, reminder ${res.reminderMinutes ? res.reminderMinutes + " min" : "off"}.`);
    refresh();
  });

  holder.replaceChildren(h("div", { class: "card" },
    h("p", { class: "muted small" }, "Empty field = the value from appsettings.json (shown as the placeholder). Changes apply immediately."),
    zones,
    h("div", { class: "field" }, h("label", {}, "Server time zone"), zone,
      h("span", { class: "muted small" }, `Now: ${s.current.timeZone}. Used for LFG start times and recurring groups (IANA name, e.g. Europe/Kyiv).`)),
    h("div", { class: "field" }, h("label", {}, "Delete LFG posts after (hours)"), hours,
      h("span", { class: "muted small" }, `Now: ${s.current.autoDeleteHours} h after the start (or creation if no time). Applies to new groups.`)),
    h("div", { class: "field" }, h("label", {}, "Reminder before the start (minutes, 0 = off)"), reminder,
      h("span", { class: "muted small" }, `Now: ${s.current.reminderMinutes ? s.current.reminderMinutes + " min" : "off"}.`)),
    save));
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
        "From this panel: create groups, **recurring groups** posted on a schedule, edit the roster, force-close."
      ]]
    ],
    discord: [
      ["Create a group", [
        "Click **Create LFG** on the LFG panel (or use `/lfg`). A menu only you can see opens.",
        "Choose the size → the activity (or «Custom activity», no ping) → select slots and set their roles → **Next**.",
        "Pick the date and time (or «No time») → **Publish**. You take slot 1 with your last used character."
      ]],
      ["Sign up", [
        "Click a role button under the group post, then pick a saved character or **New character**.",
        "Under the reply you can pick your **class role** and **add a note** — saved for that character and shown in the post.",
        "Later: `/character class name role` and `/character note name text` (empty = remove). `/character list` shows them.",
        "Click the same role again to switch character, another role to switch role. **Leave** frees your slot.",
        "Grey role button = no free slot → you go to the **reserve**."
      ]],
      ["Organizer", [
        "⚙️ **Manage** — move members between the group and the reserve (moved-in members get a DM).",
        "📋 **Invite to group** — `/invite` commands, 🏰 **Invite to dungeon** — plain names (anyone in the group can use both).",
        "**Close** — shows the group invites, then deletes the post."
      ]],
      ["DMs and admin commands", [
        "🔔 / 🔕 under the panel or `/lfg-dm` — turn group DMs on or off for yourself.",
        "Admins: `/lfg-panel` posts the panel, `/activity add|remove|list` manages activities, `/admin` → LFG."
      ]]
    ],
    panel: [
      ["Activities & panel", [
        "**Activities** — add a name + the role to ping, or remove one. Posted groups are not affected.",
        "**LFG panel** — pick a text channel and **Post** the message with the Create LFG button."
      ]],
      ["Create a group", [
        "Channel, activity (or a custom name), number of slots and the role of each slot.",
        "Date and time are in the server time zone (Settings); leave both empty for «no time».",
        "Options: DM the activity role, organizer (you) takes slot 1. **Publish**."
      ]],
      ["Recurring groups", [
        "**Add schedule**: the same form + days of the week, start time, how many hours before the start to post, the organizer, on/off.",
        "The table shows the next post and start. If a post fails (no access to the channel) that occurrence is skipped and the error is shown."
      ]],
      ["Active groups", [
        "Each card shows the roster with class and notes. Member tools: ✏️ change character, ⬇️ to reserve, ✖️ remove.",
        "Reserve tools: ⬆️ into the group (DM), ✏️, ✖️. **Force close** deletes the post. Every change updates the Discord post at once."
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
      "Per project: leadership roles, roles given on Approve, the category for channels and a custom text in the ticket message."
    ]]],
    discord: [
      ["Applicant", [
        "Click **Create ticket** on the ticket panel → choose the project.",
        "Talk with the leadership in the private channels. **Close** ends the ticket."
      ]],
      ["Leadership", [
        "**Approve** gives the roles and closes the ticket. If a role can't be given (bot role too low), the ticket stays open."
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
        "Saving a new ticket text updates the message in open tickets of that project."
      ]],
      ["Panel & open tickets", [
        "**Ticket panel** — post it into a text channel.",
        "**Open tickets** — status, link, **Force close** (deletes the channels)."
      ]]
    ]
  },
  voice: {
    title: "Temp Voice — personal voice rooms",
    features: [["What it can do", [
      "Join a **«join to create»** channel → the bot creates your own room and moves you there.",
      "The room copies the permissions of its «join to create» channel and is deleted when empty.",
      "The owner can rename, set a user limit, lock/unlock, permit or kick members; anyone can **claim** a room whose owner left."
    ]]],
    discord: [
      ["Members", [
        "Join the «➕ Create room» channel → you get your room.",
        "Use the **Room controls** panel or `/room rename|limit|lock|unlock|permit|kick|claim`; `/room panel` shows the controls just for you.",
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
      "Server **time zone** — used for LFG start times and recurring groups.",
      "After how many hours LFG posts are deleted (counted from the start, or creation without a time).",
      "How many minutes before the start the reminder is sent (0 = off)."
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
      "Changes apply at once, no re-login needed. Access itself can't be given to anyone."
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

boot();
