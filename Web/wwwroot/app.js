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
    onRemove ? h("button", { title: "Remove", onclick: async () => {
      if (await confirmDialog(`Remove @${r.name}?`, "It is removed from this list (saved when you save the form).", "Remove")) onRemove();
    } }, "×") : null);
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

/** Tabs row (projects etc.): remembers the choice in storeKey and reloads the page. */
function tabsRow(items, current, storeKey, ...extra) {
  return h("div", { class: "tabs" }, items.map(([k, label]) =>
    h("button", { class: `btn secondary${k === current ? " active-tab" : ""}`, onclick: () => { store.set(storeKey, k); refresh(); } }, label)),
    ...extra);
}

/** Pick several roles at once (checkboxes + search). Resolves with the chosen roles or null. */
function pickRolesDialog(title, exclude = [], pool = state.roles) {
  return new Promise(resolve => {
    const d = dialog();
    const search = h("input", { placeholder: "Search roles…", style: "width:100%" });
    const items = pool.filter(r => !exclude.includes(r.id)).map(r => {
      const cb = h("input", { type: "checkbox" });
      return { r, cb, el: h("label", {}, cb, roleChip(r)) };
    });
    const list = h("div", { class: "check-list" }, items.length ? items.map(x => x.el) : empty("No more roles."));
    search.addEventListener("input", () => {
      const q = search.value.trim().toLowerCase();
      for (const x of items) x.el.style.display = !q || x.r.name.toLowerCase().includes(q) ? "" : "none";
    });
    const count = h("span", { class: "muted small", style: "margin-right:auto" }, "0 selected");
    list.addEventListener("change", () => { count.textContent = `${items.filter(x => x.cb.checked).length} selected`; });
    let result = null;
    const ok = h("button", { class: "btn", onclick: () => { result = items.filter(x => x.cb.checked).map(x => x.r); d.close(); } }, "➕ Add selected");
    d.replaceChildren(h("h2", {}, title), search, list,
      h("div", { class: "actions" }, count, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), ok));
    d.onclose = () => resolve(result && result.length ? result : null);
    d.showModal();
    search.focus();
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

// ================= Shared UI pieces =================

/** Tone (color) of an icon's module: sections are told apart by color, not only by text. */
const ICON_TONES = {
  "users": "blue", "users-round": "violet", "swords": "violet", "circle-plus": "violet", "user-round": "cyan", "user-plus": "cyan",
  "ticket": "amber", "inbox": "amber", "file-pen-line": "amber", "headphones": "teal", "volume-2": "teal", "sliders-horizontal": "teal",
  "megaphone": "pink", "chart-column": "green", "circle-check": "green", "crown": "amber", "bell": "amber"
};
const toneOf = ic => `tone-${ICON_TONES[ic] || "violet"}`;
function toneChip(ic, size = 16) { return h("span", { class: `tone-chip ${toneOf(ic)}` }, icon(ic, size)); }

/** Card heading with an icon and actions on the right. */
function cardHead(ic, title, ...actions) {
  return h("div", { class: "card-head" }, h("h2", { class: "with-icon" }, toneChip(ic, 16), title), ...actions);
}

/** Small uppercase label above a group of cards. */
function sectionLabel(text) { return h("div", { class: "section-label" }, text); }

/** Statistic card: icon, label, value, supporting text; clickable when onClick is given. */
function statCard(ic, label, value, sub, onClick) {
  return h(onClick ? "button" : "div", { class: `stat ${toneOf(ic)}${onClick ? " clickable" : ""}`, onclick: onClick || null },
    h("div", { class: "stat-top" }, h("span", { class: "label" }, label), h("span", { class: "stat-icon" }, icon(ic, 16))),
    h("div", { class: "value" }, value), sub ? h("div", { class: "sub" }, sub) : null);
}

/** Empty state: what's missing and what to do next. */
function emptyState(ic, title, text, action) {
  return h("div", { class: "empty-state" }, h("span", { class: `es-icon ${toneOf(ic)}` }, icon(ic, 20)),
    h("div", {}, h("b", {}, title), text ? h("div", { class: "muted small" }, text) : null), action || null);
}

/** Capacity: "2/4" with a bar. */
function capacity(filled, size) {
  const pct = size ? Math.round(filled / size * 100) : 0;
  return h("span", { class: `capacity${filled >= size ? " full" : ""}`, title: `${filled} of ${size} slots taken` },
    h("span", { class: "cap-text" }, `${filled}/${size}`), h("span", { class: "cap-bar" }, h("span", { style: `width:${pct}%` })));
}

/** Loading placeholders. */
function skeletonLines(n) { return h("div", {}, Array.from({ length: n }, (_, i) => h("div", { class: "skeleton", style: `width:${90 - i * 15}%` }))); }
function skeletonStat() { return h("div", { class: "stat" }, h("div", { class: "skeleton", style: "width:40%" }), h("div", { class: "skeleton tall", style: "width:30%" })); }

/** Emoji at the start of a button/tab label → a Lucide icon (old labels keep working). */
const EMOJI_ICONS = { "➕": "plus", "💾": "save", "🗑️": "trash-2", "🗑": "trash-2", "✏️": "pencil", "📌": "pin", "👁": "eye", "✅": "check",
  "✕": "x", "✖": "x", "✖️": "x", "🔍": "search", "↩️": "undo-2", "📅": "calendar", "🕒": "clock", "🔊": "volume-2", "📋": "clipboard-list",
  "⚙️": "settings", "🎨": "palette", "🎮": "gamepad-2", "🌐": "globe", "📣": "megaphone", "📝": "file-pen-line", "📨": "send", "🔒": "lock",
  "🔓": "lock-open", "🔁": "repeat", "📁": "folder", "📄": "file-text", "📢": "megaphone", "🔄": "refresh-cw" };
function iconizeButtons(root) {
  for (const btn of root.querySelectorAll(".btn, .tabs button")) {
    const first = btn.firstChild;
    if (!first || first.nodeType !== Node.TEXT_NODE) continue;
    const m = /^(\S+?)️?\s+/u.exec(first.textContent);
    if (!m) continue;
    const key = Object.keys(EMOJI_ICONS).find(k => first.textContent.startsWith(k));
    if (!key) continue;
    first.textContent = first.textContent.slice(key.length).replace(/^️?\s*/u, "");
    btn.insertBefore(icon(EMOJI_ICONS[key], 16), first);
  }
}
let iconizeQueued = false;
new MutationObserver(() => {
  if (iconizeQueued) return;
  iconizeQueued = true;
  requestAnimationFrame(() => { iconizeQueued = false; iconizeButtons(document.body); });
}).observe(document.body, { childList: true, subtree: true });

// ================= Mentions: @ / # suggestions in text fields =================

const knownUsers = new Map(); // id → name (for showing <@id> as @name)
const MENTION_RE = /<@&(\d+)>|<@!?(\d+)>|<#(\d+)>|@everyone|@here/g;

/** Name of a mention code for display. */
function mentionName(m, roleId, userId, channelId) {
  if (roleId) { const r = state.roles.find(x => x.id === roleId); return { cls: "role", text: `@${r ? r.name : "unknown role"}` }; }
  if (userId) return { cls: "user", text: `@${knownUsers.get(userId) || "user " + userId.slice(-4)}` };
  if (channelId) {
    const c = [...state.channels.text, ...state.channels.voice].find(x => x.id === channelId);
    return { cls: "channel", text: c ? `#${c.name}` : "#unknown" };
  }
  return { cls: "role", text: m };
}

/** Text with mention codes shown as highlighted names. */
function renderMentions(text) {
  const out = [];
  let last = 0;
  for (const m of text.matchAll(MENTION_RE)) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const n = mentionName(m[0], m[1], m[2], m[3]);
    out.push(h("span", { class: `mention ${n.cls}`, title: m[0] }, n.text));
    last = m.index + m[0].length;
  }
  out.push(text.slice(last));
  return out;
}

/** Caret position (px) inside a textarea — via a hidden mirror element. */
function caretCoords(ta) {
  const cs = getComputedStyle(ta);
  const mirror = h("div", {});
  for (const p of ["fontFamily", "fontSize", "fontWeight", "lineHeight", "letterSpacing", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
    "borderTopWidth", "borderLeftWidth", "boxSizing", "whiteSpace", "wordWrap", "tabSize"]) mirror.style[p] = cs[p];
  Object.assign(mirror.style, { position: "absolute", visibility: "hidden", whiteSpace: "pre-wrap", wordWrap: "break-word", width: `${ta.clientWidth}px`, top: "0", left: "-9999px" });
  mirror.textContent = ta.value.slice(0, ta.selectionStart);
  const marker = h("span", {}, "​");
  mirror.append(marker);
  document.body.append(mirror);
  const pos = { top: marker.offsetTop - ta.scrollTop, left: marker.offsetLeft, line: parseFloat(cs.lineHeight) || 20 };
  mirror.remove();
  return pos;
}

/**
 * Discord-like suggestions in a textarea: type @ → @everyone/@here, roles, people; # → channels.
 * Arrows + Enter/Tab pick, Esc closes. Inserts <@&role>, <@user>, <#channel>. onInsert(kind) is called after an insert.
 */
function attachMentions(ta, onInsert) {
  const wrap = h("div", { class: "mention-wrap" });
  const pop = h("div", { class: "mention-pop", style: "display:none" });
  ta.replaceWith(wrap);
  wrap.append(ta, pop);
  let items = [], active = 0, trigger = null, timer = 0, seq = 0;

  const close = () => { pop.style.display = "none"; trigger = null; items = []; };
  const draw = () => {
    // Nothing yet (people are still being searched) — hide the list but keep waiting for results
    if (!items.length) { pop.style.display = "none"; return; }
    pop.replaceChildren(...items.map((it, i) => h("div", { class: `mention-item${i === active ? " active" : ""}`,
      onmousedown: e => { e.preventDefault(); pick(i); } }, it.icon, h("span", {}, it.label), it.hint ? h("span", { class: "muted small", style: "margin-left:auto" }, it.hint) : null)));
    const c = caretCoords(ta);
    pop.style.top = `${Math.min(c.top + c.line + 4, ta.clientHeight)}px`;
    pop.style.left = `${Math.min(c.left, Math.max(0, ta.clientWidth - 260))}px`;
    pop.style.display = "";
  };
  const pick = i => {
    const it = items[i];
    if (!it || !trigger) return;
    const before = ta.value.slice(0, trigger.start), after = ta.value.slice(ta.selectionStart);
    const insert = it.code + " ";
    ta.value = before + insert + after;
    const caret = before.length + insert.length;
    ta.setSelectionRange(caret, caret);
    close();
    ta.focus();
    ta.dispatchEvent(new Event("input"));
    if (onInsert) onInsert(it.kind);
  };
  const update = () => {
    const upto = ta.value.slice(0, ta.selectionStart);
    const m = /(^|[\s(])([@#])([^\s@#<>]{0,32})$/.exec(upto);
    if (!m) { close(); return; }
    trigger = { start: upto.length - m[3].length - 1, sign: m[2], q: m[3].toLowerCase() };
    const q = trigger.q;
    active = 0;
    if (trigger.sign === "#") {
      items = [...state.channels.text.map(c => ({ ...c, voice: false })), ...state.channels.voice.map(c => ({ ...c, voice: true }))]
        .filter(c => c.name.toLowerCase().includes(q)).slice(0, 10)
        .map(c => ({ kind: "channel", code: `<#${c.id}>`, icon: h("span", { class: "muted" }, c.voice ? "🔊" : "#"), label: c.name, hint: c.category || "" }));
      draw();
      return;
    }
    const special = ["everyone", "here"].filter(x => x.startsWith(q))
      .map(x => ({ kind: "everyone", code: `@${x}`, icon: h("span", {}, "📢"), label: `@${x}`, hint: x === "here" ? "online members" : "all members" }));
    const roles = state.roles.filter(r => r.name.toLowerCase().includes(q)).slice(0, 8)
      .map(r => ({ kind: "role", code: `<@&${r.id}>`, icon: h("span", { class: "swatch-dot", style: r.color ? `background:${r.color}` : null }), label: `@${r.name}`, hint: "role" }));
    items = [...special, ...roles];
    draw();
    // People: searched on the server (debounced)
    clearTimeout(timer);
    const my = ++seq;
    timer = setTimeout(async () => {
      try {
        const members = await gApi("GET", `/members?q=${encodeURIComponent(q)}`);
        if (my !== seq || !trigger) return;
        for (const m of members) knownUsers.set(m.id, m.name);
        const people = members.slice(0, 8).map(m => ({ kind: "user", code: `<@${m.id}>`,
          icon: m.avatarUrl ? h("img", { class: "avatar sm", src: m.avatarUrl, alt: "" }) : h("span", { class: "avatar sm" }),
          label: m.name, hint: m.username ? `@${m.username}` : "" }));
        items = [...special, ...roles, ...people];
        draw();
      } catch { /* suggestions are optional */ }
    }, 200);
  };

  ta.addEventListener("input", update);
  ta.addEventListener("click", update);
  ta.addEventListener("blur", () => setTimeout(close, 150));
  ta.addEventListener("keydown", e => {
    if (pop.style.display === "none" || !items.length) return;
    if (e.key === "ArrowDown") { active = (active + 1) % items.length; draw(); e.preventDefault(); }
    else if (e.key === "ArrowUp") { active = (active - 1 + items.length) % items.length; draw(); e.preventDefault(); }
    else if (e.key === "Enter" || e.key === "Tab") { pick(active); e.preventDefault(); }
    else if (e.key === "Escape") { close(); e.preventDefault(); }
  });
  return wrap;
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
    // Search + tick several roles at once (the plain dropdown was too long)
    const sel = h("button", { class: "btn secondary", type: "button" }, "➕ Add roles…");
    sel.addEventListener("click", async () => {
      const picked = await pickRolesDialog("Add roles", ids, pool);
      if (picked) { ids.push(...picked.map(r => r.id)); draw(); }
    });
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
// [key, Lucide icon, label, required permission, nav section]
const allTabs = [
  ["home", "house", "Home", "member", "Member"],
  ["play", "users-round", "Groups", "member", "Member"],
  ["chars", "user-round", "My characters", "member", "Member"],
  ["lfg", "swords", "LFG", "lfg", "Community modules"],
  ["tickets", "ticket", "Tickets", "tickets", "Community modules"],
  ["voice", "headphones", "Temp Voice", "voice", "Community modules"],
  ["content", "layout-template", "Panels & posts", "content", "Community modules"],
  ["announce", "megaphone", "Announcements", "announce", "Community modules"],
  ["overview", "layout-dashboard", "Dashboard", "admin", "Administration"],
  ["log", "scroll-text", "Log", "log", "Administration"],
  ["settings", "settings", "Settings", "settings", "Administration"],
  ["access", "key-round", "Access", "access", "Administration"]
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
const PERM_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Temp Voice", texts: "📝 Panel texts", posts: "📌 Posts", announce: "✍️ Announcements", log: "📜 Log", settings: "⚙️ Settings" };

function renderLogin(message) {
  const params = new URLSearchParams(location.search);
  const failed = params.get("login") === "failed";
  if (failed) history.replaceState(null, "", "/");
  document.getElementById("app").replaceChildren(
    h("div", { class: "card login" },
      h("img", { class: "logo-img", src: "/favicon.png", alt: "" }),
      h("h1", {}, "Greatness Bot"),
      h("p", {}, "Discord bot management panel. Log in with your Discord account."),
      failed ? h("p", { class: "error-text" }, "Login was cancelled or failed. Try again.") : null,
      message ? h("p", { class: "error-text" }, message) : null,
      h("a", { class: "btn lg", href: "/login" }, icon("log-in", 18), "Login with Discord")));
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
  const connected = state.connection === "Connected";
  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = (visibleTabs()[0] || ["overview"])[0];
  const collapsed = store.get("gb.navCollapsed") === "1";

  // ----- Sidebar: sections, Lucide icons, active route; collapses to icons on desktop, drawer on mobile
  const layout = h("div", { class: `layout${collapsed ? " collapsed" : ""}` });
  const closeDrawer = () => layout.classList.remove("drawer-open");
  const NAV_TONES = { home: "violet", play: "violet", chars: "cyan", lfg: "violet", tickets: "amber", voice: "teal", content: "blue",
    announce: "pink", overview: "green", log: "blue", settings: "cyan", access: "amber" };
  const navButton = ([key, ic, label]) => h("button", {
    class: `nav-item tone-${NAV_TONES[key] || "violet"}${state.tab === key ? " active" : ""}`, title: label, "aria-current": state.tab === key ? "page" : null,
    onclick: () => { state.tab = key; store.set("gb.tab", key); renderShell(); }
  }, icon(ic, 18), h("span", { class: "nav-label" }, label));
  const navItems = [];
  let section = null;
  for (const t of visibleTabs()) {
    if (t[4] !== section) { section = t[4]; navItems.push(h("div", { class: "nav-section" }, h("span", { class: "nav-section-text" }, section))); }
    navItems.push(navButton(t));
  }
  const collapseBtn = h("button", { class: "nav-item nav-collapse", title: collapsed ? "Expand menu" : "Collapse menu",
    onclick: () => { store.set("gb.navCollapsed", collapsed ? "0" : "1"); renderShell(); } },
    icon(collapsed ? "panel-left-open" : "panel-left-close", 18), h("span", { class: "nav-label" }, "Collapse"));
  const nav = h("nav", { class: "nav", "aria-label": "Sections" }, navItems, h("div", { class: "nav-footer" }, collapseBtn));
  const main = h("main", { class: "main" });
  layout.append(nav, h("div", { class: "nav-backdrop", onclick: closeDrawer }), main);

  // ----- Header: menu (mobile), brand, server, bot status, account menu
  const guildSel = h("select", { "aria-label": "Server", onchange: e => selectGuild(e.target.value) },
    state.guilds.map(g => h("option", { value: g.id, selected: g.id === state.guildId }, g.name)));
  const userPop = h("div", { class: "user-pop", style: "display:none" },
    h("div", { class: "who" }, h("b", {}, state.me.name), h("span", { class: "muted small" }, isAdmin() ? "Admin access" : "Member")),
    h("button", { class: "menu-item danger", onclick: logout }, icon("log-out", 16), "Log out"));
  const userBtn = h("button", { class: "user-button", "aria-haspopup": "menu", "aria-expanded": "false" },
    state.me.avatarUrl ? h("img", { class: "avatar", src: state.me.avatarUrl, alt: "" }) : h("span", { class: "avatar" }),
    h("span", { class: "name" }, state.me.name), icon("chevron-down", 16, "muted"));
  const toggleUser = open => {
    userPop.style.display = open ? "" : "none";
    userBtn.setAttribute("aria-expanded", open ? "true" : "false");
  };
  userBtn.addEventListener("click", e => { e.stopPropagation(); toggleUser(userPop.style.display === "none"); });
  document.onclick = e => { if (!userPop.contains(e.target)) toggleUser(false); };

  document.getElementById("app").replaceChildren(
    h("header", { class: "topbar" },
      h("button", { class: "btn ghost icon-only menu-toggle", title: "Menu", "aria-label": "Menu",
        onclick: () => layout.classList.toggle("drawer-open") }, icon("menu", 20)),
      h("div", { class: "brand" }, h("span", { class: "brand-logo" }, h("img", { src: "/favicon.png", alt: "" })),
        h("span", { class: "brand-text" }, h("span", { class: "brand-name" }, "Greatness Bot"))),
      h("div", { class: "spacer" }),
      state.guilds.length ? h("div", { class: "server-select" }, icon("server", 16), guildSel) : h("span", { class: "muted" }, "The bot is on no servers"),
      h("span", { class: `status-pill ${connected ? "ok" : "bad"}`, title: `Bot: ${state.connection || "unknown"}` },
        h("span", { class: "dot" }), h("span", { class: "label" }, connected ? "Online" : (state.connection || "Offline"))),
      h("div", { class: "user-menu" }, userBtn, userPop)),
    layout);

  if (!state.guildId) { main.append(empty("No servers available for your account.")); return; }
  if (!visibleTabs().length) { main.append(empty("You have no sections on this server. Ask an admin for the member role.")); return; }
  const page = { home: pageHome, content: pageContent, play: pagePlay, chars: pageChars, overview: pageOverview, lfg: pageLfg, tickets: pageTickets, voice: pageVoice, texts: pageTexts, posts: pagePosts, announce: pageAnnounce, log: pageLog, settings: pageSettings, access: pageAccess }[state.tab];
  page(main);
  addGuideButton(main, state.tab);
  decorateHeading(main);
}

/** Page heading: the section's icon instead of an emoji; Refresh / Guide with icons. */
function decorateHeading(main) {
  const head = main.firstElementChild;
  if (!head || !head.classList.contains("card-head")) return;
  head.classList.add("page-head");
  const title = head.querySelector("h1");
  const tab = allTabs.find(t => t[0] === state.tab);
  if (title && tab) {
    const text = title.textContent.replace(/^[^\p{L}\p{N}]+/u, "").trim();
    title.replaceChildren(icon(tab[1], 24), text);
  }
  for (const btn of head.querySelectorAll(".btn-refresh")) btn.replaceChildren(icon("refresh-cw", 16), "Refresh");
  for (const btn of head.querySelectorAll(".btn-guide")) btn.replaceChildren(icon("circle-help", 16), "Guide");
}

/** Re-renders the current page only (keeps the shell). */
function refresh() { renderShell(); }

// ================= Announcements: posts from the bot =================

async function pageAnnounce(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "✍️ Announcements"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));

  let editing = null; // scheduled post being edited
  const channel = h("select", {}, h("option", { value: "" }, "Channel…"),
    h("optgroup", { label: "Text channels" }, state.channels.text.filter(c => c.canPost)
      .map(c => h("option", { value: c.id }, `# ${c.name}${c.category ? `  ·  ${c.category}` : ""}`))),
    h("optgroup", { label: "Voice channels (their chat)" }, state.channels.voice
      .map(c => h("option", { value: c.id }, `🔊 ${c.name}${c.category ? `  ·  ${c.category}` : ""}`))));
  const text = h("textarea", { maxlength: "20000", placeholder: "Type @ to mention a role or a person, # for a channel. Markdown works: **bold**, *italic*, # Heading, - lists, > quotes, ```code```", style: "min-height:260px" });
  const counter = h("span", { class: "muted small" });
  // Mentions written in the text, shown by name (the text itself keeps Discord codes like <@&123>)
  const inText = h("div", { class: "small", style: "margin-top:4px" });
  const syncCounter = () => {
    const n = text.value.trim().length;
    counter.textContent = `${n} / 20000 characters${n > 2000 ? ` · will be split into ~${Math.ceil(n / 1900)} messages` : ""}`;
    const found = [...new Set([...text.value.matchAll(MENTION_RE)].map(m => m[0]))];
    inText.replaceChildren(...(found.length ? [h("span", { class: "muted" }, "Mentions in the text: "), ...found.flatMap(code => renderMentions(code).concat(" "))] : []));
  };
  text.addEventListener("input", syncCounter);
  syncCounter();
  const pings = h("input", { type: "checkbox" });
  // @ suggestions: a role/person picked in the text pings by default (the box can be unticked)
  const textBox = attachMentions(text, kind => {
    if (kind !== "channel" && !pings.checked) {
      pings.checked = true;
      toast("Mentions in the text will ping (untick «Ping mentions written in the text» to keep them silent).");
    }
  });

  // Whom to ping: @everyone/@here, roles, people — a mention line at the top of the post
  const ping = { everyone: "", roles: [], users: [] };
  const everyoneSel = h("select", {}, h("option", { value: "" }, "No @everyone"), h("option", { value: "everyone" }, "@everyone"), h("option", { value: "here" }, "@here"));
  everyoneSel.addEventListener("change", () => { ping.everyone = everyoneSel.value; });
  const pingChips = h("div", { style: "margin-bottom:6px" });
  const drawPing = () => {
    everyoneSel.value = ping.everyone;
    const chips = [
      ...ping.roles.map((r, i) => roleChip(r, () => { ping.roles.splice(i, 1); drawPing(); })),
      ...ping.users.map((m, i) => h("span", { class: "chip" }, `@${m.name}`, h("button", { title: "Remove", onclick: async () => {
        if (await confirmDialog(`Don't ping ${m.name}?`, "Removed from the ping list.", "Remove")) { ping.users.splice(i, 1); drawPing(); }
      } }, "×")))];
    pingChips.replaceChildren(...(chips.length ? chips : [h("span", { class: "muted small" }, "Nobody — add roles or people below.")]));
  };
  const addRoles = h("button", { class: "btn secondary", type: "button" }, "➕ Roles…");
  addRoles.addEventListener("click", async () => {
    const picked = await pickRolesDialog("Roles to ping", ping.roles.map(r => r.id));
    if (picked) { ping.roles.push(...picked); drawPing(); }
  });
  const addUser = h("button", { class: "btn secondary", type: "button" }, "➕ Person…");
  addUser.addEventListener("click", async () => {
    const m = await pickMemberDialog("Person to ping");
    if (m && !ping.users.some(u => u.id === m.id)) { ping.users.push(m); drawPing(); }
  });
  const setPing = p => {
    for (const u of p.pingUsers || []) knownUsers.set(u.id, u.name);
    ping.everyone = p.pingEveryone || "";
    ping.roles = (p.pingRoles || []).slice();
    ping.users = (p.pingUsers || []).slice();
    drawPing();
  };
  drawPing();
  const pingBody = () => ({ pingEveryone: ping.everyone || null, pingRoleIds: ping.roles.map(r => r.id), pingUserIds: ping.users.map(u => u.id) });
  const when = h("select", {}, h("option", { value: "now" }, "Send now"), h("option", { value: "later" }, "📅 Schedule"));
  const date = h("input", { type: "date" });
  const time = h("input", { type: "time" });
  const whenBox = h("div", { class: "row", style: "display:none" }, date, time, h("span", { class: "muted small" }, "server time zone (LFG settings)"));
  when.addEventListener("change", () => { whenBox.style.display = when.value === "later" ? "" : "none"; });
  const pad = n => String(n).padStart(2, "0");
  const setWhen = ms => {
    const t = new Date(ms);
    date.value = `${t.getFullYear()}-${pad(t.getMonth() + 1)}-${pad(t.getDate())}`;
    time.value = `${pad(t.getHours())}:${pad(t.getMinutes())}`;
  };

  const title = h("h2", {}, "New post");
  const cancelEdit = h("button", { class: "btn secondary", style: "display:none" }, "✕ Cancel editing");
  const go = h("button", { class: "btn" }, "👁 Preview & send");
  const resetForm = () => {
    editing = null;
    title.textContent = "New post";
    cancelEdit.style.display = "none";
    go.textContent = "👁 Preview & send";
    when.disabled = false;
  };
  cancelEdit.addEventListener("click", resetForm);

  // Preview: how the text is split → confirmation → send / schedule
  go.addEventListener("click", async () => {
    if (!channel.value) return toast("Choose a channel first.", "warn");
    const later = editing || when.value === "later";
    if (later && (!date.value || !time.value)) return toast("Pick the date and time.", "warn");
    const preview = await act(go, () => gApi("POST", "/announce/preview", { content: text.value, ...pingBody() }));
    if (!preview) return;
    const chName = channel.selectedOptions[0].textContent.split("  ·  ")[0];
    const whenText = later ? `on ${date.value} at ${time.value}` : "now";
    const ok = await new Promise(resolve => {
      const d = dialog();
      d.classList.add("wide");
      d.replaceChildren(h("h2", {}, `${editing ? "Save" : later ? "Schedule" : "Send"}: ${preview.count} message${preview.count > 1 ? "s" : ""} → ${chName}`),
        h("p", { class: "muted small" }, `${preview.length} characters · ${whenText} · ping: ${[ping.everyone && "@" + ping.everyone, ...ping.roles.map(r => "@" + r.name), ...ping.users.map(u => "@" + u.name)].filter(Boolean).join(", ") || "nobody"}${pings.checked ? " + all mentions in the text" : ""}. `,
          preview.count > 1 ? "The text is longer than Discord allows (2000) — it is split at paragraphs/lines; code blocks are kept whole." : ""),
        h("div", { class: "guide-body" }, preview.blocks.map((b, i) => h("div", { class: "card", style: "padding:10px;margin-bottom:8px" },
          h("div", { class: "muted small", style: "margin-bottom:4px" }, `Message ${i + 1}/${preview.count} · ${b.length} characters`),
          h("div", { style: "white-space:pre-wrap;word-break:break-word" }, renderMentions(b))))),
        h("div", { class: "actions" },
          h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(false); } }, "Cancel"),
          h("button", { class: "btn", onclick: () => { d.close(); resolve(true); } }, editing ? "💾 Save" : later ? "📅 Schedule" : "✅ Send")));
      d.onclose = () => { d.classList.remove("wide"); resolve(false); };
      d.showModal();
    });
    if (!ok) return;
    const body = { channelId: channel.value, content: text.value, allowMentions: pings.checked,
      date: later ? date.value : null, time: later ? time.value : null, ...pingBody() };
    const res = editing
      ? await act(go, () => gApi("PUT", `/announce/${editing}`, body), "Scheduled post saved.")
      : await act(go, () => gApi("POST", "/announce", body), r => r.scheduled ? "Post scheduled." : `Sent — ${r.sent} message(s).`);
    if (res === undefined) return;
    text.value = "";
    resetForm();
    refresh();
  });

  // Templates: save the form (channel, text, pings) and load it later
  let templates = [];
  try { templates = await gApi("GET", "/announce/templates"); } catch { /* shown below */ }
  let currentTpl = null;
  const tplSel = h("select", {}, h("option", { value: "" }, templates.length ? "📄 Load a template…" : "No templates yet"),
    templates.map(t => h("option", { value: t.key }, t.name)));
  tplSel.disabled = !templates.length;
  const applyTemplate = t => {
    currentTpl = t;
    if (t.channelId) channel.value = t.channelId;
    text.value = t.content;
    pings.checked = t.allowMentions;
    setPing(t);
    syncCounter();
  };
  tplSel.addEventListener("change", () => {
    const t = templates.find(x => x.key === tplSel.value);
    if (t) { applyTemplate(t); toast(`Template «${t.name}» loaded.`); }
  });
  const tplBody = name => ({ name, channelId: channel.value || null, content: text.value, allowMentions: pings.checked, ...pingBody() });
  const saveTpl = h("button", { class: "btn secondary", type: "button" }, "💾 Save as template");
  saveTpl.addEventListener("click", async () => {
    if (!text.value.trim()) return toast("Write the text first.", "warn");
    // Loaded template: update it or save a new one
    if (currentTpl && await confirmDialog(`Update the template «${currentTpl.name}»?`, "Cancel = save as a new template.", "Update", false)) {
      if (await act(saveTpl, () => gApi("PUT", `/announce/templates/${currentTpl.key}`, tplBody(currentTpl.name)), "Template updated.") !== undefined) refresh();
      return;
    }
    const name = await promptDialog("Template name", currentTpl ? `${currentTpl.name} (copy)` : "");
    if (!name) return;
    if (await act(saveTpl, () => gApi("PUT", "/announce/templates/new", tplBody(name)), "Template saved.") !== undefined) refresh();
  });

  main.append(h("div", { class: "card" }, h("div", { class: "card-head" }, title, cancelEdit),
    h("p", { class: "muted small" }, "The bot posts your text in a channel (or a voice channel's chat). Longer than 2000 characters — split into several messages automatically. You see the preview and confirm before anything is sent."),
    h("div", { class: "row", style: "margin-bottom:12px" }, h("div", { class: "grow" }, tplSel), saveTpl),
    h("div", { class: "field" }, h("label", {}, "Channel"), channel),
    h("div", { class: "field" }, h("label", {}, "Text — type @ or # for suggestions"), textBox, counter, inText),
    h("div", { class: "field" }, h("label", {}, "🔔 Ping (a line with mentions at the top of the post)"), pingChips,
      h("div", { class: "row" }, everyoneSel, addRoles, addUser)),
    h("label", { class: "row", style: "margin-bottom:10px" }, pings, "Ping mentions written in the text (@ suggestions turn this on) — off: they are shown but nobody is notified"),
    h("div", { class: "row", style: "margin-bottom:12px" }, when, whenBox),
    h("div", { class: "row" }, go)));

  const tplCard = h("div", { class: "card" }, h("h2", {}, "📄 Templates"),
    h("p", { class: "muted small" }, "Saved channel + text + pings. Fill the form and press 💾 Save as template; load one with 📄 Load a template."),
    templates.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Name"), h("th", {}, "Channel"), h("th", {}, "Text"), h("th", {}))),
      h("tbody", {}, templates.map(t => {
        const use = h("button", { class: "btn secondary", onclick: () => { applyTemplate(t); tplSel.value = t.key; window.scrollTo({ top: 0, behavior: "smooth" }); } }, "Use");
        const rename = h("button", { class: "btn ghost", title: "Rename" }, "✏️");
        rename.addEventListener("click", async () => {
          const name = await promptDialog("Rename template", t.name);
          if (!name || name === t.name) return;
          if (await act(rename, () => gApi("PUT", `/announce/templates/${t.key}`, {
            name, channelId: t.channelId, content: t.content, allowMentions: t.allowMentions,
            pingEveryone: t.pingEveryone, pingRoleIds: t.pingRoles.map(r => r.id), pingUserIds: t.pingUsers.map(u => u.id)
          }), "Template renamed.") !== undefined) refresh();
        });
        const del = h("button", { class: "btn ghost" }, "Delete");
        del.addEventListener("click", async () => {
          if (!await confirmDialog(`Delete the template «${t.name}»?`, "Posts already sent or scheduled are not affected.", "Delete")) return;
          if (await act(del, () => gApi("DELETE", `/announce/templates/${t.key}`), "Template deleted.") !== undefined) refresh();
        });
        return h("tr", {}, h("td", {}, h("b", {}, t.name)),
          h("td", {}, t.channel ? `# ${t.channel}` : h("span", { class: "muted small" }, "any")),
          h("td", { class: "small" }, t.content.length > 90 ? t.content.slice(0, 90) + "…" : t.content),
          h("td", { class: "actions" }, use, " ", rename, " ", del));
      })))) : empty("No templates yet."));
  const schedCard = h("div", { class: "card" }, h("h2", {}, "📅 Scheduled"), h("div", { class: "muted" }, "Loading…"));
  const sentCard = h("div", { class: "card" }, h("h2", {}, "🗂️ Sent"), h("div", { class: "muted" }, "Loading…"));
  main.append(tplCard, schedCard, sentCard);

  let data;
  try { data = await gApi("GET", "/announce"); } catch (e) { schedCard.replaceChildren(h("h2", {}, "📅 Scheduled"), empty(e.message)); return; }
  const where = p => p.channel ? `${p.voice ? "🔊" : "#"} ${p.channel}` : "deleted channel";
  const short = t => t.length > 90 ? t.slice(0, 90) + "…" : t;
  const loadIntoForm = (p, edit) => {
    channel.value = p.channelId;
    text.value = p.content;
    pings.checked = p.allowMentions;
    setPing(p);
    syncCounter();
    if (edit) {
      editing = p.id;
      title.textContent = "Edit scheduled post";
      cancelEdit.style.display = "";
      go.textContent = "👁 Preview & save";
      when.value = "later";
      when.disabled = true;
      whenBox.style.display = "";
      setWhen(p.sendAt);
    }
    window.scrollTo({ top: 0, behavior: "smooth" });
  };

  schedCard.replaceChildren(h("h2", {}, "📅 Scheduled"), data.scheduled.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
    h("thead", {}, h("tr", {}, h("th", {}, "When"), h("th", {}, "Channel"), h("th", {}, "Text"), h("th", {}, "Messages"), h("th", {}))),
    h("tbody", {}, data.scheduled.map(p => {
      const edit = h("button", { class: "btn secondary", onclick: () => loadIntoForm(p, true) }, "✏️ Edit");
      const now = h("button", { class: "btn" }, "Send now");
      now.addEventListener("click", async () => {
        if (!await confirmDialog("Send this post now?", `${p.messages} message(s) to ${where(p)}.`, "Send now", false)) return;
        if (await act(now, () => gApi("POST", `/announce/${p.id}/send-now`), r => `Sent — ${r.sent} message(s).`) !== undefined) refresh();
      });
      const cancel = h("button", { class: "btn ghost" }, "Cancel");
      cancel.addEventListener("click", async () => {
        if (!await confirmDialog("Cancel this scheduled post?", "It won't be sent. The text is deleted.", "Cancel post")) return;
        if (await act(cancel, () => gApi("DELETE", `/announce/${p.id}`), "Scheduled post cancelled.") !== undefined) refresh();
      });
      return h("tr", {},
        h("td", {}, p.status === "sending" ? "sending…" : fmtDate(p.sendAt)),
        h("td", {}, where(p)),
        h("td", { class: "small" }, short(p.content), h("div", { class: "muted small" }, "by ", member(p.createdBy))),
        h("td", {}, p.messages),
        h("td", { class: "actions" }, edit, " ", now, " ", cancel));
    })))) : empty("Nothing scheduled."));

  sentCard.replaceChildren(h("h2", {}, "🗂️ Sent"), data.sent.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
    h("thead", {}, h("tr", {}, h("th", {}, "Sent"), h("th", {}, "Channel"), h("th", {}, "Text"), h("th", {}, "Status"), h("th", {}))),
    h("tbody", {}, data.sent.map(p => {
      const again = h("button", { class: "btn secondary", title: "Copy into the form", onclick: () => loadIntoForm(p, false) }, "↩️ Reuse");
      const delMsgs = h("button", { class: "btn danger" }, "Delete messages");
      delMsgs.addEventListener("click", async () => {
        if (!await confirmDialog("Delete this post in Discord?", `${p.messages} message(s) in ${where(p)} will be deleted.`, "Delete")) return;
        if (await act(delMsgs, () => gApi("POST", `/announce/${p.id}/delete-messages`), r => `Deleted ${r.deleted} message(s).`) !== undefined) refresh();
      });
      const forget = h("button", { class: "btn ghost" }, "Remove");
      forget.addEventListener("click", async () => {
        if (!await confirmDialog("Remove from the list?", "Only the record here is removed — the messages in Discord stay.", "Remove")) return;
        if (await act(forget, () => gApi("DELETE", `/announce/${p.id}`), "Removed.") !== undefined) refresh();
      });
      return h("tr", {},
        h("td", {}, fmtDate(p.sentAt), p.sendAt ? h("div", { class: "muted small" }, "scheduled") : null),
        h("td", {}, where(p)),
        h("td", { class: "small" }, short(p.content), h("div", { class: "muted small" }, "by ", member(p.createdBy))),
        h("td", {}, p.status === "sent" ? h("span", { class: "badge ok" }, `${p.messages} msg`) : h("span", { class: "badge lock", title: p.error || "" }, "failed"),
          p.error ? h("div", { class: "error-text small" }, p.error) : null),
        h("td", { class: "actions" }, p.jumpUrl ? h("a", { class: "btn ghost", href: p.jumpUrl, target: "_blank", rel: "noopener" }, "Open") : null,
          " ", again, " ", p.messages ? delMsgs : null, " ", forget));
    })))) : empty("Nothing sent yet."));
}

// ================= Overview =================

async function pageOverview(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Dashboard"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
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
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📣 LFG"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));

  // Projects (games): every project has its own tab — activities, panel, classes, templates, schedules, groups, stats.
  // ⚙️ General — projects, settings and everything shared by all projects.
  let lfgProjects = { projects: [], panels: [] };
  try { lfgProjects = await gApi("GET", "/lfg/projects"); } catch { /* shown in the card */ }
  const projects = lfgProjects.projects;
  const defKey = projects.length ? (projects.find(p => p.isDefault) || projects[0]).key : null;
  let tab = store.get("gb.lfgProject") || (defKey || "general");
  if (tab !== "general" && !projects.some(p => p.key === tab)) tab = defKey || "general";
  const P = tab === "general" ? null : tab;              // current project
  const mode = !projects.length ? "all" : P ? "project" : "shared";
  const project = projects.find(p => p.key === P);

  if (projects.length) main.append(tabsRow([...projects.map(p => [p.key, `🎮 ${p.name}`]), ["general", "⚙️ General"]], tab, "gb.lfgProject",
    project ? h("button", { class: "btn ghost", title: "Rename the project, change its roles", onclick: () => projectDialog(project) }, `✏️ Edit ${project.name}`) : null,
    h("button", { class: "btn ghost", onclick: () => projectDialog(null) }, "➕ Project")));

  const [acts, templates] = await Promise.all([
    gApi("GET", "/lfg/activities").catch(() => []),
    gApi("GET", "/lfg/templates").catch(() => [])
  ]);
  const ofProject = key => (key || defKey) === P;       // groups / schedules / panels: no project = default project

  // --- Activities: the project's own + 🌐 shared (all projects)
  const shared = h("span", { class: "badge wait", title: "Offered in every project — manage in ⚙️ General" }, "🌐 all projects");
  const actList = mode === "all" ? acts : mode === "shared" ? acts.filter(a => !a.projectKey) : acts.filter(a => a.projectKey === P || !a.projectKey);
  const actCard = h("div", { class: "card" }, h("div", { class: "card-head" },
    h("h2", {}, mode === "shared" ? "🌐 Shared activities" : mode === "project" ? `Activities · ${project.name}` : "Activities")),
    h("p", { class: "muted small" }, "Members with the role are pinged and get a DM when a group for the activity is posted. ",
      mode === "project" ? "Shown in this project's groups only; 🌐 ones are shared by all projects." :
        mode === "shared" ? "Shared activities are offered in every project's groups." : ""));
  const moveSel = a => {
    const el = h("select", { title: "Move to a project" }, h("option", { value: "" }, "🌐 All projects"),
      projects.map(p => h("option", { value: p.key }, `🎮 ${p.name}`)));
    el.value = a.projectKey || "";
    el.addEventListener("change", async () => {
      if (await act(el, () => gApi("PUT", `/lfg/activities/${encodeURIComponent(a.key)}/project`, { projectKey: el.value || null }), "Activity moved.") !== undefined) refresh();
    });
    return el;
  };
  actCard.append(actList.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
    h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "Ping role"), projects.length ? h("th", {}, "Project") : null, h("th", {}))),
    h("tbody", {}, actList.map(a => {
      const edit = h("button", { class: "btn secondary", onclick: () => activityDialog(a) }, "✏️ Edit");
      const rm = h("button", { class: "btn ghost" }, "Remove");
      rm.addEventListener("click", async () => {
        if (!await confirmDialog(`Remove ${a.name}?`, "Groups that are already posted are not affected.", "Remove")) return;
        if (await act(rm, () => gApi("DELETE", `/lfg/activities/${encodeURIComponent(a.key)}`), "Activity removed.") !== undefined) refresh();
      });
      return h("tr", {}, h("td", {}, a.name, mode === "project" && !a.projectKey ? h("div", {}, shared.cloneNode(true)) : null),
        h("td", {}, roleChip(a.role)), projects.length ? h("td", {}, moveSel(a)) : null, h("td", { class: "actions" }, edit, " ", rm));
    })))) : empty("No activities yet."));
  const nameIn = h("input", { placeholder: "Activity name, e.g. Fire Temple", maxlength: "100" });
  const roleIn = roleSelect("Role to ping…");
  const addBtn = h("button", { class: "btn" }, "➕ Add");
  addBtn.addEventListener("click", async () => {
    const ok = await act(addBtn, () => gApi("POST", "/lfg/activities", { name: nameIn.value, roleId: roleIn.value, projectKey: P }),
      mode === "project" ? `Activity added to ${project.name}.` : "Activity added.");
    if (ok !== undefined) refresh();
  });
  actCard.append(h("div", { class: "row", style: "margin-top:12px" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn));

  // --- LFG panel of the project (posted panels are linked to it right away)
  const panelCard = () => {
    const card = postPanelCard(mode === "project" ? `LFG panel · ${project.name}` : "LFG panel",
      mode === "project" ? "Publishes the message with the “Create LFG” button in a channel (once — it stays there). Groups created with it belong to this project."
        : "Publishes the message with the “Create LFG” button in a channel (once — it stays there).",
      mode === "project" ? `/lfg/panel?project=${encodeURIComponent(P)}` : "/lfg/panel");
    if (mode === "project") {
      const mine = lfgProjects.panels.filter(x => ofProject(x.projectKey));
      card.append(h("div", { class: "muted small", style: "margin-top:10px" }, "Panels of this project:"),
        mine.length ? h("div", {}, mine.map(x => h("div", { class: "row", style: "margin-top:4px" },
          x.channel ? `#${x.channel}` : h("span", { class: "chip warn" }, "deleted channel"),
          h("a", { class: "btn ghost", href: x.jumpUrl, target: "_blank", rel: "noopener" }, "Open"))))
          : h("div", { class: "muted small" }, "none yet"));
    }
    return card;
  };

  // --- Channel for groups created by members on the site (General)
  const channelCard = () => {
    const chCard = h("div", { class: "card" }, h("h3", {}, "Channel for groups"),
      h("p", { class: "muted small" }, "Groups that members create on the site (Member → Groups) go to the channel of their project's LFG panel; if it has none — here."));
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
    return chCard;
  };

  const card = () => h("div", { class: "card" });
  const classCard = card(), tplCard = card(), recCard = card(), statsCard = card();
  const groupsCard = h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active groups")));
  const groupsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  groupsCard.append(groupsBody);

  // --- Category for group voice rooms (project's own, otherwise the common one, otherwise the post channel's category)
  const voiceCard = () => {
    const vc = h("div", { class: "card" }, h("h3", {}, mode === "project" ? `🔊 Voice rooms · ${project.name}` : "🔊 Voice rooms"),
      h("p", { class: "muted small" }, mode === "project"
        ? "Category where 🔊 Voice rooms of this project's groups are created. Empty = the common one (⚙️ General), otherwise the category of the post's channel."
        : "Category for 🔊 Voice rooms of all groups (a project can set its own). Empty = the category of the post's channel."));
    const sel = channelSelect("categories", mode === "project" ? "Not set — use the common category" : "Not set — the post channel's category");
    const save = h("button", { class: "btn" }, "💾 Save");
    const info = h("div", { class: "muted small", style: "margin-top:6px" });
    const q = mode === "project" ? `?project=${encodeURIComponent(P)}` : "";
    save.addEventListener("click", async () => {
      if (await act(save, () => gApi("PUT", `/lfg/voice-category${q}`, { channelId: sel.value || null }), "Voice room category saved.") !== undefined) refresh();
    });
    vc.append(h("div", { class: "row" }, h("div", { class: "grow" }, sel), save), info);
    gApi("GET", `/lfg/voice-category${q}`).then(c => {
      if (c.categoryId) sel.value = c.categoryId;
      info.textContent = c.effective ? `Now: ${c.effective.name}${c.inherited ? " (common)" : ""}` : "Now: the category of the post's channel.";
    }).catch(e => { info.textContent = e.message; });
    return vc;
  };

  // --- Channels where every new group post is duplicated (text channels or the chat of a voice channel)
  const mirrorCard = () => {
    const mc = h("div", { class: "card" }, h("h3", {}, mode === "project" ? `📢 Duplicate posts · ${project.name}` : "📢 Duplicate posts"),
      h("p", { class: "muted small" }, "Every new group is also posted in these channels — a text channel or the chat of a 🔊 voice channel. ",
        "Copies update with the group and are deleted with it. ",
        mode === "project" ? "Empty = the common list (⚙️ General)." : "A project can have its own list (its tab)."));
    const q = mode === "project" ? `?project=${encodeURIComponent(P)}` : "";
    let list = [];
    const chips = h("div", { style: "margin-bottom:8px" });
    const info = h("div", { class: "muted small" });
    const label = c => `${c.voice ? "🔊" : "#"} ${c.name}`;
    const draw = () => chips.replaceChildren(...(list.length ? list.map((c, i) =>
      h("span", { class: `chip${c.exists === false ? " warn" : ""}` }, label(c), h("button", { title: "Remove", onclick: async () => {
        if (await confirmDialog(`Stop duplicating to ${label(c)}?`, "Saved when you press Save. Copies already posted stay.", "Remove")) { list.splice(i, 1); draw(); }
      } }, "×"))) : [h("span", { class: "muted small" }, "No channels.")]));
    const add = h("select", {}, h("option", { value: "" }, "+ Add channel…"),
      h("optgroup", { label: "Text channels" }, state.channels.text.filter(c => c.canPost).map(c => h("option", { value: c.id }, `# ${c.name}`))),
      h("optgroup", { label: "Voice channels (their chat)" }, state.channels.voice.map(c => h("option", { value: `v${c.id}` }, `🔊 ${c.name}`))));
    add.addEventListener("change", () => {
      const voice = add.value.startsWith("v");
      const id = voice ? add.value.slice(1) : add.value;
      const src = (voice ? state.channels.voice : state.channels.text).find(c => c.id === id);
      if (src && !list.some(c => c.id === id)) { list.push({ id, name: src.name, voice, exists: true }); draw(); }
      add.value = "";
    });
    const save = h("button", { class: "btn" }, "💾 Save");
    save.addEventListener("click", async () => {
      if (await act(save, () => gApi("PUT", `/lfg/auto-mirrors${q}`, { channelIds: list.map(c => c.id) }), "Saved — new groups are duplicated there.") !== undefined) refresh();
    });
    mc.append(chips, h("div", { class: "row" }, h("div", { class: "grow" }, add), save), info);
    gApi("GET", `/lfg/auto-mirrors${q}`).then(r => {
      list = r.channels;
      draw();
      info.textContent = r.inherited ? `Now uses the common list: ${r.inherited.map(label).join(", ")}` : "";
    }).catch(e => { info.textContent = e.message; });
    return mc;
  };

  if (mode === "shared") {
    // ⚙️ General: projects, settings, channel, shared activities / templates, common classes
    const projCard = card(), lfgSettingsCard = card();
    main.append(projCard, h("div", { class: "grid-main" }, actCard, h("div", {}, channelCard(), voiceCard(), mirrorCard())), lfgSettingsCard, classCard, tplCard);
    renderProjects(projCard, lfgProjects);
    renderLfgSettings(lfgSettingsCard);
    renderClasses(classCard, projects, "", { noTabs: true });
    renderTemplates(tplCard, acts, templates, null, "shared");
    return;
  }

  main.append(h("div", { class: "grid-main" }, actCard, h("div", {}, panelCard(), mode === "all" ? channelCard() : null, voiceCard(), mirrorCard())));
  if (mode === "all") {
    const projCard = card(), lfgSettingsCard = card();
    main.append(lfgSettingsCard, projCard);
    renderLfgSettings(lfgSettingsCard);
    renderProjects(projCard, lfgProjects);
  }
  main.append(classCard, tplCard, recCard, groupsCard, statsCard);
  renderClasses(classCard, projects, P || "", { noTabs: true });
  renderTemplates(tplCard, acts, templates, P, mode);
  renderRecurring(recCard, acts, templates, projects, P);
  renderStats(statsCard, P);

  try {
    const groups = (await gApi("GET", "/lfg/groups")).filter(g => mode === "all" || ofProject(g.projectKey));
    groupsBody.replaceChildren(groups.length ? h("div", { class: "grid2" }, groups.map(groupCard)) : empty("No active groups."));
  } catch (e) { groupsBody.replaceChildren(empty(e.message)); }
}

/** Rename an activity / change the role it pings. */
function activityDialog(a) {
  const d = dialog();
  const name = h("input", { maxlength: "100", value: a.name, style: "width:100%" });
  const role = roleSelect("Role to ping…");
  role.value = a.role.id;
  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("PUT", `/lfg/activities/${encodeURIComponent(a.key)}`, { name: name.value, roleId: role.value }),
      "Activity saved.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, `Edit activity: ${a.name}`),
    h("div", { class: "field" }, h("label", {}, "Name"), name),
    h("div", { class: "field" }, h("label", {}, "Role to ping"), role),
    h("p", { class: "muted small" }, "Groups that are already posted keep the old name."),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
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

  // Project (game): characters of this project are used; members need its role.
  // lockProject (admin, inside a project tab): the project is fixed, no select
  const locked = "lockProject" in opts;
  const projectSel = !locked && opts.projects && opts.projects.length
    ? h("select", {}, opts.projects.map(p => h("option", { value: p.key }, p.name)))
    : null;
  if (projectSel) projectSel.value = v.projectKey || (opts.projects.find(p => p.isDefault) || opts.projects[0]).key;
  const currentProject = () => locked ? opts.lockProject : projectSel ? projectSel.value : null;
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
  fillActivities(currentProject());
  const custom = h("input", { placeholder: "Activity name", maxlength: "100" });
  if (v.activityKey !== undefined) {
    activity.value = v.activityKey || "";
    custom.value = v.customActivity || "";
  } else if (v.activity) {
    const match = acts.find(a => a.name === v.activity);
    if (match) activity.value = match.key; else custom.value = v.activity;
  } else {
    const first = [...activity.options].find(o => o.value);
    if (first) activity.value = first.value;
  }
  const syncCustom = () => { custom.style.display = activity.value ? "none" : ""; };
  activity.addEventListener("change", syncCustom);
  syncCustom();

  if (projectSel) {
    projectSel.addEventListener("change", () => { fillActivities(projectSel.value); fillTemplates(); syncCustom(); });
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
  // Only templates of the current project (+ the ones for all projects)
  const tplSel = opts.templates && opts.templates.length ? h("select", {}) : null;
  const fillTemplates = () => {
    if (!tplSel) return;
    const key = currentProject();
    tplSel.replaceChildren(h("option", { value: "" }, "— no template —"),
      ...opts.templates.filter(t => !key || !t.projectKey || t.projectKey === key).map(t => h("option", { value: t.key }, t.name)));
  };
  fillTemplates();
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
      projectKey: currentProject(), createVoice: voice.checked,
      mirrorChannelIds: mirrorBoxes.filter(x => x.cb.checked).map(x => x.id), ...extra()
    })
  };
}

async function renderRecurring(card, acts, templates = [], projects = [], projectKey = null) {
  const add = h("button", { class: "btn" }, "➕ Add schedule");
  add.addEventListener("click", () => recurringDialog(acts, null, templates, projects, projectKey));
  const defKey = projects.length ? (projects.find(p => p.isDefault) || projects[0]).key : null;
  const body = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "🔁 Recurring groups"), add),
    h("p", { class: "muted small" }, "Posted automatically on the chosen days, the set number of hours before the start."), body);

  let list;
  try { list = await gApi("GET", "/lfg/recurring"); } catch (e) { body.replaceChildren(empty(e.message)); return; }
  // Only this project's schedules (no project = default project)
  if (projectKey) list = list.filter(r => (r.projectKey || defKey) === projectKey);
  if (!list.length) { body.replaceChildren(empty("No schedules yet.")); return; }

  const dayNames = d => DAYS.filter(([n]) => d.includes(n)).map(([, l]) => l).join(", ");
  body.replaceChildren(h("div", { class: "table-wrap" }, h("table", { class: "table" },
    h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "When"), h("th", {}, "Next post"), h("th", {}, "Status"), h("th", {}))),
    h("tbody", {}, list.map(r => {
      const edit = h("button", { class: "btn secondary" }, "Edit");
      edit.addEventListener("click", () => recurringDialog(acts, r, templates, projects, projectKey));
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

function recurringDialog(acts, r, templates = [], projects = [], projectKey = null) {
  const d = dialog();
  const form = lfgForm(acts, projectKey
    ? { mode: "recurring", initial: r || {}, templates, lockProject: projectKey }
    : { mode: "recurring", initial: r || {}, templates, projects });
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
      return h("tr", {}, h("td", {}, h("b", {}, p.name), p.isDefault ? h("span", { class: "badge ok", style: "margin-left:6px", title: "Holds the characters, groups and panels made before projects" }, "default") : null),
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
  const save = h("button", { class: "btn" }, p ? "💾 Save" : "➕ Add");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("PUT", `/lfg/projects/${p ? p.key : "new"}`, { name: name.value, roleIds: roles.value() }),
      "Project saved.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, p ? `Edit project: ${p.name}` : "New project"),
    h("div", { class: "field" }, h("label", {}, "Name"), name),
    h("div", { class: "field" }, h("label", {}, "Required role (any of) — empty = everyone"), roles.el),
    p && p.isDefault ? h("p", { class: "muted small" }, "⭐ Default project: it holds the characters, groups and panels made before projects. It stays the default (it can be renamed).") : null,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
}

async function renderClasses(card, projectList = [], projectKey = store.get("gb.classProject") || "", opts = {}) {
  if (projectKey && !projectList.some(p => p.key === projectKey)) projectKey = "";
  card.replaceChildren(h("h2", {}, "🎭 Classes"), h("div", { class: "muted" }, "Loading…"));
  let data;
  const q = projectKey ? `?project=${encodeURIComponent(projectKey)}` : "";
  try { data = await gApi("GET", `/lfg/classes${q}`); } catch (e) { card.replaceChildren(h("h2", {}, "🎭 Classes"), empty(e.message)); return; }
  // Common classes + each project's own (a project without its own uses the common list)
  const tabs = projectList.length && !opts.noTabs ? h("div", { class: "row", style: "margin-bottom:10px" },
    [["", "🌐 Common"], ...projectList.map(p => [p.key, `🎮 ${p.name}`])].map(([k, label]) =>
      h("button", { class: `btn secondary${k === projectKey ? " active-tab" : ""}`,
        onclick: () => { store.set("gb.classProject", k); renderClasses(card, projectList, k, opts); } }, label))) : null;
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
        h("button", { class: "btn ghost", title: "Remove", onclick: async () => {
          if (await confirmDialog(`Remove @${r.role.name} from classes?`, "Saved when you press Save classes.", "Remove")) { rows.splice(i, 1); draw(); }
        } }, "✖"));
    }));
  };
  // Several class roles at once
  const addSel = h("button", { class: "btn secondary" }, "➕ Add class roles…");
  addSel.addEventListener("click", async () => {
    const picked = await pickRolesDialog("Add class roles", rows.map(r => r.roleId));
    if (!picked) return;
    for (const role of picked) {
      const ri = roleIcons[role.id];
      rows.push({ roleId: role.id, role: { ...role, exists: true }, icon: ri && ri.emoji ? ri.emoji : "" });
    }
    draw();
    toast(`Added ${picked.length} role(s) — press Save classes.`);
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
  const projName = (projectList.find(p => p.key === projectKey) || {}).name;
  const title = opts.noTabs ? (projectKey ? `🎭 Classes · ${projName}` : "🎭 Common classes") : "🎭 Classes";
  // A project without its own list uses the common one — copy it here to start a separate list
  let copy = null;
  if (projectKey && !rows.length) {
    copy = h("button", { class: "btn secondary" }, "📋 Copy the common list");
    copy.addEventListener("click", async () => {
      const common = await act(copy, () => gApi("GET", "/lfg/classes"));
      if (!common) return;
      rows = common.classes.map(c => ({ roleId: c.role.id, role: c.role, icon: c.icon }));
      draw();
      toast(rows.length ? "Copied — edit and press Save classes." : "The common list is empty.", rows.length ? "ok" : "warn");
    });
  }
  card.replaceChildren(...[h("h2", {}, title), tabs,
    projectKey && !rows.length ? h("div", { class: "chip", style: "margin-bottom:8px" }, "Now uses the common list (⚙️ General → Common classes)") : null,
    h("p", { class: "muted small" }, "Roles that count as classes and their icon shown next to the player's name. If the list is not empty, players pick any of these as their class (they don't need the role); if empty — only roles they have. ",
      projectKey ? "This project's own classes — if empty, the common list is used." : "Common classes — used by every project that has no classes of its own. ",
      "Use a unicode emoji, a server emoji, or 🎨 Use role icons — the roles' own icons from Discord (image icons are uploaded as the bot's emojis, no server emoji slots used)."),
    list, h("div", { class: "row", style: "margin-top:8px" }, addSel, copy, fromRoles, save)].filter(Boolean));
}

// ----- LFG templates (admin) -----

function renderTemplates(card, acts, templates, projectKey = null, mode = "all") {
  // Project tab: its own templates + 🌐 shared ones; General: shared only
  if (mode === "project") templates = templates.filter(t => !t.projectKey || t.projectKey === projectKey);
  else if (mode === "shared") templates = templates.filter(t => !t.projectKey);
  const add = h("button", { class: "btn" }, "➕ Add template");
  add.addEventListener("click", () => templateDialog(acts, null, mode === "project" ? projectKey : null));
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "📋 Templates"), add),
    h("p", { class: "muted small" }, "Activity + slots + requirements in one click — offered in /lfg (Step 1) and in Create a group on the site. ",
      mode === "project" ? "Templates of this project; 🌐 ones are offered in every project." : mode === "shared" ? "Shared templates are offered in every project." : ""),
    templates.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("tbody", {}, templates.map(t => {
        const act = acts.find(a => a.key === t.activityKey);
        const edit = h("button", { class: "btn secondary", onclick: () => templateDialog(acts, t, t.projectKey || null) }, "Edit");
        const rm = h("button", { class: "btn ghost" }, "Delete");
        rm.addEventListener("click", async () => {
          if (!await confirmDialog(`Delete the template ${t.name}?`, "Posted groups are not affected.", "Delete")) return;
          if (await act(rm, () => gApi("DELETE", `/lfg/templates/${t.key}`), "Template deleted.") !== undefined) refresh();
        });
        return h("tr", {},
          h("td", {}, h("b", {}, t.name), mode === "project" && !t.projectKey ? h("span", { class: "badge wait", style: "margin-left:6px" }, "🌐 all projects") : null,
            h("div", { class: "muted small" }, act ? act.name : (t.customActivity || "custom"))),
          h("td", { class: "small" }, t.slots.map(x => (SLOT_ROLES.find(r => r[0] === x) || [x, x])[1]).join(" · ")),
          h("td", { class: "small" }, t.requiredRoles.map(r => roleChip(r)), t.minGs ? h("span", { class: "chip" }, `GS ${t.minGs}+`) : null),
          h("td", { class: "actions" }, edit, " ", rm));
      })))) : empty("No templates yet."));
}

function templateDialog(acts, t, projectKey = null) {
  const d = dialog();
  const form = lfgForm(acts, { mode: "template", initial: t || {}, lockProject: projectKey });
  const save = h("button", { class: "btn" }, t ? "💾 Save" : "➕ Add");
  save.addEventListener("click", async () => {
    const v = form.value();
    const res = await act(save, () => gApi("PUT", `/lfg/templates/${t ? t.key : "new"}`, {
      name: v.name, activityKey: v.activityKey, customActivity: v.customActivity, slots: v.slots,
      requiredRoleIds: v.requiredRoleIds, minGs: v.minGs, projectKey
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

async function renderStats(card, projectKey = null) {
  const period = h("select", {}, [[7, "Last 7 days"], [30, "Last 30 days"], [90, "Last 90 days"], [0, "All time"]]
    .map(([v, l]) => h("option", { value: v, selected: v === 30 }, l)));
  const body = h("div", {});
  const load = async () => {
    body.replaceChildren(h("div", { class: "muted" }, "Loading…"));
    let st;
    try { st = await gApi("GET", `/lfg/stats?days=${period.value}${projectKey ? `&project=${encodeURIComponent(projectKey)}` : ""}`); } catch (e) { body.replaceChildren(empty(e.message)); return; }
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
  const ic = s.attended === true ? "circle-check" : s.attended === false ? "circle-x" : "circle-dot";
  const cls = s.attended === true ? " att-came" : s.attended === false ? " att-miss" : "";
  const title = s.attended === true ? "Came — click: no-show" : s.attended === false ? "No-show — click: clear" : "Not marked — click: came";
  return h("button", { class: `btn ghost icon-only sm${cls}`, title, "aria-label": title,
    onclick: () => act(null, () => gApi("POST", url, { userId: s.member.id, attended: next })).then(r => { if (r !== undefined) refresh(); }) }, icon(ic, 16));
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
          tool("✖️", "Remove from the reserve", async () => {
            if (await confirmDialog(`Remove ${r.member.name} from the reserve?`, "They are taken out of this group.", "Remove")) roster("removeReserve", r.member, "Removed from the reserve.");
          })))))) : null,
    h("div", { class: "row", style: "margin-top:12px" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, "Open in Discord"),
      g.voiceUrl ? h("a", { class: "btn btn-refresh", href: g.voiceUrl, target: "_blank", rel: "noopener", title: "Open the group's voice room in Discord" }, "🔊 Voice room") : null,
      h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/lfg/groups/${encodeURIComponent(g.id)}/time`) }, "🕒 Time"),
      close));
}

// ================= Tickets =================

async function pageTickets(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎫 Tickets"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));

  // A tab per ticket project; ⚙️ General — ticket panel, all open tickets, archive
  let projects = [];
  try { projects = await gApi("GET", "/tickets/projects"); } catch (e) { main.append(empty(e.message)); return; }
  let tab = store.get("gb.ticketTab") || (projects[0] ? projects[0].key : "new");
  if (!["new", "general"].includes(tab) && !projects.some(p => p.key === tab)) tab = projects[0] ? projects[0].key : "new";
  main.append(tabsRow([...projects.map(p => [p.key, `📁 ${p.name}${p.open ? ` (${p.open})` : ""}`]), ["new", "➕ New project"], ["general", "⚙️ General"]],
    tab, "gb.ticketTab"));

  if (tab === "new") {
    const nameIn = h("input", { placeholder: "Project name, e.g. Aion 2", maxlength: "100" });
    const roleIn = roleSelect("Role given on Approve…");
    const addBtn = h("button", { class: "btn" }, "➕ Add project");
    addBtn.addEventListener("click", async () => {
      if (await act(addBtn, () => gApi("POST", "/tickets/projects", { name: nameIn.value, memberRoleId: roleIn.value }),
        "Project added. Now set its leadership roles.") === undefined) return;
      const list = await gApi("GET", "/tickets/projects").catch(() => []);
      const added = list.find(p => p.name.toLowerCase() === nameIn.value.trim().toLowerCase());
      store.set("gb.ticketTab", added ? added.key : "general");
      refresh();
    });
    main.append(h("div", { class: "card" }, h("h3", {}, "New project"),
      h("p", { class: "muted small" }, "Applicants pick the project in the ticket panel."),
      h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn)));
    return;
  }

  const project = projects.find(p => p.key === tab);
  const openBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  if (project) main.append(projectCard(project));
  else main.append(postPanelCard("Ticket panel", "Posts the message with the button to open a ticket (all projects in one panel).", "/tickets/panel"));
  main.append(h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, project ? `Open tickets · ${project.name}` : "Open tickets")), openBody));
  if (!project) main.append(ticketArchiveCard());

  try {
    let open = await gApi("GET", "/tickets/open");
    if (project) open = open.filter(t => t.project === project.name);
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
  const nameIn = h("input", { maxlength: "100", value: p.name, style: "font-weight:600;min-width:200px" });
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
      formEnabled: form.enabled(), questions: form.value(), name: nameIn.value,
      autoCloseDays: autoClose.value === "" ? null : Number(autoClose.value)
    }));
    if (res === undefined) return;
    toast(`Project ${nameIn.value.trim() || p.name} saved.`);
    if (nameIn.value.trim() && nameIn.value.trim() !== p.name) refresh();
    if (res.warning) toast(res.warning, "warn");
    if (res.ticketsTotal) toast(`Updated ${res.ticketsUpdated} of ${res.ticketsTotal} open tickets.`, res.ticketsUpdated === res.ticketsTotal ? "ok" : "warn");
  });
  const remove = h("button", { class: "btn danger" }, "🗑️ Remove");
  remove.addEventListener("click", async () => {
    if (!await confirmDialog(`Remove ${p.name}?`, "New tickets for this project can't be created. Open tickets stay until they are closed.", "Remove project")) return;
    if (await act(remove, () => gApi("DELETE", `/tickets/projects/${encodeURIComponent(p.key)}`), "Project removed.") !== undefined) refresh();
  });

  return h("div", { class: "card" },
    h("div", { class: "card-head" }, h("div", { class: "field", style: "margin:0;margin-right:auto" }, h("label", {}, "Project name"), nameIn),
      h("span", { class: "badge wait" }, `${p.open} open`)),
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
      const del = h("button", { class: "btn ghost", type: "button", title: "Remove", onclick: async () => {
        if (await confirmDialog("Remove this question?", "Saved when you press Save.", "Remove")) { qs.splice(i, 1); draw(); }
      } }, "✖");
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
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔊 Temp Voice"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));

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
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted" },
    "Edit the text of the panels the bot posts and the button labels of group posts and tickets. Saving updates what is already posted. ",
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

  const labelsOnly = p.kind === "labels";
  const what = p.key === "lfgpost" ? "active group post(s)" : p.key === "ticketmsg" ? "open ticket(s)" : "posted panel(s)";
  const resultToast = (res, done) => {
    if (!res) return;
    toast(res.panelsTotal
      ? `${done} Updated ${res.panelsUpdated} of ${res.panelsTotal} ${what}.`
      : labelsOnly ? `${done} New posts will use it.` : `${done} No posted panels to update yet — post the panel from its module tab.`,
      res.panelsUpdated === res.panelsTotal ? "ok" : "warn");
  };

  const save = h("button", { class: "btn" }, labelsOnly ? "💾 Save & update posts" : "💾 Save & update panels");
  save.addEventListener("click", async () => {
    const labels = {};
    for (const { l, input } of labelInputs) labels[l.id] = input.value;
    const res = await act(save, () => gApi("PUT", `/texts/${p.key}`, { title: title.value, description: desc.value, labels }));
    resultToast(res, "Saved.");
  });
  const reset = h("button", { class: "btn secondary" }, "↩️ Reset to default");
  reset.addEventListener("click", async () => {
    if (!await confirmDialog(`Reset ${p.name}?`, `The default text is restored and ${what} are updated.`, "Reset", false)) return;
    const res = await act(reset, () => gApi("DELETE", `/texts/${p.key}`));
    if (res === undefined) return;
    resultToast(res, "Reset.");
    refresh();
  });

  const custom = p.title || p.description || p.labels.some(l => l.value);
  return h("div", { class: "card" },
    h("div", { class: "card-head" }, h("h2", {}, p.name),
      custom ? h("span", { class: "badge wait" }, "custom") : h("span", { class: "badge ok" }, "default"),
      labelsOnly ? null : h("span", { class: "muted small" }, `${p.posted} posted`)),
    labelsOnly ? h("p", { class: "muted small" }, p.key === "lfgpost"
      ? "Buttons under every LFG group post. Saving updates the active groups." : "Buttons in every ticket message. Saving updates the open tickets.") : null,
    labelsOnly ? null : h("div", { class: "field" }, h("label", {}, "Title"), title),
    labelsOnly ? null : h("div", { class: "field" }, h("label", {}, "Text (Discord markdown)"), desc,
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
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));

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
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
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
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Home"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
  const go = (tab, extra) => () => { if (extra) extra(); state.tab = tab; store.set("gb.tab", tab); renderShell(); };
  const guild = currentGuild();

  // A. Welcome + server context
  const hour = new Date().getHours();
  const hello = hour < 5 ? "Good night" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
  main.append(h("div", { class: "home-hero" },
    h("div", {}, h("div", { class: "hero-title" }, `${hello}, ${state.me.name}!`),
      h("div", { class: "muted" }, "Here's what's happening on ", h("b", { class: "text-strong" }, guild ? guild.name : "your server"), " right now.")),
    h("span", { class: `status-pill ${state.connection === "Connected" ? "ok" : "bad"}` }, h("span", { class: "dot" }),
      h("span", {}, state.connection === "Connected" ? "Bot online" : `Bot ${state.connection || "offline"}`))));

  // B. Server overview (real counts only)
  const server = h("div", {}, sectionLabel("Server"), h("div", { class: "stats" }, [0, 1, 2, 3].map(() => skeletonStat())));
  main.append(server);
  gApi("GET", "/me/summary").then(sum => {
    server.replaceChildren(sectionLabel("Server"), h("div", { class: "stats" },
      statCard("users", "Members", sum.server.members, sum.server.name),
      statCard("swords", "Active groups", sum.activeGroups, sum.recruiting ? `${sum.recruiting} recruiting` : "none recruiting", go("play")),
      statCard("ticket", "Open tickets", sum.openTickets, "applications in progress", can("tickets") ? go("tickets") : null),
      statCard("headphones", "Voice rooms", sum.voiceRooms, "temporary rooms now", can("voice") ? go("voice") : null)));
  }).catch(e => server.replaceChildren(sectionLabel("Server"), empty(e.message)));

  // D. Quick actions — only what this user can do
  const quick = [
    ["circle-plus", "Create a group", "LFG · post a group", go("play", () => store.set("gb.openCreate", "1"))],
    ["file-pen-line", "Apply", "Open an application", () => applyDialog()],
    ["user-round", "My characters", "Classes, GS, notes", go("chars")],
    can("tickets") ? ["ticket", "Tickets", "Manage applications", go("tickets")] : null,
    can("voice") ? ["headphones", "Temp Voice", "Rooms & channels", go("voice")] : null,
    can("announce") ? ["megaphone", "Announcement", "Post as the bot", go("announce")] : null
  ].filter(Boolean);
  main.append(sectionLabel("Quick actions"), h("div", { class: "quick-actions" }, quick.map(([ic, title, sub, fn]) =>
    h("button", { class: `quick-action ${toneOf(ic)}`, onclick: fn }, h("span", { class: "qa-icon" }, icon(ic, 18)),
      h("span", { class: "qa-text" }, h("b", {}, title), h("span", { class: "muted small" }, sub))))));

  // C. Personal activity
  main.append(sectionLabel("My activity"));
  const grid = h("div", { class: "grid2" });
  const left = h("div", {}), right = h("div", {});
  grid.append(left, right);
  main.append(grid);
  const groupsCard = h("div", { class: "card" }, cardHead("users-round", "My groups", h("button", { class: "btn secondary", onclick: go("play") }, "All groups", icon("arrow-right", 15))), skeletonLines(3));
  const statsCard = h("div", { class: "card" }, cardHead("chart-column", "My stats"), skeletonLines(2));
  const ticketsCard = h("div", { class: "card" });
  const charsCard = h("div", { class: "card" }, cardHead("user-round", "My characters", h("button", { class: "btn secondary", onclick: go("chars") }, "Manage", icon("arrow-right", 15))), skeletonLines(2));
  const roomCard = h("div", { class: "card" });
  const prefsCard = h("div", { class: "card" }, cardHead("sliders-horizontal", "My room defaults"),
    h("p", { class: "muted small" }, "Applied every time your temporary voice room is created (same as /room defaults in Discord)."));
  left.append(groupsCard, statsCard, charsCard);
  right.append(ticketsCard, roomCard, prefsCard);

  let d;
  try { d = await gApi("GET", "/me/home"); } catch (e) { groupsCard.replaceChildren(empty(e.message)); return; }
  const STATUS = { organizer: ["Organizer", "ok", "crown"], group: ["In the group", "ok", "check"], reserve: ["Reserve", "wait", "clock"] };

  groupsCard.replaceChildren(groupsCard.firstChild, d.groups.length ? h("div", { class: "list" }, d.groups.map(g => h("div", { class: "list-row" },
    h("div", { class: "grow" }, h("b", {}, g.activity),
      h("div", { class: "muted small" }, `${g.startsAt ? fmtDate(g.startsAt) : "no time set"}${g.character ? ` · ${g.character}` : ""}`)),
    capacity(g.filled, g.size),
    h("span", { class: `badge ${STATUS[g.status][1]}` }, icon(STATUS[g.status][2], 12), STATUS[g.status][0]),
    h("a", { class: "btn ghost icon-only", href: g.jumpUrl, target: "_blank", rel: "noopener", title: "Open in Discord" }, icon("external-link", 16)))))
    : emptyState("users-round", "You're not in any group", "Find a group to join or create your own.",
      h("button", { class: "btn", onclick: go("play") }, icon("search", 16), "Browse groups")));

  const apply = h("button", { class: "btn secondary" }, icon("file-pen-line", 16), "Apply");
  apply.addEventListener("click", () => applyDialog());
  ticketsCard.append(cardHead("ticket", "My applications", apply), d.tickets.length ? h("div", { class: "list" }, d.tickets.map(t => h("div", { class: "list-row" },
    h("div", { class: "grow" }, h("b", {}, t.project), h("div", { class: "muted small" }, `opened ${fmtRelative(t.createdAt)}`)),
    t.approved ? h("span", { class: "badge ok" }, icon("check", 12), "Approved") : h("span", { class: "badge wait" }, icon("clock", 12), "Waiting"),
    h("a", { class: "btn ghost icon-only", href: t.jumpUrl, target: "_blank", rel: "noopener", title: "Open in Discord" }, icon("external-link", 16)))))
    : emptyState("inbox", "No open applications", "Want to join a project? Send an application — a private ticket opens for you."));

  // My voice room
  roomCard.append(cardHead("headphones", "My voice room"));
  if (!d.room) roomCard.append(emptyState("volume-2", "You don't own a room now", "Join a «join to create» voice channel in Discord — your room appears here."));
  else {
    const r = d.room;
    const name = h("input", { value: r.name, maxlength: "100", "aria-label": "Room name" });
    const limit = h("input", { type: "number", min: "0", max: "99", value: r.limit ?? 0, style: "width:90px", "aria-label": "User limit" });
    const call = (btn, body) => act(btn, () => gApi("POST", "/me/room", body), res => res.message).then(x => { if (x !== undefined) refresh(); });
    const rn = h("button", { class: "btn secondary" }, "Rename");
    rn.addEventListener("click", () => call(rn, { action: "rename", name: name.value }));
    const lm = h("button", { class: "btn secondary" }, "Set limit");
    lm.addEventListener("click", () => call(lm, { action: "limit", limit: Number(limit.value || 0) }));
    const lk = h("button", { class: "btn" }, icon(r.locked ? "lock-open" : "lock", 16), r.locked ? "Unlock" : "Lock");
    lk.addEventListener("click", () => call(lk, { action: r.locked ? "unlock" : "lock" }));
    roomCard.append(
      h("div", { class: "row", style: "margin-bottom:12px" }, h("span", { class: "badge ok" }, icon("users", 12), `${r.inside} inside`),
        r.locked ? h("span", { class: "badge lock" }, icon("lock", 12), "Locked") : h("span", { class: "badge" }, icon("lock-open", 12), "Open")),
      h("div", { class: "field" }, h("label", {}, "Name"), h("div", { class: "row" }, h("div", { class: "grow" }, name), rn)),
      h("div", { class: "field" }, h("label", {}, "User limit (0 = no limit)"), h("div", { class: "row" }, limit, lm, lk)),
      h("p", { class: "muted small" }, "Permit or kick members in Discord (Room controls or /room)."));
  }

  // E. Room defaults
  gApi("GET", "/me/room-prefs").then(pr => {
    const pn = h("input", { placeholder: "e.g. {user}'s room — empty = the channel's default", maxlength: "100", value: pr.name || "" });
    const pl = h("input", { type: "number", min: "0", max: "99", placeholder: "no limit", value: pr.limit ?? "", style: "width:110px" });
    const pk = h("input", { type: "checkbox", checked: pr.locked });
    const ps = h("button", { class: "btn" }, icon("save", 16), "Save defaults");
    ps.addEventListener("click", () => {
      if (pl.value !== "" && (Number(pl.value) < 0 || Number(pl.value) > 99)) return toast("The limit is 0–99.", "warn");
      act(ps, () => gApi("PUT", "/me/room-prefs", { name: pn.value || null, limit: pl.value === "" ? null : Number(pl.value), locked: pk.checked }),
        "Room defaults saved — used for your next room.");
    });
    prefsCard.append(
      h("div", { class: "field" }, h("label", {}, "Room name"), pn, h("span", { class: "muted small" }, "{user} is replaced with your name.")),
      h("div", { class: "row", style: "margin-bottom:12px" },
        h("div", { class: "field", style: "margin:0" }, h("label", {}, "User limit"), pl),
        h("label", { class: "row", style: "margin-top:20px" }, pk, "Lock the room right away")),
      ps);
  }).catch(e => prefsCard.append(empty(e.message)));

  // My stats
  gApi("GET", "/me/stats").then(st => {
    statsCard.replaceChildren(statsCard.firstChild,
      h("div", { class: "stats compact" },
        statCard("users-round", "Groups", st.groups, "played"),
        statCard("crown", "Organized", st.organized, "as organizer"),
        statCard("circle-check", "Reliability", st.reliability === null ? "—" : `${st.reliability}%`, `came ${st.attended} · no-show ${st.noShows}`)),
      st.recent.length ? h("div", { class: "list" }, st.recent.slice(0, 5).map(r => h("div", { class: "list-row" },
        h("div", { class: "grow" }, h("b", {}, r.activity), h("div", { class: "muted small" }, fmtDate(r.at))),
        r.organizer ? h("span", { class: "badge" }, icon("crown", 12), "Organizer")
          : r.attended === true ? h("span", { class: "badge ok" }, icon("check", 12), "Came")
          : r.attended === false ? h("span", { class: "badge lock" }, icon("x", 12), "No-show") : null)))
        : h("p", { class: "muted small" }, "Your finished groups will appear here."));
  }).catch(e => statsCard.replaceChildren(statsCard.firstChild, empty(e.message)));

  // My characters (default project)
  gApi("GET", "/me/characters").then(c => {
    const proj = (c.projects || []).find(p => p.key === c.project);
    charsCard.replaceChildren(charsCard.firstChild,
      proj ? h("p", { class: "muted small" }, `Project: ${proj.name}`) : null,
      c.characters.length ? h("div", { class: "char-grid" }, c.characters.slice(0, 6).map(ch => h("div", { class: "char-card" },
        h("span", { class: "char-icon" }, ch.classRole ? (classMark({ classIcon: (c.classRoles.find(r => r.id === ch.classRole.id) || {}).icon, classRole: ch.classRole }) || icon("user-round", 16)) : icon("user-round", 16)),
        h("div", { class: "grow" }, h("b", {}, ch.name), h("div", { class: "muted small" },
          [ch.classRole && ch.classRole.name, ch.gs ? `GS ${ch.gs}` : null].filter(Boolean).join(" · ") || "no class yet")))))
        : emptyState("user-plus", "No characters yet", "Add your characters once — then sign up for groups in one click.",
          h("button", { class: "btn", onclick: go("chars") }, icon("plus", 16), "Add a character")));
  }).catch(e => charsCard.replaceChildren(charsCard.firstChild, empty(e.message)));
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
  const tabsRow = h("div", { class: "tabs" },
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
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎮 Groups"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));

  // Project tabs: groups of one game at a time
  let projects = [];
  try { projects = (await gApi("GET", "/me/characters")).projects || []; } catch { /* no projects */ }
  let tab = store.get("gb.playProject") || "all";
  if (tab !== "all" && !projects.some(p => p.key === tab)) tab = "all";
  if (projects.length > 1) main.append(tabsRow([["all", "🌐 All"], ...projects.map(p => [p.key, `🎮 ${p.name}`])], tab, "gb.playProject"));
  main.append(holder);

  // Create a group (like /lfg) — collapsed by default
  const createBody = h("div", { style: "display:none" });
  const toggle = h("button", { class: "btn" }, "➕ Create a group");
  const createCard = h("div", { class: "card" }, cardHead("circle-plus", "Create a group", toggle),
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
        templates: opt.templates, roles: opt.roles, projects: opt.projects, mirrorChannels: opt.mirrorChannels,
        initial: tab !== "all" ? { projectKey: tab } : {} });
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
  // Home → «Create a group» opens the form right away
  if (store.get("gb.openCreate") === "1") { store.set("gb.openCreate", ""); toggle.click(); }

  let data;
  try { data = await gApi("GET", "/me/groups"); } catch (e) { holder.replaceChildren(empty(e.message)); return; }
  if (tab !== "all") data.groups = data.groups.filter(g => g.projectKey === tab);
  holder.replaceChildren(sectionLabel(`Active groups · ${data.groups.length}`), data.groups.length
    ? h("div", { class: "grid2" }, data.groups.map(g => playCard(g)))
    : h("div", { class: "card" }, emptyState("users-round", "No active groups right now", "Be the first — create a group and others will join.",
      h("button", { class: "btn", onclick: () => { if (createBody.style.display === "none") toggle.click(); window.scrollTo({ top: 0, behavior: "smooth" }); } }, icon("plus", 15), "Create a group"))));
}

/** Role pill for a slot: DD / Tank / Sup / All — icon + text, color only as an extra hint. */
const ROLE_STYLE = { DD: ["swords", "role-dd"], Tank: ["shield", "role-tank"], Sup: ["circle-plus", "role-sup"], All: ["users", "role-all"] };
function rolePill(label) {
  const [ic, cls] = ROLE_STYLE[label] || ROLE_STYLE.All;
  return h("span", { class: `role-pill ${cls}` }, icon(ic, 12), label);
}

function playCard(g) {
  const characters = g.characters || [];
  const gid = encodeURIComponent(g.id);
  // Organizer tools (like ⚙️ Manage in Discord): group ⇄ reserve
  const roster$ = (action, m, okText) => act(null,
    () => gApi("POST", `/me/groups/${gid}/roster`, { action, userId: m.id }), okText)
    .then(r => { if (r !== undefined) refresh(); });
  const tool = (ic, title, fn) => h("button", { class: "btn ghost icon-only sm", title, "aria-label": title, onclick: fn }, icon(ic, 15));
  const org = g.isOrganizer;
  const meId = state.me && state.me.id;

  // Roster: taken slots and open slots look different (not only by color)
  const roster = h("div", { class: "roster" }, g.slots.map((s, i) => s.member
    ? h("div", { class: `slot taken${s.member.id === meId ? " me" : ""}` },
        h("span", { class: "slot-num" }, i + 1), rolePill(s.role || s.slotRole),
        h("span", { class: "slot-who" }, member(s.member), classMark(s)),
        h("span", { class: "slot-char" }, s.character || "", s.gs ? h("span", { class: "gs" }, `GS ${s.gs}`) : null),
        org && g.started ? attendanceButton(s, `/me/groups/${gid}/attendance`) : null,
        org && s.member.id !== g.owner.id ? tool("arrow-right", "Move to reserve", () => roster$("toReserve", s.member, "Moved to the reserve.")) : null)
    : h("div", { class: "slot open" },
        h("span", { class: "slot-num" }, i + 1), rolePill(s.slotRole),
        h("span", { class: "slot-open-text" }, icon("circle-plus", 14), "Open slot"))));
  const reserve = g.reserve.length ? h("details", { class: "reserve", open: org },
    h("summary", {}, icon("clock", 14), `Reserve · ${g.reserve.length}`),
    h("div", { class: "roster" }, g.reserve.map(r => h("div", { class: "slot taken reserve-row" },
      rolePill(r.role || "All"), h("span", { class: "slot-who" }, member(r.member), classMark(r)),
      h("span", { class: "slot-char" }, r.character || "", r.gs ? h("span", { class: "gs" }, `GS ${r.gs}`) : null),
      org ? tool("arrow-left", "Move into the group (sends a DM)", () => roster$("promote", r.member, "Moved into the group, DM sent.")) : null,
      org ? tool("x", "Remove from the reserve", async () => {
        if (await confirmDialog(`Remove ${r.member.name} from the reserve?`, "They are taken out of this group.", "Remove")) roster$("removeReserve", r.member, "Removed from the reserve.");
      }) : null)))) : null;

  const closeBtn = org ? h("button", { class: "btn danger" }, icon("trash-2", 15), "Close group") : null;
  if (closeBtn) closeBtn.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The post is deleted in Discord. Copy the invites first if you need them.", "Close & delete")) return;
    if (await act(closeBtn, () => gApi("POST", `/me/groups/${gid}/close`), "Group closed.") !== undefined) refresh();
  });

  // Sign-up form: role + character (saved or new)
  const free = v => g.canJoin[v] || (g.me && g.me.status === "group" && g.me.role === v);
  const role = h("select", { "aria-label": "Role" }, ROLE_CHOICES.map(([v, l]) => h("option", { value: v }, free(v) ? l : `${l} (reserve)`)));
  const charSel = h("select", { "aria-label": "Character" }, characters.map(c => h("option", { value: c }, c)), h("option", { value: "" }, "+ New character…"));
  const newChar = h("input", { placeholder: "Character name", maxlength: "32", style: "width:160px", "aria-label": "New character name" });
  const sync = () => { newChar.style.display = charSel.value ? "none" : ""; };
  charSel.addEventListener("change", sync);
  if (g.me) {
    role.value = g.me.role;
    if (g.me.character && characters.includes(g.me.character)) charSel.value = g.me.character;
  }
  sync();

  const join = h("button", { class: "btn" }, icon(g.me ? "repeat" : "check", 15), g.me ? "Switch" : "Sign up");
  join.addEventListener("click", async () => {
    const character = charSel.value || newChar.value.trim();
    if (!character) return toast("Pick or enter a character.", "warn");
    const res = await act(join, () => gApi("POST", `/me/groups/${gid}/join`, { role: role.value, character }));
    if (res === undefined) return;
    toast(res.message);
    refresh();
  });
  const leave = h("button", { class: "btn secondary" }, icon("log-out", 15), "Leave");
  leave.addEventListener("click", async () => {
    if (!await confirmDialog(`Leave ${g.activity}?`, "Your slot becomes free.", "Leave")) return;
    if (await act(leave, () => gApi("POST", `/me/groups/${gid}/leave`), "You left the group.") !== undefined) refresh();
  });

  const full = g.filled >= g.size;
  const status = g.closed ? h("span", { class: "badge lock" }, icon("lock", 12), "Closed")
    : full ? h("span", { class: "badge ok" }, icon("check", 12), "Full")
    : h("span", { class: "badge recruiting" }, h("span", { class: "pulse" }), "Recruiting");
  const mine = org ? h("span", { class: "badge" }, icon("crown", 12), "Organizer")
    : g.me ? h("span", { class: `badge ${g.me.status === "group" ? "ok" : "wait"}` }, icon(g.me.status === "group" ? "user-check" : "clock", 12), g.me.status === "group" ? "You're in" : "Reserve")
    : null;
  const req = g.requirements ? h("div", { class: "meta-item" }, icon("shield", 14),
    g.requirements.roles.map(r => roleChip(r)), g.requirements.minGs ? h("span", { class: "chip" }, `GS ${g.requirements.minGs}+`) : null) : null;

  return h("div", { class: `card group-card${full ? " is-full" : ""}`, style: "margin:0" },
    h("div", { class: "group-head" },
      h("div", { class: "group-title" },
        g.project ? h("span", { class: "project-chip" }, icon("gamepad-2", 12), g.project) : null,
        h("h3", {}, g.activity)),
      h("div", { class: "group-state" }, status, capacity(g.filled, g.size))),
    h("div", { class: "group-meta" },
      h("div", { class: "meta-item" }, icon("crown", 14), member(g.owner)),
      h("div", { class: "meta-item" }, icon("calendar-clock", 14), g.startsAt ? fmtDate(g.startsAt) : "No time set"),
      g.channel ? h("div", { class: "meta-item" }, icon("hash", 14), g.channel) : null,
      mine, req),
    roster, reserve,
    g.closed ? h("div", { class: "muted small group-note" }, icon("lock", 14), "Signups are closed.") :
      g.blocked && !g.me ? h("div", { class: "muted small group-note" }, icon("lock", 14), g.blocked) :
      h("div", { class: "group-join" }, role, charSel, newChar, join, g.me && !org ? leave : null),
    h("div", { class: "group-actions" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 15), "Open in Discord"),
      g.voiceUrl ? h("a", { class: "btn secondary tone-teal voice-btn", href: g.voiceUrl, target: "_blank", rel: "noopener", title: "Open the group's voice room in Discord" }, icon("volume-2", 15), "Voice room") : null,
      org ? h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/me/groups/${gid}/time`) }, icon("clock", 15), "Time") : null,
      closeBtn ? h("span", { class: "spacer" }) : null, closeBtn));
}

// ================= Member: characters =================

async function pageChars(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "My characters"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(3)));
  main.append(holder);

  let data;
  const wanted = store.get("gb.charProject") || "";
  try { data = await gApi("GET", `/me/characters?project=${encodeURIComponent(wanted)}`); } catch (e) { holder.replaceChildren(empty(e.message)); return; }
  const pq = data.project ? `?project=${encodeURIComponent(data.project)}` : "";
  const project = data.projects.find(p => p.key === data.project);

  // Projects (games): each has its own characters; locked without the project role
  const projTabs = data.projects.length ? h("div", { class: "tabs" },
    data.projects.map(p => h("button", {
      class: `btn secondary${p.key === data.project ? " active-tab" : ""}`, disabled: !p.allowed,
      title: p.allowed ? "" : `Needs ${p.roles.map(r => "@" + r.name).join(" or ")}`,
      onclick: () => { store.set("gb.charProject", p.key); refresh(); }
    }, icon(p.allowed ? "gamepad-2" : "lock", 15), p.name))) : null;

  // Notifications
  const dm = h("input", { type: "checkbox", class: "switch", checked: data.dmEnabled, "aria-label": "Group DMs" });
  dm.addEventListener("change", () => act(null, () => gApi("PUT", "/me/dm", { enabled: dm.checked }),
    dm.checked ? "Group DMs turned on." : "Group DMs turned off."));
  const prefs = h("div", { class: "card pref-row" }, toneChip("bell", 16),
    h("div", { class: "grow" }, h("b", {}, "Group DMs"),
      h("div", { class: "muted small" }, "Get a DM when a group is posted for an activity whose role you have. Same as 🔔 / 🔕 under the LFG panel or /lfg-dm.")),
    dm);

  const isCustom = ic => /^<a?:[^:]+:\d+>$/.test(ic || "");
  const cards = data.characters.map(c => {
    // <option> can't show images: unicode icons go in the text, server/bot emojis are shown in the card header
    const cls = h("select", { "aria-label": "Class" }, h("option", { value: "" }, "No class"),
      data.classRoles.map(r => h("option", { value: r.id }, `${r.icon && !isCustom(r.icon) ? r.icon + " " : ""}${r.name}`)));
    if (c.classRole) cls.value = c.classRole.id;
    const badge = h("span", { class: "char-icon big" });
    const clsName = h("span", { class: "muted small" });
    const syncIcon = () => {
      const r = data.classRoles.find(x => x.id === cls.value);
      badge.replaceChildren(r && r.icon ? classMark({ classIcon: r.icon, classRole: r }) : icon("user-round", 18));
      clsName.textContent = r ? r.name : "No class";
    };
    cls.addEventListener("change", syncIcon);
    syncIcon();
    const note = h("input", { maxlength: String(data.maxComment), placeholder: "e.g. can lead, healer alt", value: c.comment || "" });
    const gs = h("input", { type: "number", min: "0", max: "99999", placeholder: "—", value: c.gs ?? "" });
    const save = h("button", { class: "btn" }, icon("save", 15), "Save");
    save.addEventListener("click", () => {
      if (gs.value !== "" && (Number(gs.value) < 0 || Number(gs.value) > 99999)) return toast("GS: 0–99999.", "warn");
      act(save, () => gApi("PUT", `/me/characters/${encodeURIComponent(c.name)}${pq}`, { classRoleId: cls.value || null, comment: note.value, gs: gs.value === "" ? null : Number(gs.value) }),
        r => r.groupsUpdated ? `Saved. Updated ${r.groupsUpdated} active group(s).` : "Saved.");
    });
    const rm = h("button", { class: "btn danger" }, icon("trash-2", 15), "Remove");
    rm.addEventListener("click", async () => {
      if (!await confirmDialog(`Remove ${c.name}?`, "The character, its class and note are deleted.", "Remove")) return;
      if (await act(rm, () => gApi("DELETE", `/me/characters/${encodeURIComponent(c.name)}${pq}`), "Removed.") !== undefined) refresh();
    });
    // Rename: class, note, GS and active groups follow the new name
    const rename = h("button", { class: "btn ghost icon-only sm", title: "Rename", "aria-label": `Rename ${c.name}` }, icon("pencil", 15));
    rename.addEventListener("click", async () => {
      const name = await promptDialog(`Rename ${c.name}`, c.name);
      if (!name || name === c.name) return;
      if (await act(rename, () => gApi("POST", `/me/characters/${encodeURIComponent(c.name)}/rename${pq}`, { name }),
        r => r.groupsUpdated ? `Renamed. Updated ${r.groupsUpdated} active group(s).` : "Renamed.") !== undefined) refresh();
    });
    return h("div", { class: "card char-edit" },
      h("div", { class: "char-edit-head" }, badge,
        h("div", { class: "grow" }, h("div", { class: "row", style: "gap:4px;flex-wrap:nowrap" }, h("b", { class: "char-name" }, c.name), rename), clsName),
        c.gs ? h("span", { class: "gs big" }, `GS ${c.gs}`) : null),
      h("div", { class: "char-fields" },
        h("div", { class: "field" }, h("label", {}, "Class"), cls),
        h("div", { class: "field gs-field" }, h("label", {}, "GS"), gs)),
      h("div", { class: "field" }, h("label", {}, "Personal note — only you see it"), note),
      h("div", { class: "char-actions" }, save, h("span", { class: "spacer" }), rm));
  });

  // Add a character
  const nameIn = h("input", { placeholder: "Character name", maxlength: "32" });
  const add = h("button", { class: "btn" }, icon("plus", 15), "Add");
  const doAdd = async () => {
    if (!nameIn.value.trim()) return toast("Enter a name.", "warn");
    if (await act(add, () => gApi("POST", `/me/characters${pq}`, { name: nameIn.value }), "Character added.") !== undefined) refresh();
  };
  add.addEventListener("click", doAdd);
  nameIn.addEventListener("keydown", e => { if (e.key === "Enter") doAdd(); });
  const addCard = h("div", { class: "card char-add" }, h("span", { class: "es-icon tone-cyan" }, icon("user-plus", 20)),
    h("div", { class: "grow" }, h("b", {}, "New character"), h("div", { class: "muted small" }, project ? `In ${project.name}` : "Saved for sign-ups")),
    h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), add));

  holder.replaceChildren(prefs, projTabs,
    h("p", { class: "muted small", style: "margin:-6px 0 14px" }, "Each game has its own characters. Class — any class of the project (its icon is shown after your name in group posts). GS is checked by group requirements."),
    h("div", { class: "char-edit-grid" }, cards, addCard));
}

// ================= Access (owner only) =================

async function pageAccess(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔑 Access"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: refresh }, "🔄 Refresh")));
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
  announce: {
    title: "Announcements — posts from the bot",
    features: [["What it can do", [
      "Write a post on the site and the bot publishes it in a text channel or in the chat of a 🔊 voice channel.",
      "Longer than Discord's 2000 characters — split automatically into several messages (at paragraphs, lines, sentences; code blocks stay whole). Up to 20000 characters.",
      "**Preview & confirm**: you see every message before anything is sent.",
      "**📅 Schedule**: send at a date and time (server time zone); edit, send now or cancel a scheduled post.",
      "**🔔 Ping**: pick @everyone / @here, roles and people — a mention line is added at the top and only they are notified. Mentions inside the text don't ping unless you tick «Also ping mentions written in the text».",
      "**📄 Templates**: save channel + text + pings and reuse them.",
      "**Sent** list: open in Discord, ↩️ reuse the text, delete the messages in Discord. Every action is in the Log."
    ]]],
    discord: [["In Discord", [
      "Posts are sent as the bot. To edit a sent post: delete its messages here and send a corrected one."
    ]]],
    panel: [["Using it", [
      "In the text type **@** → pick @everyone/@here, a role or a person (↑↓ + Enter or click); **#** → a channel. The text keeps Discord codes like `<@&123>`; under the field and in the preview they are shown by name.",
      "Optionally **📄 Load a template**. Pick the channel, write the text (Markdown works), add whom to **🔔 ping**, choose **Send now** or **📅 Schedule** → **👁 Preview & send** → check the messages → **✅ Send** / **📅 Schedule**.",
      "**💾 Save as template** stores the form (for a loaded template — update it or save a copy). **📄 Templates**: Use, ✏️ rename, Delete.",
      "**📅 Scheduled**: ✏️ Edit (loads it into the form → Preview & save), **Send now**, **Cancel** (asks first).",
      "**🗂️ Sent**: **Open**, **↩️ Reuse**, **Delete messages** (in Discord), **Remove** (only from this list).",
      "Access: the owner gives the ✍️ **Announcements** right in Access."
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
      "A role marked **(reserve)** has no free slot: you'll be put on the reserve list.",
      "Tabs at the top show the groups of one project (game) or 🌐 all; **🔊 Voice room** opens the group's voice room."
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
        "**➕ Create a group** — project, activity, slots, date/time, 🔊 voice room → **Publish**. It is posted in the project's LFG channel; you are the organizer and take slot 1.",
        "In your own groups: ⬇️ move a member to the reserve, ⬆️ move someone from the reserve into the group (they get a DM), ✖️ remove from the reserve, **Close group**."
      ]]
    ]
  },
  chars: {
    title: "My characters",
    features: [["What you can do here", [
      "Keep your list of characters for signups — separately for each project (game), switched with the tabs (🔒 = you don't have the project's role).",
      "Give each character a **class** (any class of the project — its icon is shown after your name in posts), a **GS** (checked by group requirements) and a **note** (only you see it, when picking a character).",
      "Turn group DMs on or off."
    ]]],
    discord: [["The same in Discord", [
      "`/character list`, `/character add`, `/character class name role`, `/character gs name 1450`, `/character note name text`, `/character rename name new_name`, `/character remove` — add `project:` for another game.",
      "🔔 / 🔕 under the LFG panel or `/lfg-dm` for DMs."
    ]]],
    panel: [["Using it", [
      "Pick the class, GS and note → **Save**. Active groups where you play this character are updated.",
      "✏️ next to the name renames the character (class, note, GS and active groups follow).",
      "**Add character** adds a new name; **Remove** deletes it with its class and note (asks first)."
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
      "`/lfg`, `/lfg-dm`, `/character list|add|class|gs|note|rename|remove` — groups and characters (everyone).",
      "`/room rename|limit|lock|unlock|permit|kick|claim|panel` — your temporary voice room (everyone).",
      "`/admin` — the in-Discord admin panel (bot admins).",
      "`/lfg-panel`, `/ticket-panel`, `/tempvoice …`, `/activity …`, `/ticket-project …` — setup commands (bot admins)."
    ]]],
    panel: [["Using the panel", [
      "Tabs on the left show only the sections you have access to.",
      "The cards on this page show counters; click one to open its section.",
      "❔ **Guide** (orange, top right) on every tab explains that module; 🔄 **Refresh** (green) reloads the data.",
      "Where a module works per project (LFG, Tickets, Groups, My characters) — tabs at the top switch the project.",
      "Every removal asks for confirmation first."
    ]]]
  },
  lfg: {
    title: "LFG — group & raid finder",
    features: [
      ["What it can do", [
        "Groups of 2–25 slots with roles ⚔️ DD / 🛡️ Tank / 💚 Sup / 🎲 All per slot.",
        "**Activities** — a name linked to a server role: the role is pinged and its members get a DM when a group is posted.",
        "Start time shown to everyone in their own time zone; ⏰ reminder before the start; the post is removed automatically after the start.",
        "**Characters**: members sign up with a saved character; each character can have a **class** (any class of its project, shown as an icon after the name), a **GS** and a **note** (only the owner sees it, when picking a character).",
        "**Reserve** when the needed slots are full; the organizer moves people between the group and the reserve (with a DM).",
        "📋 **Invite to group** — ready `/invite Name` commands; 🏰 **Invite to dungeon** — just the names.",
        "**Templates** (admin): activity + slots + requirements, picked in one click in /lfg and on the site.",
        "**Projects (games)**: each has its own characters, activities, classes, templates, panels and statistics; a project's groups and characters need its role.",
        "**Classes**: the admin picks which roles are classes and their icons (emoji, server emoji or the role's own icon) — only the icon is shown after the name.",
        "**Duplicate posts**: every new group is also posted in channels set by the admin (text channels or the chat of a voice channel); admins can add more after publishing. Copies stay in sync and are deleted with the group.",
        "**Requirements**: a required role and/or minimum character **GS** — others go to the reserve.",
        "🔊 **Voice room** for a group — only if turned on when creating; created in the category set by the admin. Everyone can join and listen, only group members can speak; the link is in the post; deleted with the group.",
        "**Change time** of a posted group — everyone signed up gets a DM.",
        "**Attendance** after the start → **statistics** and player reliability.",
        "Admin tab **LFG**: a tab per project + ⚙️ General — activities, panel, voice room category, duplicate posts, classes, templates, **recurring groups**, any group's roster, force-close, statistics.",
        "Member tab **Groups**: everyone with the member role signs up, creates groups and manages their own groups."
      ]]
    ],
    discord: [
      ["Create a group", [
        "Click **Create LFG** on the LFG panel (or use `/lfg`). A menu only you can see opens.",
        "Choose the size → the activity (or «Custom activity», no ping) → select slots and set their roles → **Next**.",
        "Pick the date and time (or «No time»), turn 🔊 **Voice room** on if needed → **Publish**. You take slot 1 with your last used character.",
        "The post is duplicated automatically to the channels set in the admin panel (📢 Duplicate posts). Admins can add more after publishing in «📢 Also post this group in…» (text or voice channels)."
      ]],
      ["Sign up", [
        "Click a role button under the group post, then pick a saved character or **New character**.",
        "Under the reply you can pick your **class** (any class of the project) and set **GS** and a **note** — saved for that character.",
        "Later: `/character class name role`, `/character gs name 1450`, `/character note name text` (empty = remove), `/character rename name new_name`, add `project:` for another game. `/character list` shows all.",
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
      ["Tabs", [
        "A tab per **project** (game) at the top + **⚙️ General**. A project tab shows only that project: activities, panel, voice rooms, duplicate posts, classes, templates, schedules, active groups and statistics.",
        "🌐 **all projects** items (activities, templates) are shared — shown in every project, managed in ⚙️ General.",
        "**✏️ Edit <project>** next to the tabs — rename it, change its required roles. **➕ Project** — add one.",
        "The ⭐ **default** project holds the characters, groups and panels made before projects; it can be renamed but not replaced or deleted while other projects exist."
      ]],
      ["Project tab", [
        "**Activities** — name + the role to ping. ✏️ **Edit** renames it or changes the role; the Project list moves it to another project or to 🌐 all projects.",
        "**LFG panel** — publishes the message with the Create LFG button in a channel (once, it stays there); groups created with it belong to the project. Below: the panels of this project.",
        "**🔊 Voice rooms** — the category where this project's group voice rooms are created. Empty = the common one (General), otherwise the category of the post's channel.",
        "**📢 Duplicate posts** — text channels and/or voice channels (their chat) where every new group of the project is posted too. Empty = the common list (General).",
        "**🎭 Classes** — the project's own classes (empty = the common list; **📋 Copy the common list** to start). **➕ Add class roles…** — tick several at once; **🎨 Use role icons** takes the roles' own icons from Discord.",
        "**📋 Templates**, **🔁 Recurring groups**, **Active groups**, **📈 Statistics** — of this project only."
      ]],
      ["⚙️ General", [
        "**🎮 Projects** and which project each posted LFG panel belongs to.",
        "**Shared activities**, **Channel for groups** (where members' groups from the site go if the project has no panel), the common **🔊 voice room** category and **📢 duplicate posts**.",
        "**⚙️ LFG settings** — server time zone, delete posts after N hours, reminder N minutes before the start (0 = off).",
        "**Common classes** and **shared templates**."
      ]],
      ["Creating groups", [
        "One-off groups are created in **Member → Groups → Create a group** (as an admin you are a member too) or with Create LFG / `/lfg` in Discord."
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
      ["Tabs", [
        "A tab per ticket **project** (the number = open tickets), **➕ New project** and **⚙️ General**."
      ]],
      ["Project tab", [
        "**Project name** (editable), leadership roles, roles given on Approve, the category for channels and the ticket text → **Save**.",
        "Saving a new name or ticket text updates the message in open tickets of that project.",
        "**Application form**: tick «Ask these questions», add up to 5 questions (text, hint, long/short, required, max length), **Save**.",
        "**Auto-close**: number of days without messages (2–90), empty = never. **Open tickets** of the project below; **🗑️ Remove** deletes the project (asks first)."
      ]],
      ["⚙️ General", [
        "**Ticket panel** — publishes the message with the Create ticket button in a channel (all projects in one panel).",
        "**Open tickets** of all projects — status, link, **Force close** (deletes the channels).",
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
      "Button labels under **LFG group posts** (Leave, Invite…, Manage, Close, Voice room) and in **ticket messages** (Approve, Deny, Close).",
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
  const btn = h("button", { class: "btn btn-guide", title: "How this section works", onclick: () => guideDialog(key) }, "❔ Guide");
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
