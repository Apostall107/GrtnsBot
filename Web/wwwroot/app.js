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

let apiKeepalive = false;

async function api(method, path, body) {
  const opts = { method, headers: { "X-Panel": "1" }, credentials: "same-origin" };
  if (apiKeepalive) opts.keepalive = true; // the tab is closing: let pending deletions finish
  // Owner only: «view as member» — the server then answers as for a normal member
  if (store.get("gb.viewAs") === "member") opts.headers["X-View-As"] = "member";
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
  if (pending.quiet && kind === "ok") return; // Save all: one summary instead of a toast per card
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
    // A short green flash on the button that did it
    if (button && button.isConnected) { button.classList.remove("ok-flash"); void button.offsetWidth; button.classList.add("ok-flash");
      setTimeout(() => button.classList.remove("ok-flash"), 1100); }
    return result;
  } catch (e) {
    toast(e.message || String(e), "error");
    return undefined;
  } finally {
    if (button) button.disabled = false;
  }
}

// ================= Undo instead of "Are you sure?" =================

/** Toast with an Undo button for a few seconds; onUndo runs if it is pressed. */
function undoToast(text, onUndo, ms = 6000) {
  const btn = h("button", { class: "toast-undo", type: "button" }, icon("undo-2", 14), "Undo");
  const el = h("div", { class: "toast ok has-undo" }, h("span", { class: "toast-text" }, text), btn,
    h("i", { class: "toast-timer", style: `animation-duration:${ms}ms` }));
  let done = false;
  const close = () => { done = true; el.remove(); };
  btn.addEventListener("click", () => { if (done) return; close(); onUndo(); });
  document.getElementById("toasts").append(el);
  setTimeout(() => { if (!done) close(); }, ms);
}

/** Local list edit (saved later with Save): removed at once, Undo puts it back. */
function removeLocal(list, i, redraw, text) {
  const [item] = list.splice(i, 1);
  redraw();
  undoToast(text, () => { list.splice(Math.min(i, list.length), 0, item); redraw(); });
}

const deferredRemovals = new Set();
/**
 * Server deletion with Undo: the element disappears right away, the request is sent after 6 s unless Undo is pressed
 * (or at once when the tab is closed). `after` runs when the deletion went through (usually a refresh).
 */
function removeWithUndo(el, text, run, after) {
  if (el) el.classList.add("removing");
  const job = { fired: false, timer: 0 };
  job.fire = async () => {
    if (job.fired) return;
    job.fired = true; clearTimeout(job.timer); deferredRemovals.delete(job);
    try { await run(); if (after) after(); }
    catch (e) { if (el) el.classList.remove("removing"); toast(e.message || String(e), "error"); }
  };
  job.timer = setTimeout(job.fire, 6000);
  deferredRemovals.add(job);
  undoToast(text, () => {
    if (job.fired) return;
    job.fired = true; clearTimeout(job.timer); deferredRemovals.delete(job);
    if (el) el.classList.remove("removing");
  });
}
window.addEventListener("pagehide", () => { apiKeepalive = true; for (const j of [...deferredRemovals]) j.fire(); });

// ================= Unsaved changes: one sticky bar for the whole page (Save all / Discard) =================

const pending = { items: [], saving: false, deferred: false, quiet: false, bar: null, timer: 0 };

/**
 * Tracks one editor (a card, a table row, a form). It is "unsaved" while snapshot() differs from the saved state.
 * save() → truthy on success. Returns { save, attach(root) → root, rebase() }.
 * Use t.save as the card's own Save button; the bar at the bottom saves every unsaved one at once.
 */
function track(name, snapshot, save) {
  const t = { name, snapshot, root: null, base: null, dirty: false, seen: false };
  const snap = () => { try { return JSON.stringify(snapshot()); } catch { return null; } };
  t.rebase = () => { t.base = snap(); t.dirty = false; t.root?.classList.remove("unsaved"); schedulePendingCheck(); };
  t.attach = root => {
    pending.items = pending.items.filter(x => x === t || x.root !== root);
    t.root = root;
    t.rebase();
    return root;
  };
  t.save = async () => {
    const ok = await save();
    if (ok) t.rebase();
    afterPendingSave();
    return ok;
  };
  t.check = () => {
    const live = !!t.root && t.root.isConnected;
    if (live) t.seen = true;
    t.dirty = live && t.base !== null && snap() !== t.base;
    t.root?.classList.toggle("unsaved", t.dirty);
  };
  pending.items.push(t);
  return t;
}

function pendingCheck() {
  for (const t of pending.items) t.check();
  pending.items = pending.items.filter(t => !(t.seen && !t.root.isConnected));
  drawPendingBar();
  return pending.items.filter(t => t.dirty);
}

function schedulePendingCheck() {
  clearTimeout(pending.timer);
  pending.timer = setTimeout(pendingCheck, 60);
}

function drawPendingBar() {
  if (!pending.bar) {
    const count = h("b", { class: "pending-count" });
    const names = h("span", { class: "pending-names muted small" });
    const discard = h("button", { class: "btn secondary", type: "button" }, icon("undo-2", 15), "Discard");
    const saveAll = h("button", { class: "btn", type: "button" }, icon("save", 15), h("span", {}, "Save all"));
    discard.addEventListener("click", async () => {
      const n = pendingCheck().length;
      if (n > 1 && !await confirmDialog(`Discard ${n} unsaved changes?`, "The page is reloaded with the saved values.", "Discard")) return;
      renderShell();
    });
    saveAll.addEventListener("click", savePendingAll);
    pending.bar = { el: h("div", { class: "pending-bar", role: "status" }, icon("triangle-alert", 18, "pending-icon"),
      h("div", { class: "pending-text" }, count, names), discard, saveAll), count, names, discard, saveAll };
    document.body.append(pending.bar.el);
  }
  const dirty = pending.items.filter(t => t.dirty);
  const show = dirty.length > 0 || pending.saving;
  const b = pending.bar;
  b.el.classList.toggle("show", show);
  document.body.classList.toggle("has-pending", show);
  if (!show) return;
  b.count.textContent = pending.saving ? "Saving…" : dirty.length === 1 ? "Unsaved changes" : `${dirty.length} unsaved changes`;
  b.names.textContent = dirty.map(t => t.name).join(" · ");
  b.names.title = b.names.textContent;
  b.saveAll.lastChild.textContent = dirty.length > 1 ? `Save all (${dirty.length})` : "Save";
  b.saveAll.disabled = b.discard.disabled = pending.saving;
}

async function savePendingAll() {
  const dirty = pendingCheck();
  if (!dirty.length) return;
  pending.saving = true;
  pending.quiet = dirty.length > 1;
  drawPendingBar();
  let ok = 0;
  try {
    for (const t of dirty) if (await t.save()) ok++;
  } finally {
    pending.saving = false;
    pending.quiet = false;
  }
  if (dirty.length > 1) toast(ok === dirty.length ? `Saved all ${ok} changes.` : `Saved ${ok} of ${dirty.length} — the highlighted ones still need a look.`,
    ok === dirty.length ? "ok" : "warn");
  afterPendingSave();
}

/** A refresh asked for while other edits are unsaved waits until they are saved or discarded. */
function afterPendingSave() {
  if (pending.saving) return;
  const dirty = pendingCheck();
  if (pending.deferred && !dirty.length) renderShell();
}

/** Before leaving the page (menu, tabs, server, Refresh): ask when something is unsaved. */
async function leavePage() {
  const n = pendingCheck().length;
  if (!n) return true;
  return confirmDialog(n === 1 ? "Leave without saving?" : `Leave without saving ${n} changes?`,
    "Your edits on this page are not saved yet. Use Save in the bar at the bottom to keep them.", "Discard changes");
}

function hardRefresh() { leavePage().then(ok => { if (ok) renderShell(); }); }

document.addEventListener("input", schedulePendingCheck, true);
document.addEventListener("change", schedulePendingCheck, true);
document.addEventListener("click", () => setTimeout(schedulePendingCheck, 30), true);
document.addEventListener("close", schedulePendingCheck, true); // dialogs (e.g. "Remove this question?")
setInterval(() => { if (pending.items.length) pendingCheck(); }, 1500);
window.addEventListener("beforeunload", e => {
  if (pendingCheck().length) { e.preventDefault(); e.returnValue = ""; }
});

const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } }
};

// Links from Discord (title of a group post): ?tab=play&guild=…&group=… → open Groups on that server with the group in focus.
// Saved before login, so it still works after the Discord login redirect.
(() => {
  const q = new URLSearchParams(location.search);
  if (!q.has("tab") && !q.has("group")) return;
  if (/^\d+$/.test(q.get("guild") || "")) store.set("gb.guild", q.get("guild"));
  if (/^[a-z]+$/.test(q.get("tab") || "")) store.set("gb.tab", q.get("tab"));
  if (/^[a-z0-9]+$/i.test(q.get("group") || "")) { store.set("gb.focusGroup", q.get("group")); store.set("gb.playProject", "all"); }
  history.replaceState(null, "", location.pathname);
})();

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
  return h("span", { class: "member", title: m.username ? `@${m.username}` : m.id, "data-uid": m.id },
    m.avatarUrl ? h("img", { class: "avatar sm", src: m.avatarUrl, alt: "" }) : h("span", { class: "avatar sm" }),
    m.name, m.onServer ? null : h("span", { class: "muted small" }, " (left)"));
}

/** A role's own icon on the server: custom image, else unicode emoji, else null. */
function roleIconEl(r, size = 16) {
  if (!r) return null;
  if (r.iconUrl) return h("img", { class: "gm-emo", src: r.iconUrl, alt: "", style: `width:${size}px;height:${size}px;border-radius:3px` });
  if (r.icon) return h("span", { style: `font-size:${size}px;line-height:1` }, r.icon);
  return null;
}

function roleChip(r, onRemove) {
  return h("span", { class: `chip${r.exists === false ? " warn" : ""}` },
    roleIconEl(r, 15) || h("span", { class: "swatch", style: r.color ? `background:${r.color}` : null }),
    `@${r.name}`,
    onRemove ? h("button", { title: "Remove", onclick: () => onRemove() }, "×") : null);
}

/** Empty list / table: the same dashed box everywhere. */
function empty(text) { return h("div", { class: "empty-state empty-inline" }, h("span", { class: "es-icon" }, icon("inbox", 18)), h("div", { class: "muted" }, text)); }
/** Load error (not an empty list). */
function errorState(text) { return h("div", { class: "empty-state error-state" }, h("span", { class: "es-icon" }, icon("triangle-alert", 18)), h("div", {}, text)); }

// ================= Dialogs =================

/** A free <dialog>: the second one is used when a dialog opens on top of another (e.g. member picker in a form). */
const dialog = () => {
  const first = document.getElementById("dialog");
  return first.open ? document.getElementById("dialog2") : first;
};

// ================= Member card: click any member (admins) → everything the bot knows about them =================

document.addEventListener("click", e => {
  const el = e.target.closest && e.target.closest(".member[data-uid]");
  if (!el || !isAdmin() || !state.guildId || e.target.closest("button, a, select, input, label, .menu-pop")) return;
  if (el.closest("dialog.member-card-dialog")) return;
  e.preventDefault();
  memberCard(el.dataset.uid);
});

async function memberCard(userId) {
  const d = dialog();
  // The dialog element is shared: drop the look of whatever used it before (search, wide editors)
  d.classList.remove("qs-dialog", "wide", "xwide", "page-dialog");
  d.classList.add("member-card-dialog");
  d.onclose = () => d.classList.remove("member-card-dialog");
  d.replaceChildren(h("div", { class: "mc-loading" }, h("span", { class: "mc-avatar mc-skel" }), h("div", { class: "muted" }, "Loading…")));
  if (!d.open) d.showModal();
  let c;
  try { c = await gApi("GET", `/members/${userId}/card`); } catch (e) { d.replaceChildren(errorState(e.message)); return; }
  const m = c.member;
  const accent = (c.roles.find(r => r.color && r.color !== "#000000" && r.color !== "#99aab5") || {}).color || "#8b6cff";
  d.style.setProperty("--mc", accent);
  const date = ms => ms ? new Date(ms).toLocaleDateString() : "—";
  const ago = ms => ms ? fmtRelative(ms) : "never";
  const none = text => h("div", { class: "mc-none" }, text);
  // Long lists: first 3, the rest behind "Show all"
  const capped = (rows, n = 3) => {
    const box = h("div", { class: "mc-list" }, rows.slice(0, n));
    if (rows.length > n) {
      const more = h("button", { type: "button", class: "mc-more" }, icon("chevron-down", 14), `Show all ${rows.length}`);
      more.addEventListener("click", () => { box.replaceChildren(...rows); });
      box.append(more);
    }
    return box;
  };
  const sec = (ic, tone, title, count, body) => h("section", { class: `mc-sec tone-${tone}` },
    h("h3", {}, h("span", { class: "mc-sec-ic" }, icon(ic, 14)), title, count != null ? h("span", { class: "mc-count" }, String(count)) : null), body);
  const stat = (ic, label, value, cls = "") => h("div", { class: `mc-stat ${cls}` }, h("span", { class: "mc-stat-ic" }, icon(ic, 15)),
    h("div", { class: "mc-stat-body" }, h("b", { title: value }, value), h("span", {}, label)));
  const bd = c.birthday ? `${String(c.birthday.day).padStart(2, "0")}.${String(c.birthday.month).padStart(2, "0")}${c.birthday.year ? "." + c.birthday.year : ""}` : "—";
  const OUT = { approved: ["ok", "Approved"], denied: ["lock", "Denied"], closed: ["wait", "Closed"] };
  const at = c.attendance;
  d.replaceChildren(
    h("div", { class: "mc-banner" },
      h("button", { class: "btn ghost icon-only sm mc-close", title: "Close", "aria-label": "Close", onclick: () => d.close() }, icon("x", 16))),
    h("div", { class: "mc-head" },
      h("div", { class: `mc-avatar-wrap${c.voiceNow ? " in-voice" : ""}` },
        m.avatarUrl ? h("img", { class: "mc-avatar", src: m.avatarUrl, alt: "" }) : h("span", { class: "mc-avatar" }),
        c.voiceNow ? h("span", { class: "mc-voice-dot", title: `In voice: ${c.voiceNow}` }, icon("volume-2", 11)) : null),
      h("div", { class: "mc-who" }, h("h2", {}, m.name),
        h("div", { class: "mc-handle" }, m.username ? `@${m.username}` : "", h("span", { class: "mc-id", title: "Copy ID",
          onclick: () => { navigator.clipboard?.writeText(m.id); toast("ID copied."); } }, m.id)),
        m.onServer ? null : h("span", { class: "badge lock" }, "left the server")),
      h("a", { class: "btn secondary sm mc-discord", href: `https://discord.com/users/${m.id}`, target: "_blank", rel: "noopener" }, icon("external-link", 14), "Discord")),
    c.roles.length ? (() => {
      const box = h("div", { class: "chip-wrap mc-roles" }, c.roles.slice(0, 6).map(r => roleChip(r)));
      if (c.roles.length > 6) {
        const more = h("button", { type: "button", class: "chip mc-more-roles", title: c.roles.slice(6).map(r => "@" + r.name).join(", ") }, `+${c.roles.length - 6}`);
        more.addEventListener("click", () => box.replaceChildren(...c.roles.map(r => roleChip(r))));
        box.append(more);
      }
      return box;
    })() : null,
    h("div", { class: "mc-stats" },
      stat("calendar", "joined", date(c.joinedAt)),
      stat("message-square-text", "last message", ago(c.lastMessage)),
      stat("headphones", c.voiceNow ? `in ${c.voiceNow}` : "last voice", c.voiceNow ? "in voice now" : ago(c.lastVoice), c.voiceNow ? "live" : ""),
      stat("cake", "birthday", bd, c.birthday ? "pink" : "")),
    h("div", { class: "mc-grid" },
      sec("user-round", "cyan", "Characters", c.characters.length, c.characters.length ? capped(c.characters.map(ch =>
        h("div", { class: "mc-row" }, h("div", { class: "mc-row-main" }, h("b", { title: ch.name }, ch.name), h("span", { class: "muted small" }, ch.classRole ? `@${ch.classRole.name} · ${ch.project}` : ch.project)),
          h("div", { class: "mc-row-side" }, ch.gs ? h("span", { class: "gs" }, `GS ${ch.gs}`) : null, ch.cp ? h("span", { class: "gs cp" }, `CP ${fmtCp(ch.cp)}`) : null)))) : none("No characters yet.")),
      sec("swords", "violet", "Groups now", c.groups.length, c.groups.length ? capped(c.groups.map(g =>
        h("div", { class: "mc-row" }, h("div", { class: "mc-row-main" }, h("b", { title: g.activity }, g.activity),
          h("span", { class: "muted small" }, [g.character, g.startsAt ? fmtDate(g.startsAt) : "no time"].filter(Boolean).join(" · "))),
          h("div", { class: "mc-row-side" }, g.organizer ? h("span", { class: "badge ok", title: "Organizer" }, icon("crown", 12)) : null,
            g.reserve ? h("span", { class: "badge wait" }, "reserve") : null)))) : none("Not in any group right now.")),
      sec("calendar-clock", "teal", "Attendance · 90 days", null, at ? h("div", {},
        h("div", { class: "mc-att" },
          // Bar = all groups: came · no-show · not marked by the organizer; % = came among the marked ones
          h("div", { class: "mc-att-bar", title: `${at.attended} came · ${at.noShows} no-show · ${at.unmarked} not marked` },
            h("i", { class: "ok", style: `width:${at.groups ? 100 * at.attended / at.groups : 0}%` }),
            h("i", { class: "no", style: `width:${at.groups ? 100 * at.noShows / at.groups : 0}%` }),
            h("i", { class: "un", style: `width:${at.groups ? 100 * at.unmarked / at.groups : 0}%` })),
          h("div", { class: "mc-att-rate" }, h("b", {}, at.rate == null ? "—" : `${at.rate}%`), h("span", {}, "of marked"))),
        h("div", { class: "mc-att-legend" },
          h("span", {}, h("i", { class: "ok" }), `${at.attended} came`), h("span", {}, h("i", { class: "no" }), `${at.noShows} no-show`),
          h("span", {}, h("i", { class: "un" }), `${at.unmarked} not marked`)),
        h("div", { class: "muted small" }, `${at.groups} group(s) · ${at.organized} organized`))
        : none("No groups in the last 90 days.")),
      sec("ticket", "amber", "Tickets", c.openTickets.length + c.pastTickets.length, c.openTickets.length || c.pastTickets.length ? capped([
        ...c.openTickets.map(t => h("div", { class: "mc-row" }, h("div", { class: "mc-row-main" }, h("b", { title: t.project }, t.project),
          h("span", { class: "muted small" }, `opened ${fmtRelative(t.openedAt)}`)), h("div", { class: "mc-row-side" }, h("span", { class: "badge recruiting" }, "open")))),
        ...c.pastTickets.map(t => { const [cls, label] = OUT[t.outcome] || ["wait", t.outcome];
          return h("div", { class: "mc-row" }, h("div", { class: "mc-row-main" }, h("b", { title: t.project }, t.project),
            h("span", { class: "muted small", title: t.by ? `by ${t.by}` : "" }, [date(t.closedAt), t.by ? `by ${t.by}` : null].filter(Boolean).join(" · "))),
            h("div", { class: "mc-row-side" }, h("span", { class: `badge ${cls}` }, label))); })])
        : none("No tickets."))));
}

/** Подтверждение опасного действия вводом слова (имени): кнопка активна, только когда введено верно. */
function confirmTyped(title, text, word, okLabel = "Confirm") {
  return new Promise(resolve => {
    const d = dialog();
    const input = h("input", { placeholder: word, style: "width:100%" });
    const ok = h("button", { class: "btn danger", disabled: true }, okLabel);
    input.addEventListener("input", () => { ok.disabled = input.value.trim() !== word.trim(); });
    input.addEventListener("keydown", e => { if (e.key === "Enter" && !ok.disabled) ok.click(); });
    let done = false;
    const finish = v => { if (done) return; done = true; d.close(); resolve(v); };
    ok.addEventListener("click", () => finish(true));
    d.replaceChildren(h("h2", {}, "⚠️ " + title), h("p", { class: "muted" }, text),
      h("div", { class: "field" }, h("label", {}, `Type “${word}” to confirm`), input),
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => finish(false) }, "Cancel"), ok));
    d.onclose = () => { if (!done) { done = true; resolve(false); } };
    d.showModal();
    input.focus();
  });
}

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
    h("button", { class: `btn secondary${k === current ? " active-tab" : ""}`, onclick: async () => { if (!await leavePage()) return; store.set(storeKey, k); renderShell(); } }, label)),
    ...extra);
}

/**
 * Pick several roles at once (checkboxes + search). Resolves with the chosen roles or null.
 * fullPool: when given, `pool` is the short list (e.g. roles of the project) and a toggle shows all of fullPool.
 */
function pickRolesDialog(title, exclude = [], pool = state.roles, fullPool = null) {
  return new Promise(resolve => {
    const d = dialog();
    const search = h("input", { placeholder: "Search roles…", style: "width:100%" });
    const shortIds = new Set(pool.map(r => r.id));
    const all = fullPool && fullPool.length > pool.length ? fullPool : pool;
    const items = all.filter(r => !exclude.includes(r.id)).map(r => {
      const cb = h("input", { type: "checkbox" });
      return { r, cb, short: shortIds.has(r.id), el: h("label", {}, cb, roleChip(r)) };
    });
    const showAll = h("input", { type: "checkbox" });
    const toggle = all !== pool ? h("label", { class: "row small muted", style: "margin-top:6px" }, showAll, "Show all server roles") : null;
    const none = empty("No more roles.");
    const list = h("div", { class: "check-list" }, items.map(x => x.el), none);
    const filter = () => {
      const q = search.value.trim().toLowerCase();
      let shown = 0;
      for (const x of items) {
        const visible = (showAll.checked || x.short || x.cb.checked) && (!q || x.r.name.toLowerCase().includes(q));
        x.el.style.display = visible ? "" : "none";
        if (visible) shown++;
      }
      none.style.display = shown ? "none" : "";
    };
    search.addEventListener("input", filter);
    showAll.addEventListener("change", filter);
    filter();
    const count = h("span", { class: "muted small", style: "margin-right:auto" }, "0 selected");
    list.addEventListener("change", () => { count.textContent = `${items.filter(x => x.cb.checked).length} selected`; });
    let result = null;
    const ok = h("button", { class: "btn", onclick: () => { result = items.filter(x => x.cb.checked).map(x => x.r); d.close(); } }, "➕ Add selected");
    d.replaceChildren(h("h2", {}, title), search, toggle, list,
      h("div", { class: "actions" }, count, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), ok));
    d.onclose = () => resolve(result && result.length ? result : null);
    d.showModal();
    search.focus();
  });
}

/** Pick several channels (search + highlight). Resolves with the chosen ids or null (cancel). */
function pickChannelsDialog(title, pool, selected = []) {
  return new Promise(resolve => {
    const d = dialog();
    const search = h("input", { placeholder: "Search channels…", style: "width:100%" });
    const chosen = new Set(selected);
    const items = pool.map(c => {
      const cb = h("input", { type: "checkbox", checked: chosen.has(c.id) });
      return { c, cb, el: h("label", {}, cb, icon(c.voice ? "volume-2" : "hash", 15, "muted"), h("span", {}, c.name)) };
    });
    const groups = [["Text channels", items.filter(x => !x.c.voice)], ["Voice channels (their chat)", items.filter(x => x.c.voice)]]
      .filter(([, list]) => list.length);
    const list = h("div", { class: "check-list" }, groups.flatMap(([label, list]) => [h("div", { class: "select-group" }, label), ...list.map(x => x.el)]));
    search.addEventListener("input", () => {
      const q = search.value.trim().toLowerCase();
      for (const x of items) x.el.style.display = !q || x.c.name.toLowerCase().includes(q) ? "" : "none";
    });
    const count = h("span", { class: "muted small", style: "margin-right:auto" });
    const syncCount = () => { count.textContent = `${items.filter(x => x.cb.checked).length} selected`; };
    list.addEventListener("change", syncCount);
    syncCount();
    let result = null;
    d.replaceChildren(h("h2", {}, title), search, list,
      h("div", { class: "actions" }, count, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"),
        h("button", { class: "btn", onclick: () => { result = items.filter(x => x.cb.checked).map(x => x.c.id); d.close(); } }, icon("check", 15), "Done")));
    d.onclose = () => resolve(result);
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

const RACES = [[2, "Asmodians"], [1, "Elyos"]];
let armoryServersCache = null;
/** EU servers of AION 2 (faction → servers), loaded once. */
async function armoryServers(path = "/me/armory/servers") {
  if (!armoryServersCache) armoryServersCache = await gApi("GET", path);
  return armoryServersCache;
}

/** Faction + server selects: the server list follows the faction. Returns { race, server, el, value() }. */
function realmPicker(servers, race, serverId, disabled = false) {
  const raceSel = h("select", { "aria-label": "Faction", disabled }, RACES.map(([v, l]) => h("option", { value: String(v) }, l)));
  const serverSel = h("select", { "aria-label": "Server", disabled });
  raceSel.value = String(race || 2);
  const fill = keep => {
    const list = servers.filter(x => String(x.race) === raceSel.value);
    serverSel.replaceChildren(h("option", { value: "" }, "Any server"), ...list.map(x => h("option", { value: String(x.id) }, x.name)));
    serverSel.value = list.some(x => String(x.id) === String(keep)) ? String(keep) : "";
  };
  fill(serverId);
  raceSel.addEventListener("change", () => fill(serverSel.value));
  return { race: raceSel, server: serverSel, value: () => ({ race: Number(raceSel.value), serverId: serverSel.value ? Number(serverSel.value) : null }) };
}

/** Find the character in the official AION 2 armory (EU): faction + server + name → pick one. Resolves to the hit or null. */
function armoryPickDialog(name, servers, race, serverId, initialHits = null) {
  return new Promise(resolve => {
    const d = dialog();
    const list = h("div", { class: "pick-list" });
    const input = h("input", { value: name, maxlength: "32", placeholder: "Character name", style: "flex:1;min-width:140px" });
    const realm = realmPicker(servers, race, serverId);
    const go = h("button", { class: "btn" }, icon("search", 15), "Search");
    const search = async () => {
      const q = input.value.trim();
      if (q.length < 2) return toast("Enter at least 2 characters.", "warn");
      const r = realm.value();
      list.replaceChildren(skeletonLines(2));
      try {
        let hits = initialHits || await gApi("GET", `/me/armory/search?name=${encodeURIComponent(q)}${r.serverId ? `&server=${r.serverId}` : ""}`);
        initialHits = null;
        hits = hits.filter(x => !x.serverId || Math.floor(x.serverId / 1000) === r.race);
        const exact = x => x.name.toLowerCase() === q.toLowerCase();
        hits.sort((a, b) => exact(b) - exact(a) || b.level - a.level);
        list.replaceChildren(...(hits.length ? hits.map(x => h("button", { class: exact(x) ? "exact" : "", onclick: () => { d.close(); resolve(x); } },
          h("b", {}, x.name), h("span", { class: "muted small" }, [x.server, x.className, x.level ? `Lv ${x.level}` : null].filter(Boolean).join(" · ")),
          exact(x) ? h("span", { class: "chip" }, "exact name") : null))
          : [empty("Nobody with this name — check the faction and server.")]));
      } catch (e) { list.replaceChildren(errorState(e.message)); }
    };
    go.addEventListener("click", search);
    input.addEventListener("keydown", e => { if (e.key === "Enter") search(); });
    realm.server.addEventListener("change", search);
    d.replaceChildren(h("h2", {}, "Link the official profile"),
      h("p", { class: "muted small" }, "Find your character on aion2.plaync.com (EU). After linking, combat power, item level and legion come from there — press Update when they change."),
      h("div", { class: "row", style: "flex-wrap:wrap;margin-bottom:8px" }, realm.race, realm.server),
      h("div", { class: "row", style: "flex-wrap:nowrap" }, input, go), list,
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(null); } }, "Cancel")));
    d.onclose = () => resolve(null);
    d.showModal();
    input.focus();
    if (name) search();
  });
}

/** Server chip colored by faction: Elyos — light blue, Asmodians — purple (race 1 / 2; by server ID 1xxx / 2xxx). */
function serverChip(name, race) {
  const cls = race === 1 ? " elyos" : race === 2 ? " asmo" : "";
  return h("span", { class: `chip server${cls}`, title: race === 1 ? "Elyos" : race === 2 ? "Asmodians" : "" }, name);
}

/** Visible link to the official AION 2 profile (aion2.plaync.com); null — not linked. */
function armoryLink(a, label = "Armory") {
  if (!a || !a.url) return null;
  return h("a", { class: "armory-link", href: a.url, target: "_blank", rel: "noopener",
    title: `Official profile on aion2.plaync.com${a.server ? ` · ${a.server}` : ""}` }, icon("external-link", 12), label);
}

/** Combat power like on the official site: 149159 → 149.2K. */
function fmtCp(v) {
  if (v == null) return "";
  return v >= 1e6 ? `${+(v / 1e6).toFixed(2)}M` : v >= 1e4 ? `${+(v / 1e3).toFixed(1)}K` : String(v);
}

/** Roster cell: combat power from the official armory (link to the profile) and the legion. */
function armoryCell(r) {
  const a = r.armory;
  if (!a) return h("span", { class: "muted" }, "—");
  return h("a", { href: a.url, target: "_blank", rel: "noopener", class: "armory-cell",
    title: `Official profile${a.server ? ` · ${a.server}` : ""}${a.synced ? ` · updated ${fmtRelative(a.synced)}` : ""}${a.error ? " · not found" : ""}` },
    a.combatPower != null ? a.combatPower.toLocaleString() : "profile", a.error ? " ⚠️" : "",
    null);
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
      } catch (e) { list.replaceChildren(errorState(e.message)); }
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

/** Список отдельных людей (кнопка «Add person…» с поиском) → { el, value() } (ID). */
function userListEditor(initial = [], onChange = null, addLabel = "Add person…") {
  let list = initial.filter(Boolean).map(m => ({ ...m }));
  // chips и button доступны и по отдельности — чтобы поставить «Add person…» в один ряд с «Add roles…»
  const chips = h("div", { class: "row", style: "gap:6px;flex-wrap:wrap" });
  const button = h("button", { class: "btn secondary", type: "button", onclick: async () => {
    const m = await pickMemberDialog("Add a person");
    if (m && !list.some(x => x.id === m.id)) { list.push(m); draw(); if (onChange) onChange(); }
  } }, icon("user-plus", 15), addLabel);
  const draw = () => {
    chips.replaceChildren(...list.map(m =>
      h("span", { class: "chip" }, member(m), h("button", { type: "button", title: "Remove",
        onclick: () => { list = list.filter(x => x.id !== m.id); draw(); if (onChange) onChange(); } }, "×"))));
    chips.style.display = list.length ? "" : "none";
    chips.style.marginBottom = list.length ? "6px" : "";
  };
  const el = h("div", {}, chips, button);
  draw();
  return { el, chips, button, value: () => list.map(m => m.id), count: () => list.length };
}

// ================= Shared UI pieces =================

/** Tone (color) of an icon's module: sections are told apart by color, not only by text. */
const ICON_TONES = {
  "users": "blue", "users-round": "violet", "swords": "violet", "circle-plus": "violet", "user-round": "cyan", "user-plus": "cyan",
  "ticket": "amber", "inbox": "amber", "file-pen-line": "amber", "headphones": "teal", "volume-2": "teal", "sliders-horizontal": "teal",
  "megaphone": "pink", "chart-column": "green", "circle-check": "green", "crown": "amber", "bell": "amber",
  "hash": "blue", "message-square-text": "blue", "bot": "green", "key-round": "amber", "pen-line": "pink", "layout-template": "blue",
  "pin": "violet", "drama": "cyan", "archive": "amber", "calendar": "pink", "repeat": "violet", "trending-up": "green",
  "database-backup": "green", "settings": "cyan", "clipboard-list": "violet", "folder": "amber", "file-text": "blue",
  "gamepad-2": "violet", "list": "blue", "shield-check": "green", "server": "blue", "list-filter": "blue", "scroll-text": "blue"
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
function statCard(ic, label, value, sub, onClick, trend) {
  return h(onClick ? "button" : "div", { class: `stat ${toneOf(ic)}${onClick ? " clickable" : ""}`, onclick: onClick || null },
    h("div", { class: "stat-top" }, h("span", { class: "label" }, label), h("span", { class: "stat-icon" }, icon(ic, 16))),
    h("div", { class: "value" }, value), sub ? h("div", { class: "sub" }, sub) : null, trend ? trendRow(trend) : null);
}

/** Change vs yesterday / 7 days ago (▲ ▼) + a sparkline of the daily snapshots. */
function trendRow(t) {
  const chip = (delta, period, title) => {
    const per = h("span", { class: "trend-period" }, period);
    if (delta === null || delta === undefined) return h("span", { class: "trend flat", title: `${title}: no snapshot yet` }, h("span", { class: "trend-num" }, "—"), per);
    const cls = delta > 0 ? "up" : delta < 0 ? "down" : "flat";
    const arrow = delta > 0 ? "▲" : delta < 0 ? "▼" : "•";
    return h("span", { class: `trend ${cls}`, title }, h("span", { class: "trend-num" }, delta === 0 ? "0" : `${Math.abs(delta)}${arrow}`), per);
  };
  if (!("day" in t) && !("week" in t)) return h("div", { class: "trend-row" }, sparkline(t.history || []));   // sparkline only
  return h("div", { class: "trend-row" },
    chip(t.day, "24h", "Change since yesterday's daily snapshot"),
    chip(t.week, "7d", "Change since the snapshot 7 days ago"),
    sparkline(t.history || []));
}

function sparkline(points) {
  if (points.length < 2) return null;
  const w = 64, hgt = 22, vals = points.map(p => p.value);
  const min = Math.min(...vals), max = Math.max(...vals), span = max - min || 1;
  const xy = vals.map((v, i) => `${(i / (vals.length - 1) * w).toFixed(1)},${(hgt - 2 - (v - min) / span * (hgt - 4)).toFixed(1)}`).join(" ");
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", `0 0 ${w} ${hgt}`); svg.setAttribute("width", w); svg.setAttribute("height", hgt); svg.setAttribute("class", "sparkline");
  const title = document.createElementNS(ns, "title");
  title.textContent = points.map(p => `${p.date}: ${p.value}`).join("\n");
  const line = document.createElementNS(ns, "polyline");
  line.setAttribute("points", xy); line.setAttribute("fill", "none"); line.setAttribute("stroke", "currentColor"); line.setAttribute("stroke-width", "1.6");
  svg.append(title, line);
  return svg;
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
  "✕": "x", "✖": "x", "✖️": "x", "🔍": "search", "↩️": "undo-2", "📅": "calendar", "🕒": "clock", "🔊": "volume-2", "📋": "clipboard-list", "⬇️": "download", "⬆️": "upload", "👤": "user-round", "🔎": "search",
  "⚙️": "settings", "🎨": "palette", "🎮": "gamepad-2", "🌐": "globe", "📣": "megaphone", "📝": "file-pen-line", "📨": "send", "🔒": "lock",
  "🔓": "lock-open", "🔁": "repeat", "📁": "folder", "📄": "file-text", "📢": "megaphone", "🔄": "refresh-cw", "↩": "undo-2", "📈": "trending-up", "↑": "chevron-up", "↓": "chevron-down", "👑": "crown", "↻": "refresh-cw", "❔": "circle-help", "🧩": "layers", "✉️": "mail-plus" };
/**
 * One look for cards, row actions and Save buttons, whatever page built them:
 * a bare card title gets the standard header, Delete/Remove/Edit in rows become icon buttons, Save gets its icon.
 */
// ----- Small motion helpers: numbers count up, a ripple where you click -----
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
function countUp(el) {
  el.dataset.counted = "1";
  const text = el.textContent.trim();
  const m = /^(\d[\d\s,]*)(.*)$/.exec(text);
  if (!m || reduceMotion || /^[.,]\d/.test(m[2])) return;
  const target = Number(m[1].replace(/[\s,]/g, ""));
  if (!Number.isFinite(target) || target < 2) return;
  const sep = /[\s,]/.test(m[1]) ? m[1].match(/[\s,]/)[0] : "";
  const fmt = n => sep ? String(n).replace(/\B(?=(\d{3})+(?!\d))/g, sep) : String(n);
  const t0 = performance.now(), dur = Math.min(900, 380 + target * 2);
  const step = now => {
    const p = Math.min(1, (now - t0) / dur), v = Math.round(target * (1 - Math.pow(1 - p, 3)));
    el.textContent = fmt(v) + m[2];
    if (p < 1 && el.isConnected) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}
if (!reduceMotion) document.addEventListener("pointerdown", e => {
  const b = e.target.closest && e.target.closest(".btn, .seg-btn, .nav-item, .quick-action, .group-mini, .att-row");
  if (!b || b.disabled) return;
  const r = b.getBoundingClientRect(), size = Math.max(r.width, r.height) * 2.2;
  // the ripple lives in its own clipped box, so the button keeps its own overflow (glows, active bars)
  const rip = h("span", { class: "ripple-box" }, h("span", { class: "ripple", style: `width:${size}px;height:${size}px;left:${e.clientX - r.left - size / 2}px;top:${e.clientY - r.top - size / 2}px` }));
  if (getComputedStyle(b).position === "static") b.style.position = "relative";
  b.append(rip);
  setTimeout(() => rip.remove(), 650);
}, { passive: true });

function normalizeUi(root) {
  for (const v of root.querySelectorAll(".stat .value:not([data-counted]), .dash-facts b:not([data-counted]), .mc-stat b:not([data-counted])")) countUp(v);
  for (const hd of root.querySelectorAll(".card > h2:first-child, .card > h3:first-child, .card > .section-label:first-child")) {
    if (hd.closest("dialog") || hd.parentElement.classList.contains("no-norm")) continue;
    const title = hd.tagName === "H2" ? hd : h("h2", { class: hd.className.replace(/\bsection-label\b/, "").trim() || null }, ...hd.childNodes);
    const head = h("div", { class: "card-head" });
    hd.replaceWith(head);
    head.append(title);
  }
  for (const b of root.querySelectorAll("td.actions > .btn:not([data-norm]), .list-row > .btn:not([data-norm]), .binding-row > .btn:not([data-norm]), .actions-row > .btn:not([data-norm])")) {
    b.dataset.norm = "1";
    const t = b.textContent.trim();
    const label = t.replace(/^[^\p{L}]+/u, "");
    if (/^(Delete|Remove)$/.test(label) && !b.classList.contains("icon-only")) {
      b.className = "btn ghost icon-only sm danger-text"; b.title = b.title || label; b.setAttribute("aria-label", label);
      b.replaceChildren(icon("trash-2", 15));
    } else if (/^Edit$/.test(label) && !b.classList.contains("icon-only")) {
      b.className = "btn secondary icon-only sm"; b.title = b.title || "Edit"; b.setAttribute("aria-label", "Edit");
      b.replaceChildren(icon("pencil", 15));
    }
  }
  // Trash icon buttons are always red
  for (const svg of root.querySelectorAll('.btn.icon-only > svg[data-icon="trash-2"]')) svg.parentElement.classList.add("danger-text");
  // Save buttons: always the save icon
  for (const b of root.querySelectorAll(".btn:not([data-save-norm])")) {
    b.dataset.saveNorm = "1";
    const first = b.firstChild;
    if (first && first.nodeType === Node.TEXT_NODE && /^Save\b/.test(first.textContent.trim())) b.insertBefore(icon("save", 16), first);
  }
  // Chip "×" buttons → a proper small x icon
  for (const x of root.querySelectorAll(".chip > button:not([data-norm])")) {
    x.dataset.norm = "1";
    if (x.textContent.trim() === "×") { x.replaceChildren(icon("x", 12)); x.classList.add("chip-x"); }
  }
}

function iconizeButtons(root) {
  for (const btn of root.querySelectorAll(".btn, .tabs button")) {
    const first = btn.firstChild;
    if (!first || first.nodeType !== Node.TEXT_NODE) continue;
    const key = Object.keys(EMOJI_ICONS).find(k => first.textContent.startsWith(k));
    if (!key) continue;
    first.textContent = first.textContent.slice(key.length).replace(/^️?\s*/u, "");
    btn.insertBefore(icon(EMOJI_ICONS[key], 16), first);
  }
}
// ================= Custom selects & number fields =================
// Every <select> gets a styled trigger + a glass list with search; long lists open as a popup window.
// The native <select> stays (hidden) as the source of truth: .value and "change" keep working everywhere.

const SELECT_POPUP_AT = 16;   // options from which the list opens as a centered popup
const SELECT_SEARCH_AT = 6;   // options from which a search field is shown

/** How an option looks: role pills for slot/role lists, class icons, plain text otherwise. */
function optionView(sel, opt) {
  const text = opt.textContent.trim();
  if (sel.dataset.render === "role") {
    const num = /^(\d+)\.\s*/.exec(text);
    const rest = text.replace(/^\d+\.\s*/, "");
    const reserve = /\(reserve\)/.test(rest);
    const label = rest.replace(/\(reserve\)/, "").replace(/^[^\p{L}]+/u, "").trim();
    return h("span", { class: "opt-view" }, num ? h("span", { class: "muted" }, `${num[1]}.`) : null, rolePill(label),
      reserve ? h("span", { class: "muted small" }, "reserve") : null);
  }
  const ic = opt.dataset.icon;
  const iconUrl = opt.dataset.iconUrl;
  const iconEl = iconUrl ? h("img", { class: "gm-emo", src: iconUrl, alt: "", style: "width:18px;height:18px;border-radius:3px" })
    : ic ? (classMark({ classIcon: ic }) || h("span", {}, ic)) : null;
  return h("span", { class: "opt-view" }, iconEl ? h("span", { class: "opt-icon" }, iconEl) : null, h("span", { class: "opt-text" }, text));
}

function enhanceSelect(sel) {
  if (sel.dataset.enhanced || sel.multiple || sel.closest(".no-enhance")) return;
  sel.dataset.enhanced = "1";
  const wrap = h("span", { class: "select" });
  if (sel.style.cssText) { wrap.style.cssText = sel.style.cssText; sel.style.cssText = ""; }
  const label = h("span", { class: "select-label" });
  const trigger = h("button", { type: "button", class: "select-trigger", "aria-haspopup": "listbox" }, label, icon("chevrons-up-down", 15, "select-chevron"));
  if (sel.title) trigger.title = sel.title;
  if (sel.getAttribute("aria-label")) trigger.setAttribute("aria-label", sel.getAttribute("aria-label"));
  sel.parentNode.insertBefore(wrap, sel);
  wrap.append(sel, trigger);
  sel.tabIndex = -1;
  sel.setAttribute("aria-hidden", "true");

  const sync = () => {
    const opt = sel.options[sel.selectedIndex];
    label.replaceChildren(opt ? optionView(sel, opt) : h("span", { class: "muted" }, "—"));
    trigger.disabled = sel.disabled;
    wrap.classList.toggle("placeholder", !sel.value);
  };
  sel._syncSelect = sync;
  // Programmatic .value / .selectedIndex changes update the label too
  for (const prop of ["value", "selectedIndex"]) {
    const d = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, prop);
    Object.defineProperty(sel, prop, { configurable: true, get() { return d.get.call(this); }, set(v) { d.set.call(this, v); sync(); } });
  }
  new MutationObserver(sync).observe(sel, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"] });
  sel.addEventListener("change", sync);
  trigger.addEventListener("click", () => openSelect(sel, trigger));
  trigger.addEventListener("keydown", e => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); openSelect(sel, trigger); }
    // Start typing on a focused field → the list opens with the filter filled in
    else if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && e.key !== " ") { e.preventDefault(); openSelect(sel, trigger, e.key); }
  });
  sync();
}

let openSelectClose = null;
function openSelect(sel, trigger, typed = "") {
  if (openSelectClose) openSelectClose();
  const opts = [...sel.options];
  const popup = opts.length >= SELECT_POPUP_AT;
  const host = trigger.closest("dialog[open]") || document.body;

  const search = h("input", { class: "select-search", placeholder: "Type to filter…", "aria-label": "Filter" });
  const list = h("div", { class: "select-list", role: "listbox" });
  let items = [], active = 0;
  const draw = () => {
    const q = search.value.trim().toLowerCase();
    items = [];
    const nodes = [];
    let group = null;
    for (const opt of opts) {
      const og = opt.parentElement.tagName === "OPTGROUP" ? opt.parentElement.label : null;
      if (q && !opt.textContent.toLowerCase().includes(q)) continue;
      if (og !== group) { group = og; if (og) nodes.push(h("div", { class: "select-group" }, og)); }
      const it = h("div", { class: `select-option${opt.selected ? " selected" : ""}${opt.disabled ? " disabled" : ""}`, role: "option",
        "aria-selected": opt.selected ? "true" : "false" }, optionView(sel, opt), opt.selected ? icon("check", 15, "select-check") : null);
      if (!opt.disabled) {
        const idx = items.length;
        items.push({ opt, el: it });
        it.addEventListener("mousedown", e => { e.preventDefault(); pick(idx); });
        it.addEventListener("mousemove", () => { if (active !== idx) { active = idx; mark(); } });
      }
      nodes.push(it);
    }
    list.replaceChildren(...(nodes.length ? nodes : [h("div", { class: "select-empty" }, "Nothing found")]));
    active = Math.max(0, items.findIndex(x => x.opt.selected));
    mark();
  };
  const mark = () => items.forEach((x, i) => x.el.classList.toggle("active", i === active));
  const pick = i => {
    const x = items[i];
    if (!x) return;
    if (sel.value !== x.opt.value) {
      sel.value = x.opt.value;
      sel.dispatchEvent(new Event("change", { bubbles: true }));
    }
    close();
    trigger.focus();
  };

  const box = h("div", { class: `select-pop${popup ? " popup" : ""}` },
    popup ? h("div", { class: "select-pop-head" }, h("b", {}, trigger.getAttribute("aria-label") || sel.title || (opts[0] && opts[0].value === "" ? opts[0].textContent.replace(/[…:]+$/, "") : "Choose")),
      h("button", { type: "button", class: "btn ghost icon-only sm", "aria-label": "Close", onclick: () => close() }, icon("x", 16))) : null,
    opts.length >= SELECT_SEARCH_AT || popup ? h("div", { class: "select-search-wrap" }, icon("search", 15), search) : null, list);
  const backdrop = popup ? h("div", { class: "select-backdrop", onmousedown: () => close() }) : null;
  if (backdrop) host.append(backdrop);
  host.append(box);

  const place = () => {
    if (popup) return;
    const r = trigger.getBoundingClientRect();
    const below = window.innerHeight - r.bottom - 12, above = r.top - 12;
    const h0 = Math.min(box.scrollHeight, 360);
    box.style.minWidth = `${Math.max(r.width, 200)}px`;
    box.style.left = `${Math.min(r.left, window.innerWidth - box.offsetWidth - 8)}px`;
    if (below < h0 && above > below) { box.style.top = ""; box.style.bottom = `${window.innerHeight - r.top + 6}px`; box.style.maxHeight = `${Math.min(360, above)}px`; }
    else { box.style.bottom = ""; box.style.top = `${r.bottom + 6}px`; box.style.maxHeight = `${Math.min(360, below)}px`; }
  };
  if (typed && box.querySelector(".select-search")) search.value = typed;
  draw();
  place();
  trigger.classList.add("open");
  (box.querySelector(".select-search") || list).focus?.();
  if (!box.querySelector(".select-search")) { box.tabIndex = -1; box.focus(); }
  setTimeout(() => { const s = list.querySelector(".selected"); if (s) s.scrollIntoView({ block: "nearest" }); });

  const onKey = e => {
    if (e.key === "ArrowDown") { active = Math.min(items.length - 1, active + 1); mark(); items[active]?.el.scrollIntoView({ block: "nearest" }); e.preventDefault(); }
    else if (e.key === "ArrowUp") { active = Math.max(0, active - 1); mark(); items[active]?.el.scrollIntoView({ block: "nearest" }); e.preventDefault(); }
    else if (e.key === "Enter") { pick(active); e.preventDefault(); }
    else if (e.key === "Escape") { close(); trigger.focus(); e.preventDefault(); e.stopPropagation(); }
    else if (e.key === "Tab") close();
  };
  search.addEventListener("input", draw);
  box.addEventListener("keydown", onKey);
  const outside = e => { if (!box.contains(e.target) && e.target !== trigger && !trigger.contains(e.target)) close(); };
  const onScroll = e => { if (!box.contains(e.target)) place(); };
  setTimeout(() => document.addEventListener("mousedown", outside, true));
  window.addEventListener("scroll", onScroll, true);
  window.addEventListener("resize", place);
  function close() {
    box.remove();
    if (backdrop) backdrop.remove();
    trigger.classList.remove("open");
    document.removeEventListener("mousedown", outside, true);
    window.removeEventListener("scroll", onScroll, true);
    window.removeEventListener("resize", place);
    openSelectClose = null;
  }
  openSelectClose = close;
}

/** Number fields: styled − / + steppers instead of the browser's arrows. */
function enhanceNumber(input) {
  if (input.dataset.enhanced) return;
  input.dataset.enhanced = "1";
  const wrap = h("span", { class: "num-field" });
  if (input.style.cssText) { wrap.style.cssText = input.style.cssText; input.style.cssText = ""; }
  input.parentNode.insertBefore(wrap, input);
  const step = dir => () => {
    const min = input.min === "" ? -Infinity : Number(input.min), max = input.max === "" ? Infinity : Number(input.max);
    const cur = input.value === "" ? (dir > 0 ? Math.max(0, min) - 1 : Math.max(0, min) + 1) : Number(input.value);
    input.value = String(Math.min(max, Math.max(min, cur + dir * (Number(input.step) || 1))));
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  };
  wrap.append(input, h("span", { class: "num-steps" },
    h("button", { type: "button", tabindex: "-1", "aria-label": "Increase", onclick: step(1) }, icon("chevron-up", 12)),
    h("button", { type: "button", tabindex: "-1", "aria-label": "Decrease", onclick: step(-1) }, icon("chevron-down", 12))));
}

// ================= Date & time pickers (glass popups instead of the browser's) =================

/** Keeps the hidden native input as the value holder: .value works, programmatic sets update the label. */
function hookValue(input, sync) {
  const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
  Object.defineProperty(input, "value", { configurable: true, get() { return d.get.call(this); }, set(v) { d.set.call(this, v); sync(); } });
}
function setInputValue(input, v) {
  input.value = v;
  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}
/** A floating glass popup under an anchor; returns close(). */
function popover(anchor, content, onClose) {
  const host = anchor.closest("dialog[open]") || document.body;
  const box = h("div", { class: "select-pop picker-pop" }, content);
  host.append(box);
  const place = () => {
    const r = anchor.getBoundingClientRect();
    const below = window.innerHeight - r.bottom - 12;
    box.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - box.offsetWidth - 8))}px`;
    if (below < box.offsetHeight && r.top > below) { box.style.top = ""; box.style.bottom = `${window.innerHeight - r.top + 6}px`; }
    else { box.style.bottom = ""; box.style.top = `${r.bottom + 6}px`; }
  };
  place();
  const outside = e => { if (!box.contains(e.target) && !anchor.contains(e.target)) close(); };
  const key = e => { if (e.key === "Escape") { close(); anchor.focus(); e.stopPropagation(); } };
  setTimeout(() => document.addEventListener("mousedown", outside, true));
  window.addEventListener("scroll", place, true);
  box.addEventListener("keydown", key);
  anchor.classList.add("open");
  function close() {
    box.remove();
    anchor.classList.remove("open");
    document.removeEventListener("mousedown", outside, true);
    window.removeEventListener("scroll", place, true);
    if (onClose) onClose();
  }
  return { box, close };
}

const pad2 = n => String(n).padStart(2, "0");
const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

function enhanceDate(input) {
  if (input.dataset.enhanced) return;
  input.dataset.enhanced = "1";
  const wrap = h("span", { class: "select picker" });
  if (input.style.cssText) { wrap.style.cssText = input.style.cssText; input.style.cssText = ""; }
  const label = h("span", { class: "select-label" });
  const trigger = h("button", { type: "button", class: "select-trigger" }, icon("calendar", 16, "picker-icon"), label);
  input.parentNode.insertBefore(wrap, input);
  wrap.append(input, trigger);
  input.tabIndex = -1;
  const sync = () => {
    const v = input.value;
    if (v) {
      const [y, m, d] = v.split("-").map(Number);
      label.textContent = new Date(y, m - 1, d).toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short", year: "numeric" });
    } else label.textContent = "Pick a date";
    wrap.classList.toggle("placeholder", !v);
  };
  hookValue(input, sync);
  sync();

  trigger.addEventListener("click", () => {
    const today = new Date();
    const sel = input.value ? input.value.split("-").map(Number) : null;
    let y = sel ? sel[0] : today.getFullYear(), m = sel ? sel[1] - 1 : today.getMonth();
    const grid = h("div", { class: "cal-grid" });
    const title = h("b", {});
    const draw = () => {
      title.textContent = `${MONTHS[m]} ${y}`;
      const first = (new Date(y, m, 1).getDay() + 6) % 7; // Monday first
      const days = new Date(y, m + 1, 0).getDate();
      const cells = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"].map(d => h("span", { class: "cal-dow" }, d));
      for (let i = 0; i < first; i++) cells.push(h("span", {}));
      for (let d = 1; d <= days; d++) {
        const isToday = d === today.getDate() && m === today.getMonth() && y === today.getFullYear();
        const isSel = sel && sel[0] === y && sel[1] - 1 === m && sel[2] === d;
        const past = new Date(y, m, d) < new Date(today.getFullYear(), today.getMonth(), today.getDate());
        cells.push(h("button", { type: "button", class: `cal-day${isToday ? " today" : ""}${isSel ? " selected" : ""}${past ? " past" : ""}`,
          onclick: () => { setInputValue(input, `${y}-${pad2(m + 1)}-${pad2(d)}`); pop.close(); trigger.focus(); } }, d));
      }
      grid.replaceChildren(...cells);
    };
    const nav = dir => () => { m += dir; if (m < 0) { m = 11; y--; } if (m > 11) { m = 0; y++; } draw(); };
    const pop = popover(trigger, h("div", { class: "cal" },
      h("div", { class: "cal-head" },
        h("button", { type: "button", class: "btn ghost icon-only sm", "aria-label": "Previous month", onclick: nav(-1) }, icon("arrow-left", 16)),
        title,
        h("button", { type: "button", class: "btn ghost icon-only sm", "aria-label": "Next month", onclick: nav(1) }, icon("arrow-right", 16))),
      grid,
      h("div", { class: "cal-foot" },
        h("button", { type: "button", class: "btn ghost sm", onclick: () => { setInputValue(input, ""); pop.close(); } }, "Clear"),
        h("button", { type: "button", class: "btn secondary sm", onclick: () => {
          setInputValue(input, `${today.getFullYear()}-${pad2(today.getMonth() + 1)}-${pad2(today.getDate())}`); pop.close();
        } }, "Today"))));
    draw();
  });
}

function enhanceTime(input) {
  if (input.dataset.enhanced) return;
  input.dataset.enhanced = "1";
  const wrap = h("span", { class: "select picker" });
  if (input.style.cssText) { wrap.style.cssText = input.style.cssText; input.style.cssText = ""; }
  // Typing works too: the visible field is a text box with HH:MM
  const text = h("input", { class: "time-text", placeholder: "--:--", maxlength: "5", inputmode: "numeric", "aria-label": "Time" });
  const btn = h("button", { type: "button", class: "time-btn", "aria-label": "Pick time" }, icon("clock", 16));
  input.parentNode.insertBefore(wrap, input);
  wrap.append(input, h("span", { class: "select-trigger time-field" }, text, btn));
  input.tabIndex = -1;
  const sync = () => { if (document.activeElement !== text) text.value = input.value || ""; wrap.classList.toggle("placeholder", !input.value); };
  hookValue(input, sync);
  sync();
  const commit = () => {
    const m = /^(\d{1,2})[:.\s]?(\d{2})$/.exec(text.value.trim());
    if (!text.value.trim()) setInputValue(input, "");
    else if (m && +m[1] < 24 && +m[2] < 60) setInputValue(input, `${pad2(+m[1])}:${m[2]}`);
    text.value = input.value;
  };
  text.addEventListener("blur", commit);
  text.addEventListener("keydown", e => { if (e.key === "Enter") { e.preventDefault(); commit(); } });

  const field = wrap.querySelector(".time-field");
  btn.addEventListener("click", () => {
    let [hh, mm] = (input.value || "20:00").split(":").map(Number);
    const col = (count, stepVal, cur, onPick) => h("div", { class: "time-col" }, Array.from({ length: count }, (_, i) => i * stepVal).map(v =>
      h("button", { type: "button", class: `time-cell${v === cur ? " selected" : ""}`, onclick: e => { onPick(v); [...e.target.parentNode.children].forEach(c => c.classList.toggle("selected", c === e.target)); } }, pad2(v))));
    const apply = () => setInputValue(input, `${pad2(hh)}:${pad2(mm)}`);
    const hours = col(24, 1, hh, v => { hh = v; apply(); });
    const mins = col(12, 5, mm - mm % 5, v => { mm = v; apply(); });
    const pop = popover(field, h("div", { class: "time-pop" },
      h("div", { class: "time-cols" }, h("div", {}, h("div", { class: "cal-dow" }, "Hour"), hours), h("div", {}, h("div", { class: "cal-dow" }, "Min"), mins)),
      h("div", { class: "cal-foot" },
        h("button", { type: "button", class: "btn ghost sm", onclick: () => { setInputValue(input, ""); pop.close(); } }, "Clear"),
        h("button", { type: "button", class: "btn sm", onclick: () => { apply(); pop.close(); } }, "Done"))));
    setTimeout(() => pop.box.querySelectorAll(".time-cell.selected").forEach(c => c.scrollIntoView({ block: "center" })));
  });
}

function enhanceControls(root) {
  for (const sel of root.querySelectorAll("select:not([data-enhanced])")) enhanceSelect(sel);
  for (const n of root.querySelectorAll("input[type=number]:not([data-enhanced])")) enhanceNumber(n);
  for (const d of root.querySelectorAll("input[type=date]:not([data-enhanced])")) enhanceDate(d);
  for (const t of root.querySelectorAll("input[type=time]:not([data-enhanced])")) enhanceTime(t);
  for (const ta of root.querySelectorAll("textarea:not([data-autogrow])")) autoGrowSetup(ta);
}

// ----- Text areas grow with their text (up to 75% of the screen, then they scroll)
const growObserver = "IntersectionObserver" in window
  ? new IntersectionObserver(entries => entries.forEach(e => { if (e.isIntersecting) autoGrow(e.target); })) : null;
function autoGrow(ta) {
  if (!ta.isConnected || !ta.offsetParent) return;
  if (!ta.dataset.minH) ta.dataset.minH = String(ta.offsetHeight || 0);
  const max = Math.round(window.innerHeight * 0.75);
  ta.style.height = "auto";
  const want = ta.scrollHeight + (ta.offsetHeight - ta.clientHeight);
  ta.style.height = Math.min(Math.max(want, Number(ta.dataset.minH)), max) + "px";
  ta.style.overflowY = want > max ? "auto" : "hidden";
}
function autoGrowSetup(ta) {
  ta.dataset.autogrow = "1";
  // Value set from code (templates, reset) — catch it too
  const d = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value");
  Object.defineProperty(ta, "value", { configurable: true, get() { return d.get.call(this); }, set(v) { d.set.call(this, v); requestAnimationFrame(() => autoGrow(this)); } });
  if (growObserver) growObserver.observe(ta);
  autoGrow(ta);
}
document.addEventListener("input", e => { if (e.target instanceof HTMLTextAreaElement) autoGrow(e.target); });
document.addEventListener("focusin", e => { if (e.target instanceof HTMLTextAreaElement) autoGrow(e.target); });
window.addEventListener("resize", () => document.querySelectorAll("textarea[data-autogrow]").forEach(autoGrow));

/** Card headings: a leading emoji (or a known title) becomes a tone icon chip. */
const HEADING_EMOJI = { "🧩": "layers", "✉️": "mail-plus", "✉": "mail-plus", "⚙️": "settings", "🎭": "drama", "🎮": "gamepad-2", "👥": "users", "💾": "database-backup", "📄": "file-text",
  "📅": "calendar", "📈": "trending-up", "📋": "clipboard-list", "📝": "file-pen-line", "🔁": "repeat", "🗂️": "folder", "🗄️": "archive",
  "🔊": "headphones", "📢": "megaphone", "📌": "pin", "🎫": "ticket", "📣": "megaphone", "🔑": "key-round", "📊": "chart-column", "📜": "scroll-text" };
const HEADING_TEXT = [["Activities", "swords"], ["Shared activities", "swords"], ["LFG panel", "pin"], ["Voice rooms", "headphones"],
  ["Duplicate posts", "megaphone"], ["Channel for groups", "hash"], ["Active groups", "users-round"], ["Open tickets", "ticket"],
  ["Ticket panel", "pin"], ["Room control panel", "pin"], ["New project", "circle-plus"], ["'Join to create' channels", "headphones"], ["Create a 'join to create' channel", "circle-plus"],
  ["Use an existing voice channel", "volume-2"], ["Active rooms", "volume-2"], ["Room panel", "pin"], ["Bot messages in a channel", "message-square-text"],
  ["Panels", "layout-template"], ["Bot", "bot"], ["Give access", "key-round"], ["People with access", "users"], ["New post", "pen-line"],
  ["Edit scheduled post", "pen-line"], ["Classes", "drama"], ["Common classes", "drama"], ["Templates", "clipboard-list"], ["Log", "scroll-text"],
  ["Filters", "list-filter"], ["Settings", "settings"], ["Member roles", "users"], ["Project name", "folder"]];
function iconizeHeadings(root) {
  for (const hd of root.querySelectorAll(".card h2:not(.with-icon):not([data-iconized]), .card h3:not(.with-icon):not([data-iconized])")) {
    hd.dataset.iconized = "1";
    if (hd.closest("dialog")) continue;
    const text = hd.textContent.trim();
    let ic = null, rest = text;
    const key = Object.keys(HEADING_EMOJI).find(k => text.startsWith(k));
    if (key) { ic = HEADING_EMOJI[key]; rest = text.slice(key.length).replace(/^\uFE0F?\s*/u, ""); }
    else {
      const m = HEADING_TEXT.find(([t]) => text === t || text.startsWith(t + " ·") || text.startsWith(t + " "));
      if (m) ic = m[1];
    }
    if (!ic) continue;
    if (key) {
      // keep child nodes after the emoji (e.g. badges)
      const first = hd.firstChild;
      if (first && first.nodeType === Node.TEXT_NODE) first.textContent = first.textContent.slice(key.length).replace(/^\uFE0F?\s*/u, "");
      else hd.textContent = rest;
    }
    hd.classList.add("with-icon");
    hd.prepend(toneChip(ic, 16));
  }
}

/**
 * Меньше серого текста: длинные пояснения под полями, во вступлении карточки и страницы превращаются в «?» рядом
 * с подписью/заголовком (текст — по наведению). Короткие строки, счётчики и тексты со ссылками/кнопками не трогаются.
 * Каждый элемент проверяется один раз — при первом появлении (динамические строки, пустые в начале, остаются как есть).
 */
const HINT_MIN = 45;
function hintText(el) {
  if (el.dataset.hintChecked) return null;
  el.dataset.hintChecked = "1";
  if (el.closest("dialog, .dc-frame, .guide-body, .no-hints")) return null;
  if (el.querySelector("a, button, input, select, textarea, img, .chip, .member")) return null;
  const t = el.textContent.replace(/\s+/g, " ").trim();
  return t.length >= HINT_MIN ? t : null;
}
function addHint(target, text) {
  const existing = target.querySelector(":scope > .hint");
  if (existing) { existing.dataset.tip += "\n\n" + text; existing.setAttribute("aria-label", existing.dataset.tip); return; }
  target.append(hint(text));
}
function tidyHints(root) {
  // 1) Пояснение под полем → «?» у подписи поля
  for (const el of root.querySelectorAll(".field > span.muted.small, .field > p.muted.small, .field > p.muted")) {
    const label = el.parentElement.querySelector(":scope > label");
    if (!label || label.querySelector("input")) { el.dataset.hintChecked = "1"; continue; }
    const t = hintText(el);
    if (!t) continue;
    label.classList.add("field-label");
    addHint(label, t);
    el.remove();
  }
  // 2) Вступление карточки (сразу после заголовка) → «?» у заголовка
  for (const el of root.querySelectorAll(".card > p.muted, .card > p.muted.small")) {
    const prev = el.previousElementSibling;
    const head = prev && (prev.matches(".card-head") ? prev.querySelector("h2, h3") : prev.matches("h2, h3, .section-label") ? prev : null);
    if (!head) { el.dataset.hintChecked = "1"; continue; }
    const t = hintText(el);
    if (!t) continue;
    addHint(head, t);
    head.classList.add("has-hint");
    el.remove();
  }
  // 3) Вступление страницы (под заголовком страницы) → «?» у заголовка страницы
  for (const el of root.querySelectorAll("main > p.muted, main > p.muted.small, main > div > p.muted.small:first-child")) {
    const prev = el.previousElementSibling || (el.parentElement !== root && el.parentElement.previousElementSibling);
    const h1 = prev && prev.matches(".card-head") ? prev.querySelector("h1, h2") : null;
    if (!h1) { el.dataset.hintChecked = "1"; continue; }
    const t = hintText(el);
    if (!t) continue;
    addHint(h1, t);
    h1.classList.add("has-hint", "page-hint");
    el.remove();
  }
  // 4) Пояснение сразу под вкладками → «?» у заголовка страницы
  for (const el of root.querySelectorAll(".tabs + p.muted, .tabs + p.muted.small")) {
    const h1 = el.closest("main") && el.closest("main").querySelector(".card-head h1");
    if (!h1) { el.dataset.hintChecked = "1"; continue; }
    const t = hintText(el);
    if (!t) continue;
    addHint(h1, t);
    h1.classList.add("has-hint", "page-hint");
    el.remove();
  }
}

// Page heading turns to glass once the page is scrolled (see .page-head in app.css)
window.addEventListener("scroll", () => document.body.classList.toggle("scrolled", window.scrollY > 4), { passive: true });

let iconizeQueued = false;
new MutationObserver(() => {
  if (iconizeQueued) return;
  iconizeQueued = true;
  requestAnimationFrame(() => { iconizeQueued = false; decorateHeading(document.querySelector("main.main")); iconizeButtons(document.body); normalizeUi(document.body); iconizeHeadings(document.body); enhanceControls(document.body); tidyHints(document.body); });
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
    pool.filter(r => !exclude.includes(r.id)).map(r =>
      h("option", { value: r.id, "data-icon": r.icon || null, "data-icon-url": r.iconUrl || null }, `@${r.name}`)));
}

function channelSelect(kind, placeholder) {
  const list = state.channels[kind] || [];
  return h("select", {},
    h("option", { value: "" }, placeholder),
    list.map(c => h("option", { value: c.id, disabled: kind === "text" && !c.canPost },
      `${kind === "categories" ? "" : "#"}${c.name}${c.category ? `  ·  ${c.category}` : ""}${kind === "text" && !c.canPost ? "  (no access)" : ""}`)));
}

/**
 * Editable list of roles: chips + "add" picker. Returns { el, value(), roles() }.
 * pool: array or a function returning the short list for the picker (e.g. roles of the current project);
 * with fullPool the picker can also show all server roles. onChange — after add/remove.
 */
function roleListEditor(initial, pool = state.roles, onChange = null, fullPool = null, addLabel = "➕ Add roles…", people = null) {
  let ids = initial.map(r => r.id);
  const known = new Map(initial.map(r => [r.id, r]));
  for (const r of fullPool || (typeof pool === "function" ? pool() : pool)) known.set(r.id, { ...r, exists: true });
  const el = h("div", {});
  const changed = () => { draw(); if (onChange) onChange(); };
  const draw = () => {
    // Search + tick several roles at once (the plain dropdown was too long)
    const sel = h("button", { class: "btn secondary", type: "button" }, addLabel);
    sel.addEventListener("click", async () => {
      const short = typeof pool === "function" ? pool() : pool;
      const picked = await pickRolesDialog(addLabel.replace(/^➕\s*|…$/g, ""), ids, short, fullPool);
      if (picked) { for (const r of picked) known.set(r.id, { ...r, exists: true }); ids.push(...picked.map(r => r.id)); changed(); }
    });
    // people — userListEditor: люди показываются под ролями, «Add person…» — рядом с «Add roles…»
    el.replaceChildren(...[
      h("div", { class: "chip-wrap" }, ids.length ? ids.map(id => roleChip(known.get(id) || { id, name: "deleted role", exists: false }, () => { ids = ids.filter(x => x !== id); changed(); }))
                              : people && people.count() ? null : h("span", { class: "muted" }, "not set")),
      people ? h("div", { style: "margin-top:6px" }, people.chips) : null,
      h("div", { class: "row", style: "margin-top:6px;gap:6px;flex-wrap:wrap" }, sel, people ? people.button : null)].filter(Boolean));
  };
  draw();
  return { el, redraw: draw, value: () => ids.slice(), roles: () => ids.map(id => known.get(id) || { id, name: "deleted role", exists: false }) };
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
  ["achievements", "trophy", "Achievements", "member", "Member"],
  ["schedule", "calendar-clock", "Calendar", "member", "Member"],
  ["roster", "users", "Guild roster", "member", "Member"],
  ["lfg", "swords", "LFG", "lfg", "Games"],
  ["guild", "calendar", "Guild", "guild", "Games"],
  ["welcome", "door-open", "Welcome", "welcome", "Community"],
  ["roles", "user-plus", "Role panels", "roles", "Community"],
  ["tickets", "ticket", "Tickets", "tickets", "Community"],
  ["voice", "headphones", "Temp Voice", "voice", "Community"],
  ["birthdays", "cake", "Birthdays", "birthdays", "Community"],
  ["pages", "layers", "Channel pages", "pages", "Posts & channels"],
  ["announce", "megaphone", "Announcements", "announce", "Posts & channels"],
  ["content", "layout-template", "Panels & posts", "content", "Posts & channels"],
  ["media", "image", "Media library", "media", "Posts & channels"],
  ["overview", "layout-dashboard", "Dashboard", "admin", "Administration"],
  ["log", "scroll-text", "Log", "log", "Administration"],
  ["settings", "settings", "Settings", "settings", "Administration"],
  ["access", "key-round", "Access", "access", "Owner tools"],
  ["voicenow", "headphones", "Voice now", "access", "Owner tools"],
  ["mascot", "bot", "Mascot", "access", "Owner tools"]
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
const MEMBER_TABS = ["home", "play", "chars", "achievements"];
const visibleTabs = () => allTabs.filter(t => can(t[3]));
const PERM_LABELS = { lfg: "LFG", guild: "Guild", tickets: "Tickets", voice: "Temp Voice", welcome: "Welcome", roles: "Role panels", texts: "Panel texts", posts: "Posts", announce: "Announcements", pages: "Channel pages", media: "Media library", birthdays: "Birthdays", log: "Log", settings: "Settings" };
const PERM_ICONS = { lfg: "swords", guild: "calendar", tickets: "ticket", voice: "headphones", welcome: "door-open", roles: "user-plus", texts: "file-pen-line", posts: "pin", announce: "megaphone", pages: "layers", birthdays: "cake", log: "scroll-text", settings: "settings" };

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

// ----- Glass atmosphere: slowly drifting light behind the glass + a soft spotlight that follows the pointer on cards
(function glassFx() {
  if (!document.querySelector(".gv-aurora"))
    document.body.prepend(h("div", { class: "gv-aurora", "aria-hidden": "true" }, h("i", {}), h("i", {}), h("i", {}), h("i", {})));
  if (matchMedia("(prefers-reduced-motion: reduce)").matches || matchMedia("(pointer: coarse)").matches) return;
  // Pointer light inside the card under the cursor: a glow + a lit piece of the rim, both moved with transform
  // (composited layers — the glass itself is not repainted, so it keeps up while moving and scrolling) and clipped to the card.
  const SEL = ".main .card, .stat, .quick-action, .group-mini";
  // Only the edges light up near the cursor: a bright line on the rim + a soft halo just outside it (no light inside the card)
  const rimDot = h("i", {}), haloDot = h("i", {});
  const light = h("div", { class: "gv-light", "aria-hidden": "true" }, h("div", { class: "gv-halo" }, haloDot), h("div", { class: "gv-rim" }, rimDot));
  let x = -1, y = -1, queued = false, host = null;
  const frame = () => {
    queued = false;
    const hit = x < 0 ? null : document.elementFromPoint(x, y);
    let el = hit && hit.closest && !hit.closest("dialog, .user-pop, .menu-pop, .select-pop, .page-head") ? hit.closest(SEL) : null;
    // nested card → light the outer glass card
    while (el && el.parentElement && el.parentElement.closest(SEL)) el = el.parentElement.closest(SEL);
    if (el !== host) {
      light.classList.remove("on");
      host = el;
      if (!el) return;
      el.append(light);
      void light.offsetWidth;
    }
    if (!el) return;
    const r = el.getBoundingClientRect();
    const lx = x - r.left, ly = y - r.top;
    rimDot.style.transform = `translate3d(${lx}px, ${ly}px, 0)`;
    haloDot.style.transform = `translate3d(${lx + 10}px, ${ly + 10}px, 0)`;
    light.classList.add("on");
  };
  const schedule = () => { if (!queued) { queued = true; requestAnimationFrame(frame); } };
  document.addEventListener("pointermove", e => { x = e.clientX; y = e.clientY; schedule(); }, { passive: true });
  document.addEventListener("scroll", schedule, { passive: true, capture: true });
  document.documentElement.addEventListener("pointerleave", () => { x = -1; schedule(); });
})();

// ----- Easter egg: now and then our little Greatness robot (from the bot's icon) flies across the page.
// It flies over the page area (not the sidebar); while it flies, clicks right around it (where it reacts to the cursor) are ignored.
// The robot's escape spells (key, emoji, name) — also used by the owner's "Summon" buttons
const SPELLS = [["hole", "🌀", "Black hole"], ["fire", "🔥", "Fireball"], ["bolt", "⚡", "Lightning"], ["rock", "🪨", "Stone barrage"], ["air", "🌪️", "Tornado"],
  ["ice", "❄️", "Ice storm"], ["meteor", "☄️", "Meteor"], ["water", "🌊", "Tidal wave"], ["seal", "👑", "Seal of Greatness"], ["acid", "☠️", "Acid cloud"],
  ["laser", "🔴", "Orbital beam"], ["claws", "🖤", "Shadow claws"], ["glitch", "📺", "Glitch"], ["sakura", "🌸", "Sakura storm"], ["fireworks", "🎆", "Fireworks"],
  ["lava", "🌋", "Eruption"], ["bubbles", "🫧", "Bubbles"], ["eclipse", "🌑", "Eclipse"], ["spores", "🍄", "Spores"], ["portal", "🌌", "Portal"],
  ["pixel", "👾", "Pixel art"], ["printer", "📠", "Printer"], ["banhammer", "🔨", "Banhammer"], ["money", "💸", "Money rain"], ["swarm", "🐝", "Swarm"],
  ["rainbow", "🌈", "Rainbow"], ["leaves", "🍂", "Leaf storm"], ["tomato", "🍅", "Tomatoes"], ["poop", "💩", "Poop barrage", .2]];
/** A random spell, weighted (the poop barrage is 5× rarer than the rest); `not` — a key to skip. */
function pickSpell(not) {
  const list = SPELLS.filter(x => x[0] !== not), total = list.reduce((a, x) => a + (x[3] ?? 1), 0);
  let r = Math.random() * total;
  for (const x of list) { r -= x[3] ?? 1; if (r <= 0) return x[0]; }
  return list[0][0];
}
/** Achievements: site activity — a visit (once a day), quick search, Member tabs (each once). Fire and forget. */
const siteTrack = (() => {
  const seen = new Set();
  const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; };
  return (what, tab) => {
    if (!state.guildId || !state.me) return;
    const key = `${state.guildId}:${what}:${tab || ""}:${today()}`;
    if (what !== "search" && seen.has(key)) return;
    seen.add(key);
    gApi("POST", "/me/track", { what, day: today(), tab: tab || null }).catch(() => { /* just stats */ });
  };
})();
/** Owner only: summon the robot now — it will surely cast the chosen spell when it escapes. */
function summonButtons(after) {
  const go = kind => { if (after) after(); if (kind === "golden" || kind === "rainbow" || kind === "both") window.__neonCat && window.__neonCat({ golden: kind !== "rainbow", rainbow: kind !== "golden" }); else window.__mascotFinale && window.__mascotFinale(kind); };
  return h("div", { class: "row summon-row", style: "gap:6px;flex-wrap:wrap" },
    [...SPELLS, ["legend", "🌟", "LEGENDARY (two spells at once)"], ["random", "🎲", "Random spell"], ["golden", "🏆", "GOLDEN robot (always casts, 6% legendary)"], ["rainbow", "🌈", "RAINBOW robot (legendary ×10)"], ["both", "💫", "GOLDEN RAINBOW robot (always casts, 60% legendary)"]].map(([k, e, n]) => h("button", { type: "button", class: "btn secondary sm", title: `Summon — casts: ${n}`, onclick: () => go(k) }, e)));
}
(function mascots() {
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  const px = (rows, col, y0 = 0) => rows.flatMap((r, y) => [...r].map((c, x) => col[c] ? `<rect x="${x}" y="${y + y0}" width="1.02" height="1.02" fill="${col[c]}"/>` : "")).join("");
  const BOT = {
    name: "robot", w: 84, h: 114, rotate: false,
    // Our bot from its icon, drawn in full: crown with a blue crystal, hooded face with glowing eyes (they blink),
    // gold pauldrons, a robe with a gold hem and a "G" emblem, a glowing orb in hand, little feet that step
    svg: "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 -4 120 163\" width=\"84\" height=\"114\" aria-hidden=\"true\"> <defs> <linearGradient id=\"gbCry\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#E6F4FF\"/><stop offset=\".5\" stop-color=\"#7CC4FF\"/><stop offset=\"1\" stop-color=\"#2F6FD6\"/></linearGradient> <linearGradient id=\"gbGold\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#FFE9A3\"/><stop offset=\".45\" stop-color=\"#E8B84A\"/><stop offset=\"1\" stop-color=\"#9C6A1E\"/></linearGradient> <radialGradient id=\"gbHood\" cx=\".35\" cy=\".25\" r=\".9\"><stop offset=\"0\" stop-color=\"#6A4BC4\"/><stop offset=\".45\" stop-color=\"#3A2478\"/><stop offset=\"1\" stop-color=\"#160C33\"/></radialGradient> <linearGradient id=\"gbRobe\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#4A2E92\"/><stop offset=\"1\" stop-color=\"#1E1240\"/></linearGradient> <radialGradient id=\"gbFace\" cx=\".5\" cy=\".4\" r=\".7\"><stop offset=\"0\" stop-color=\"#140A2A\"/><stop offset=\"1\" stop-color=\"#030108\"/></radialGradient> <radialGradient id=\"gbEye\" cx=\".5\" cy=\".4\" r=\".6\"><stop offset=\"0\" stop-color=\"#FFFFFF\"/><stop offset=\".45\" stop-color=\"#E2D6FF\"/><stop offset=\"1\" stop-color=\"#9B7BFF\"/></radialGradient> <radialGradient id=\"gbOrb\" cx=\".4\" cy=\".35\" r=\".7\"><stop offset=\"0\" stop-color=\"#FFFFFF\"/><stop offset=\".35\" stop-color=\"#D9C9FF\"/><stop offset=\".75\" stop-color=\"#9B6BFF\"/><stop offset=\"1\" stop-color=\"#6A3BD6\"/></radialGradient> <filter id=\"gbGlow\" x=\"-50%\" y=\"-50%\" width=\"200%\" height=\"200%\"><feGaussianBlur stdDeviation=\"2.2\" result=\"b\"/><feMerge><feMergeNode in=\"b\"/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter> </defs> <g class=\"gb-legs\"><g class=\"gb-leg l\"><rect x=\"42.5\" y=\"122\" width=\"9\" height=\"29\" rx=\"4\" fill=\"#2C1B5E\" stroke=\"#0E0720\" stroke-width=\"1.4\"/><path d=\"M43.5,146 L50.5,146\" stroke=\"url(#gbGold)\" stroke-width=\"2\" stroke-linecap=\"round\"/><ellipse cx=\"47\" cy=\"153\" rx=\"8.5\" ry=\"4.8\" fill=\"#2A1A55\" stroke=\"#0E0720\" stroke-width=\"1.5\"/><path d=\"M41,151.5 Q47,149 53,151.5\" stroke=\"#8C6CFF\" stroke-width=\"1.2\" fill=\"none\" opacity=\".8\"/></g><g class=\"gb-leg r\"><rect x=\"68.5\" y=\"122\" width=\"9\" height=\"29\" rx=\"4\" fill=\"#2C1B5E\" stroke=\"#0E0720\" stroke-width=\"1.4\"/><path d=\"M69.5,146 L76.5,146\" stroke=\"url(#gbGold)\" stroke-width=\"2\" stroke-linecap=\"round\"/><ellipse cx=\"73\" cy=\"153\" rx=\"8.5\" ry=\"4.8\" fill=\"#2A1A55\" stroke=\"#0E0720\" stroke-width=\"1.5\"/><path d=\"M67,151.5 Q73,149 79,151.5\" stroke=\"#8C6CFF\" stroke-width=\"1.2\" fill=\"none\" opacity=\".8\"/></g></g> <path d=\"M30,100 Q20,112 22,124\" fill=\"none\" stroke=\"#2C1B5E\" stroke-width=\"9\" stroke-linecap=\"round\"/> <circle cx=\"22.5\" cy=\"125\" r=\"5\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.2\"/> <path d=\"M34,96 L86,96 L97,136 Q60,148 23,136 Z\" fill=\"url(#gbRobe)\" stroke=\"#0E0720\" stroke-width=\"2\" stroke-linejoin=\"round\"/> <path d=\"M48,100 L44,138 M72,100 L76,138 M60,100 L60,142\" stroke=\"#5C3DB0\" stroke-width=\"1.2\" opacity=\".55\"/> <path d=\"M24,134 Q60,146 96,134\" fill=\"none\" stroke=\"url(#gbGold)\" stroke-width=\"3.2\" stroke-linecap=\"round\"/> <circle cx=\"60\" cy=\"114\" r=\"9\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.4\"/> <path d=\"M64.5,110.5 A5.2,5.2 0 1 0 64.8,117.6 L64.8,114.6 L61,114.6\" fill=\"none\" stroke=\"#5A3A0E\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/> <path d=\"M14,104 Q16,90 34,90 L42,96 Q36,108 22,110 Q14,110 14,104Z\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.5\"/> <path d=\"M106,104 Q104,90 86,90 L78,96 Q84,108 98,110 Q106,110 106,104Z\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.5\"/> <path d=\"M20,100 Q28,96 36,96\" stroke=\"#FFF2C4\" stroke-width=\"1.4\" fill=\"none\" opacity=\".7\"/> <path d=\"M100,100 Q92,96 84,96\" stroke=\"#FFF2C4\" stroke-width=\"1.4\" fill=\"none\" opacity=\".7\"/> <path d=\"M22,70 C22,45 40,34 60,34 C80,34 98,45 98,70 C98,86 92,95 84,99 L36,99 C28,95 22,86 22,70Z\" fill=\"url(#gbHood)\" stroke=\"#0E0720\" stroke-width=\"2\"/> <path d=\"M30,56 C34,44 46,39 58,38\" fill=\"none\" stroke=\"#8C6CFF\" stroke-width=\"1.6\" stroke-linecap=\"round\" opacity=\".7\"/> <ellipse cx=\"60\" cy=\"71\" rx=\"28\" ry=\"21\" fill=\"url(#gbFace)\"/> <ellipse cx=\"60\" cy=\"71\" rx=\"28\" ry=\"21\" fill=\"none\" stroke=\"#000\" stroke-opacity=\".6\" stroke-width=\"2\"/> <g class=\"gb-look\"><g class=\"gb-eyes\" filter=\"url(#gbGlow)\"> <ellipse cx=\"48\" cy=\"72\" rx=\"6.2\" ry=\"7.4\" fill=\"url(#gbEye)\"/> <ellipse cx=\"72\" cy=\"72\" rx=\"6.2\" ry=\"7.4\" fill=\"url(#gbEye)\"/> </g> <g class=\"gb-blink\"><ellipse cx=\"48\" cy=\"72\" rx=\"7\" ry=\"8\" fill=\"#08040F\"/><ellipse cx=\"72\" cy=\"72\" rx=\"7\" ry=\"8\" fill=\"#08040F\"/> <path d=\"M42,73 Q48,75 54,73 M66,73 Q72,75 78,73\" stroke=\"#B9A2FF\" stroke-width=\"1.6\" fill=\"none\" stroke-linecap=\"round\"/></g></g> <path d=\"M33,40 L30,20 L41,30 L48,14 L55,27 L60,22 L65,27 L72,14 L79,30 L90,20 L87,40 Z\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.6\" stroke-linejoin=\"round\"/> <rect x=\"32\" y=\"36\" width=\"56\" height=\"7\" rx=\"2\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.4\"/> <circle cx=\"44\" cy=\"39.5\" r=\"2\" fill=\"#B35CFF\"/><circle cx=\"76\" cy=\"39.5\" r=\"2\" fill=\"#B35CFF\"/><circle cx=\"60\" cy=\"39.5\" r=\"2.2\" fill=\"#4FB0FF\"/> <g filter=\"url(#gbGlow)\"> <polygon points=\"60,0 69,14 60,30 51,14\" fill=\"url(#gbCry)\" stroke=\"#1F4FA8\" stroke-width=\"1.2\"/> <polygon points=\"60,2 64,13 60,26\" fill=\"#FFFFFF\" opacity=\".55\"/> </g> <path d=\"M90,100 Q102,104 104,114\" fill=\"none\" stroke=\"#2C1B5E\" stroke-width=\"9\" stroke-linecap=\"round\"/> <circle cx=\"104\" cy=\"115\" r=\"5\" fill=\"url(#gbGold)\" stroke=\"#6E4A14\" stroke-width=\"1.2\"/> <circle class=\"gb-orb-glow\" cx=\"104\" cy=\"100\" r=\"14\" fill=\"#9B6BFF\" opacity=\".28\"/> <circle cx=\"104\" cy=\"100\" r=\"9\" fill=\"url(#gbOrb)\" stroke=\"#5A2FC0\" stroke-width=\"1\" filter=\"url(#gbGlow)\"/> <path d=\"M100,96 Q103,93 107,95\" stroke=\"#fff\" stroke-width=\"1.4\" fill=\"none\" opacity=\".8\" stroke-linecap=\"round\"/> <g class=\"gb-sparks\" fill=\"#E9E1FF\"><circle cx=\"116\" cy=\"88\" r=\"1.3\"/><circle cx=\"92\" cy=\"86\" r=\"1\"/><circle cx=\"114\" cy=\"110\" r=\"1\"/></g> </svg>",
    lines: ["Beep-boop! You caught the Greatness bot 🤖", "Okay, okay, you got me! 😅", "Fast hands — worthy of a raid leader ⚡", "The bot salutes you, officer 👑", "Your tickets will be approved faster now (not really) 😄", "System check: guild morale 100% 💜", "Bot says: see you in the next raid ⚔️"]
  };
  const area = () => {
    const n = document.querySelector(".layout:not(.drawer-open) .nav"), r = n && n.getBoundingClientRect();
    const top = ((document.querySelector(".topbar") || {}).getBoundingClientRect?.().bottom || 56) + 10;
    return { left: r && r.right > 0 && innerWidth > 900 ? r.right : 0, top, right: innerWidth - 12, bottom: innerHeight - 8 };
  };
  let busy = false, timer = 0;
  function fly(opts) {
    // The owner can summon it with a guaranteed spell (opts.finale: a spell or "random").
    let forced = opts && typeof opts === "object" ? opts.finale : null;
    const summonGolden = !!(opts && typeof opts === "object" && opts.golden), summonRainbow = !!(opts && typeof opts === "object" && opts.rainbow);
    if ((forced || summonGolden || summonRainbow) && busy) return toast("The robot is already flying — wait until it's gone.", "warn");
    if (busy || document.hidden || !state.me) return schedule();
    if (forced === "random") forced = pickSpell();
    // The GOLDEN robot: after 10 catches in a row (counted in this browser session) there's a 10% chance, doubling with every further
    // catch (20%, 40%, 80%, 100%); when it appears the count starts over. It always casts, legendary 4× as often (6%).
    // the catch streaks live in this browser session only (sessionStorage) — nothing goes to the server
    const sess = { get: k => { try { return sessionStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { sessionStorage.setItem(k, v); } catch { /* private mode */ } } };
    const streakKey = `gb.mascotStreak.${state.guildId}`;
    const streak = Number(sess.get(streakKey) || 0);
    const golden = summonGolden || (streak >= 10 && Math.random() < Math.min(1, .1 * 2 ** (streak - 10)));
    if (golden && !summonGolden) sess.set(streakKey, "0");
    // The RAINBOW robot: +1% for every catch in a row (its own count, starts over when it appears or one escapes).
    // It can come together with the golden one. Legendary casts ×10 (60% when golden too).
    const rbKey = `gb.mascotRainbow.${state.guildId}`, rbStreak = Number(sess.get(rbKey) || 0);
    // the plain catch streak shown to the player (doesn't reset when a special robot comes — only when one escapes)
    const runKey = `gb.mascotRun.${state.guildId}`;
    let run = 0;
    // +1% per catch as usual; in the golden zone (10+ catches in a row) +2% more for every catch from the 10th on
    const rainbow = summonRainbow || Math.random() < Math.min(1, rbStreak / 100 + Math.max(0, streak - 9) * .02);
    if (rainbow && !summonRainbow) sess.set(rbKey, "0");
    // Escape: 4% a spell, never legendary. Caught: 60% (1.5% legendary) — it strikes back as it vanishes.
    const rollSpell = caught => {
      const r = Math.random();
      const boost = rainbow ? 10 : 1;   // rainbow: legendary ×10 (golden rainbow 60%)
      if (golden) return r < .06 * boost ? "legend" : pickSpell();
      return caught ? (r < .015 * boost ? "legend" : r < .6 ? pickSpell() : null) : (r < .04 ? pickSpell() : null);
    };
    const finale = forced || rollSpell(false);
    const M = BOT; // only our robot flies (the cat is retired)
    const A = area(), W = M.w, H = M.h;
    // Starts just outside the page area: mostly left↔right, sometimes up↕down. While it flies, clicks right around it
    // of it are ignored (so you never hit a button by accident while trying to catch it).
    let pos, dir;
    if (Math.random() < .7) {
      const y = A.top + Math.random() * Math.max(10, A.bottom - A.top - H), ltr = Math.random() < .5;
      pos = [ltr ? A.left - W : A.right, y]; dir = [ltr ? 1 : -1, 0];
    } else {
      const x = A.left + 10 + Math.random() * Math.max(10, A.right - A.left - W - 20), up = Math.random() < .5;
      pos = [x, up ? A.bottom : A.top - H]; dir = [0, up ? -1 : 1];
    }
    busy = true;
    const span = dir[0] ? A.right - A.left + W : A.bottom - A.top + H;
    const base = span / Math.max(4500, span * 7.5);          // px per ms — same pace as before
    const el = h("div", { class: `mascot mascot-${M.name}${golden ? " golden" : ""}${rainbow ? " rainbow" : ""}`,
      title: golden && rainbow ? "A GOLDEN RAINBOW robot!" : golden ? "A GOLDEN robot — catch it!" : rainbow ? "A RAINBOW robot — catch it!" : "Catch me!" });
    if (golden && rainbow) toast("💫 A GOLDEN RAINBOW robot appeared! Always casts — 60% legendary!");
    else if (golden) toast("✨ A GOLDEN robot appeared! It surely casts something…");
    else if (rainbow) toast("🌈 A RAINBOW robot appeared! Legendary casts ×10");
    el.innerHTML = `<div class="mascot-dodge"><div class="mascot-bob">${M.svg}</div></div>`;
    el.style.opacity = "0";
    document.body.append(el);
    const dodge = el.firstChild, look = el.querySelector(".gb-look"), box = () => el.getBoundingClientRect();
    const place = () => { el.style.transform = `translate(${pos[0].toFixed(1)}px, ${pos[1].toFixed(1)}px)`; };
    place();

    // Behaviour: eyes follow the cursor. When the cursor comes close the robot turns and runs the other way
    // (double speed for 1 s, legs go faster). If it's cornered and can't run away, it dashes past the cursor.
    // After 5 runs/dashes it gets tired: slows down and can be caught.
    const NEAR = 55, MAX_ACTS = 5, inside = () => ({ x0: A.left + 4, y0: A.top + 4, x1: A.right - W - 4, y1: A.bottom - H - 4 });
    let ptr = null, raf = 0, last = performance.now(), born = last, entered = false, acts = 0, tired = false;
    let runUntil = 0, dash = null, done = false, casting = false, caughtIt = false, castKinds = [], castLegend = false, dodges = 0, dashes = 0, enteredAt = 0;
    const onMove = e => { ptr = [e.clientX, e.clientY]; };
    const norm = (x, y) => { const l = Math.hypot(x, y) || 1; return [x / l, y / l]; };
    const tire = () => { if (++acts >= MAX_ACTS) { tired = true; el.classList.add("tired"); el.classList.remove("run"); el.title = "It's tired — catch it!"; } };
    const room = (d, b) => {   // free distance along d before the robot hits the edge
      const tx = d[0] > 0 ? (b.x1 - pos[0]) / d[0] : d[0] < 0 ? (b.x0 - pos[0]) / d[0] : Infinity;
      const ty = d[1] > 0 ? (b.y1 - pos[1]) / d[1] : d[1] < 0 ? (b.y0 - pos[1]) / d[1] : Infinity;
      return Math.max(0, Math.min(tx, ty));
    };
    const tick = now => {
      raf = requestAnimationFrame(tick);
      const dt = Math.min(50, now - last); last = now;
      if (done || casting) return;
      const b = inside(), cx = pos[0] + W / 2, cy = pos[1] + H / 2;
      if (dash) {   // quick hop to a new spot
        const t = Math.min(1, (now - dash.t0) / 280), k = 1 - Math.pow(1 - t, 3);
        pos = [dash.from[0] + (dash.to[0] - dash.from[0]) * k, dash.from[1] + (dash.to[1] - dash.from[1]) * k];
        place(); if (t >= 1) dash = null;
        return;
      }
      if (ptr) {
        // eyes look toward the cursor
        const ey = pos[1] + H * .46, dx = ptr[0] - cx, dy = ptr[1] - ey, L = Math.hypot(dx, dy) || 1, m = Math.min(1, L / 90);
        if (look) look.style.transform = `translate(${(dx / L * 5 * m).toFixed(1)}px, ${(dy / L * 3.5 * m).toFixed(1)}px)`;
        const edge = Math.hypot(Math.max(pos[0] - ptr[0], 0, ptr[0] - pos[0] - W), Math.max(pos[1] - ptr[1], 0, ptr[1] - pos[1] - H));
        if (entered && !tired && edge < NEAR) {
          const away = norm(cx - ptr[0], cy - ptr[1]);
          if (room(away, b) < 40) {
            // cornered: dash past the cursor to the other side
            const through = norm(ptr[0] - cx, ptr[1] - cy), side = Math.random() < .5 ? 1 : -1, perp = [-through[1] * side, through[0] * side];
            let to = [ptr[0] - W / 2 + through[0] * 150 + perp[0] * 50, ptr[1] - H / 2 + through[1] * 150 + perp[1] * 50];
            to = [Math.max(b.x0, Math.min(b.x1, to[0])), Math.max(b.y0, Math.min(b.y1, to[1]))];
            dash = { from: pos.slice(), to, t0: now }; dir = norm(to[0] - pos[0], to[1] - pos[1]);
            dodge.classList.remove("hop"); void dodge.offsetWidth; dodge.classList.add("hop");
            runUntil = 0; el.classList.remove("run"); dashes++; tire();
            return;
          }
          // just turn and run the other way
          if (now >= runUntil) { dodges++; tire(); if (!tired) { runUntil = now + 1000; el.classList.add("run"); } }
          dir = away;
        }
      }
      const running = now < runUntil;
      if (!running) el.classList.remove("run");
      const v = base * (running ? 2 : tired ? .6 : 1) * dt;
      pos = [pos[0] + dir[0] * v, pos[1] + dir[1] * v];
      if (running) pos = [Math.max(b.x0, Math.min(b.x1, pos[0])), Math.max(b.y0, Math.min(b.y1, pos[1]))];   // no escaping while fleeing
      place();
      // fade with how much of it is on the page; done once it has entered and then left
      const ix = Math.max(0, Math.min(pos[0] + W, A.right) - Math.max(pos[0], A.left)), iy = Math.max(0, Math.min(pos[1] + H, A.bottom) - Math.max(pos[1], A.top));
      const frac = ix * iy / (W * H);
      el.style.opacity = frac.toFixed(2);
      if (frac > .98 && !entered) { entered = true; enteredAt = now; }
      // a successful escape: instead of just slipping off the screen it stops at the edge and casts
      if (finale && entered && !running && frac < .98) {
        pos = [Math.max(b.x0, Math.min(b.x1, pos[0])), Math.max(b.y0, Math.min(b.y1, pos[1]))];
        place(); el.style.opacity = "1";
        return cast(finale);
      }
      if ((entered && frac <= 0) || now - born > 120000) end();
    };
    document.addEventListener("pointermove", onMove, { passive: true });
    raf = requestAnimationFrame(tick);
    const stopWatch = () => { done = true; cancelAnimationFrame(raf); document.removeEventListener("pointermove", onMove); };
    const trail = setInterval(() => {
      if (done || +el.style.opacity < .2) return;
      const r = box(), sx = r.left + r.width / 2 - dir[0] * 36, sy = r.top + r.height / 2 - dir[1] * 44;
      const s = h("i", { class: `cat-spark${rainbow ? ` ${["fire", "gold", "acid", "water", "volt", "petal"][Math.floor(Math.random() * 6)]}` : golden ? " gold" : Math.random() < .4 ? " blue" : ""}`, style: `left:${sx + (Math.random() * 10 - 5)}px;top:${sy + (Math.random() * 10 - 5)}px` });
      document.body.append(s); setTimeout(() => s.remove(), 800);
    }, 55);
    // Click guard: the same zone the robot reacts to (NEAR px around it) — there buttons, links and fields don't react,
    // only the mascot does; everywhere else the site works as usual
    const guard = e => {
      if (!el.isConnected || (e.target.closest && e.target.closest(".mascot"))) return;
      const r = box();
      if (e.clientX == null || Math.hypot(Math.max(r.left - e.clientX, 0, e.clientX - r.right), Math.max(r.top - e.clientY, 0, e.clientY - r.bottom)) > NEAR) return;
      e.preventDefault(); e.stopImmediatePropagation();
    };
    const EVENTS = ["pointerdown", "mousedown", "pointerup", "mouseup", "click", "dblclick", "contextmenu"];
    EVENTS.forEach(t => document.addEventListener(t, guard, true));
    let ended = false;
    // Achievements: the member caught it / saw it escape (with the spells it cast) — counted on the server
    // every report also carries what happened in this flight: dodges, dashes, worn out, my local hour and day
    const report = body => {
      if (!state.guildId) return Promise.resolve(null);
      const d = new Date(), day = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
      const chase = enteredAt ? performance.now() - enteredAt : 0;
      return gApi("POST", "/me/mascot", { ...body, dodges, dashes, tired, hour: d.getHours(), day,
        fast: body.kind === "caught" && enteredAt > 0 && chase < 3000, long: body.kind === "caught" && chase > 10000 }).catch(() => null);
    };
    function end() {
      if (ended) return; ended = true;
      if (!caughtIt && entered) { report({ kind: "escaped", spells: castKinds, legendary: castLegend, golden, rainbow }); sess.set(streakKey, "0"); sess.set(rbKey, "0"); sess.set(runKey, "0"); }
      stopWatch(); clearInterval(trail); EVENTS.forEach(t => document.removeEventListener(t, guard, true)); el.remove(); busy = false; schedule();
    }
    // ---- The finale spells (pure DOM/CSS, a few seconds, never block clicks) ----
    const fxEl = (cls, style) => { const d = h("div", { class: `gb-fx ${cls}`, style: style || "" }); document.body.append(d); return d; };
    const burst = (x, y, n, dist, cls, dur) => {
      for (let i = 0; i < n; i++) {
        const a = Math.random() * Math.PI * 2, d = dist * (.5 + Math.random() * .7);
        const s = h("i", { class: `cat-spark big ${cls}`, style: `left:${x}px;top:${y}px` });
        document.body.append(s);
        s.animate([{ transform: "translate(0,0) scale(1.4)", opacity: 1 }, { transform: `translate(${Math.cos(a) * d}px, ${Math.sin(a) * d}px) scale(.2)`, opacity: 0 }],
          { duration: dur * (.7 + Math.random() * .5), easing: "cubic-bezier(.1,.7,.3,1)" });
        setTimeout(() => s.remove(), dur * 1.3);
      }
    };
    // Cracked glass where the spell hit: splinters out in a blink, then heals back to the centre over ~3 s.
    // Each spell leaves its own kind: fire — scorched, glowing cracks that cool down; black hole — a big broken-out
    // hole with fewer cracks; lightning — a small hole but lots of crackling, electrified cracks.
    const CRACK = {
      hole: { n: [9, 12], pit: .26, rings: [.4, .58, .78], branch: .1, layers: ["sh", "hi"] },
      fire: { n: [11, 15], pit: .11, rings: [.2, .36, .56, .76], branch: .12, layers: ["gl", "sh", "hi"] },
      bolt: { n: [20, 26], pit: .05, rings: [.13, .24, .37, .52, .68, .85], branch: .3, layers: ["gl", "sh", "hi"] },
      rock: { n: [7, 10], pit: .3, rings: [.55, .8], branch: .18, layers: ["sh", "hi"], w: 1.7 },
      meteor: { n: [12, 16], pit: .2, rings: [.3, .5, .72], branch: .14, layers: ["gl", "sh", "hi"], w: 1.6 },
      acid: { n: [4, 7], pit: .36, rings: [.62], branch: .1, layers: ["gl", "sh", "hi"], w: 1.3 }
    };
    function cracks(x, y, R, kind) {
      const cfg = CRACK[kind] || CRACK.fire;
      const NS = "http://www.w3.org/2000/svg", S = Math.ceil(R * 2 + 40), c = S / 2;
      const svg = document.createElementNS(NS, "svg");
      svg.setAttribute("class", `gb-fx gb-cracks ${kind}`); svg.setAttribute("width", S); svg.setAttribute("height", S);
      svg.setAttribute("viewBox", `0 0 ${S} ${S}`); svg.style.left = `${x - c}px`; svg.style.top = `${y - c}px`;
      const mk = (tag, attrs) => { const n = document.createElementNS(NS, tag); for (const k in attrs) n.setAttribute(k, attrs[k]); return n; };
      const lines = [], rings = [];
      const add = (pts, list, w) => {
        let L = 0; for (let i = 1; i < pts.length; i++) L += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
        const d = "M" + pts.map(p => `${p[0].toFixed(1)},${p[1].toFixed(1)}`).join("L");
        for (const cls of cfg.layers) {
          const ww = w * (cfg.w || 1);
          const path = mk("path", { d, class: cls, "stroke-width": cls === "gl" ? ww + 5 : cls === "sh" ? ww + (kind === "fire" ? 3 : 1.4) : ww });
          if (cls === "sh") path.setAttribute("transform", "translate(.8 .8)");
          path.style.strokeDasharray = `${L} ${L}`;
          svg.append(path); list.push([path, L]);
        }
      };
      // the broken-out hole in the middle
      const pitR = R * cfg.pit, m = 16 + Math.floor(Math.random() * 7), pitPts = [];
      for (let i = 0; i < m; i++) { const a = i / m * Math.PI * 2, r = pitR * (.7 + Math.random() * .5); pitPts.push([c + Math.cos(a) * r, c + Math.sin(a) * r]); }
      const pit = mk("path", { d: "M" + pitPts.map(p => p.map(v => v.toFixed(1)).join(",")).join("L") + "Z", class: "pit" });
      // rays
      const [n0, n1] = cfg.n, n = n0 + Math.floor(Math.random() * (n1 - n0 + 1)), base = Math.random() * Math.PI * 2, rays = [];
      for (let i = 0; i < n; i++) {
        let a = base + i / n * Math.PI * 2 + (Math.random() - .5) * .35, r = pitR * .9 + 2;
        const len = R * (.5 + Math.random() * .5), pts = [[c + Math.cos(a) * r, c + Math.sin(a) * r]];
        while (r < len) {
          r += R * (.07 + Math.random() * .08); a += (Math.random() - .5) * (kind === "bolt" ? .3 : .16);
          pts.push([c + Math.cos(a) * r, c + Math.sin(a) * r]);
          if (Math.random() < cfg.branch && r < len * .85) {   // a small branch
            const b = a + (Math.random() < .5 ? 1 : -1) * (.35 + Math.random() * .3), bl = R * (.1 + Math.random() * .15), p0 = pts[pts.length - 1];
            add([p0, [p0[0] + Math.cos(b) * bl * .5, p0[1] + Math.sin(b) * bl * .5], [p0[0] + Math.cos(b + .1) * bl, p0[1] + Math.sin(b + .1) * bl]], lines, .9);
          }
        }
        add(pts, lines, 1.3 - i % 3 * .2); rays.push(pts);
      }
      // the "spider web" rings between neighbouring rays
      for (const f of cfg.rings) rays.forEach((ra, i) => {
        const rb = rays[(i + 1) % rays.length], pick = pts => pts[Math.min(pts.length - 1, Math.round(f * (pts.length - 1) / .9))];
        if (Math.random() < (f > .6 ? .45 : .8) && ra.length > 2 && rb.length > 2) {
          const p = pick(ra), q = pick(rb), mx = (p[0] + q[0]) / 2, my = (p[1] + q[1]) / 2, k = .9 + Math.random() * .06;
          add([p, [c + (mx - c) * k, c + (my - c) * k], q], rings, .8);
        }
      });
      svg.append(pit);
      document.body.append(svg);
      const HOLD = 350, HEAL = 2650, ALL = HOLD + HEAL + 140, keep = (HOLD + 140) / ALL;
      [...lines, ...rings].forEach(([p, L]) => p.animate(
        [{ strokeDashoffset: L }, { strokeDashoffset: 0, offset: .05 }, { strokeDashoffset: 0, offset: keep }, { strokeDashoffset: L }],
        { duration: ALL, easing: "ease-in-out", fill: "forwards" }));
      rings.forEach(([p]) => p.animate([{ opacity: 1 }, { opacity: 1, offset: .2 }, { opacity: 0, offset: .6 }, { opacity: 0 }], { duration: ALL, fill: "forwards" }));
      pit.animate([{ transform: "scale(.2)" }, { transform: "scale(1)", offset: .05 }, { transform: "scale(1)", offset: keep }, { transform: "scale(0)" }], { duration: ALL, easing: "ease-in-out", fill: "forwards" });
      svg.animate([{ opacity: 1 }, { opacity: 1, offset: .85 }, { opacity: 0 }], { duration: ALL, fill: "forwards" });
      let zap = 0;
      if (kind === "bolt") zap = setInterval(() => {   // little electric arcs jumping along the cracks
        const ray = rays[Math.floor(Math.random() * rays.length)], i = 1 + Math.floor(Math.random() * Math.max(1, ray.length - 2)), p = ray[Math.min(i, ray.length - 1)];
        const a = Math.random() * Math.PI * 2, pts = [p];
        for (let k = 0; k < 4; k++) { const q = pts[pts.length - 1]; pts.push([q[0] + Math.cos(a + (k % 2 ? .9 : -.9)) * 7, q[1] + Math.sin(a + (k % 2 ? .9 : -.9)) * 7]); }
        const arc = mk("path", { d: "M" + pts.map(q => q.map(v => v.toFixed(1)).join(",")).join("L"), class: "arc" });
        svg.append(arc); setTimeout(() => arc.remove(), 140);
      }, 70);
      setTimeout(() => { clearInterval(zap); svg.remove(); }, ALL + 200);
    }
    // Water drops on the "glass": they magnify a little, hang, then run down leaving a trail
    function waterDrops(n, at, variant) {
      for (let i = 0; i < n; i++) {
        const [x, y] = at(i), sz = 9 + Math.random() * 18, wait = 200 + Math.random() * 1400, fall = 50 + Math.random() * 170;
        const d = h("div", { class: `gb-fx gb-drop${variant ? ` ${variant}` : ""}`, style: `left:${x}px;top:${y}px;width:${sz}px;height:${sz * 1.12}px` }, h("i", { class: "trail" }));
        document.body.append(d);
        d.animate([{ transform: "translate(-50%,-50%) scale(.2)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: .05 },
          { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: (wait + 200) / (wait + 2600) },
          { transform: `translate(-50%, calc(-50% + ${fall}px)) scale(.85, 1.05)`, opacity: .9, offset: .9 },
          { transform: `translate(-50%, calc(-50% + ${fall + 10}px)) scale(.7)`, opacity: 0 }], { duration: wait + 2600, easing: "ease-in", fill: "forwards" });
        d.firstChild.animate([{ height: "0px" }, { height: "0px", offset: (wait + 200) / (wait + 2600) }, { height: `${fall}px` }], { duration: wait + 2600, easing: "ease-in", fill: "forwards" });
        setTimeout(() => d.remove(), wait + 2700);
      }
    }
    // Smoke puffs rising and spreading
    function smoke(x, y, n, spread) {
      for (let i = 0; i < n; i++) {
        const s = h("div", { class: "gb-fx gb-smoke", style: `left:${x + (Math.random() - .5) * spread}px;top:${y + (Math.random() - .5) * spread * .5}px;width:${40 + Math.random() * 50}px;height:${40 + Math.random() * 50}px` });
        document.body.append(s);
        const dur = 1800 + Math.random() * 1400;
        s.animate([{ transform: "translate(-50%,-50%) scale(.4)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1)", opacity: .75, offset: .15 },
          { transform: `translate(calc(-50% + ${(Math.random() - .5) * 60}px), calc(-50% - ${60 + Math.random() * 90}px)) scale(2.4)`, opacity: 0 }], { duration: dur, easing: "ease-out", fill: "forwards" });
        setTimeout(() => s.remove(), dur + 50);
      }
    }
    // Frost ferns growing from the page edges inward (ice storm); they melt into drops
    function frost(A) {
      const NS = "http://www.w3.org/2000/svg", svg = document.createElementNS(NS, "svg");
      svg.setAttribute("class", "gb-fx gb-frost"); svg.setAttribute("width", innerWidth); svg.setAttribute("height", innerHeight);
      const paths = [];
      const fern = (p, ang, len, depth) => {
        const pts = [p]; let a = ang, q = p;
        const steps = 5 + Math.floor(len / 30);
        for (let i = 0; i < steps; i++) {
          a += (Math.random() - .5) * .25;
          q = [q[0] + Math.cos(a) * len / steps, q[1] + Math.sin(a) * len / steps]; pts.push(q);
          if (depth > 0 && i > 0 && i % 2 === 0) for (const side of [-1, 1])
            if (Math.random() < .8) fern(q, a + side * (.6 + Math.random() * .4), len * (.28 + Math.random() * .2) * (1 - i / steps), depth - 1);
        }
        paths.push([pts, depth]);
      };
      const W = A.right - A.left, Hh = A.bottom - A.top, n = 44;
      for (let i = 0; i < n; i++) {
        const side = i % 4, t = Math.random();
        const [p, ang] = side === 0 ? [[A.left + t * W, A.top], Math.PI / 2] : side === 1 ? [[A.left + t * W, A.bottom], -Math.PI / 2]
          : side === 2 ? [[A.left, A.top + t * Hh], 0] : [[A.right, A.top + t * Hh], Math.PI];
        fern(p, ang + (Math.random() - .5) * .9, 90 + Math.random() * Math.min(W, Hh) * .32, 2);
      }
      for (const [pts, depth] of paths) {
        let L = 0; for (let i = 1; i < pts.length; i++) L += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
        const d = "M" + pts.map(q => q.map(v => v.toFixed(1)).join(",")).join("L");
        for (const cls of ["gl", "hi"]) {
          const pa = document.createElementNS(NS, "path"); pa.setAttribute("d", d); pa.setAttribute("class", cls);
          pa.setAttribute("stroke-width", (cls === "gl" ? 5 : 1.2) + depth * .6); pa.style.strokeDasharray = `${L} ${L}`; svg.append(pa);
          pa.animate([{ strokeDashoffset: L }, { strokeDashoffset: 0 }], { duration: 600 + Math.random() * 700, delay: (2 - depth) * 180, easing: "ease-out", fill: "both" });
        }
      }
      document.body.append(svg);
      svg.animate([{ opacity: 1 }, { opacity: 1, offset: .35 }, { opacity: 0 }], { duration: 4300, easing: "ease-in", fill: "forwards" });
      setTimeout(() => svg.remove(), 4400);
    }
    // Dirt splat on the "glass" (tornado): an irregular blob with drips and specks that slowly slides down and fades
    function mud(x, y, size, variant) {
      const blob = h("div", { class: `gb-fx gb-mud${variant ? ` ${variant}` : ""}`, style: `left:${x}px;top:${y}px;width:${size}px;height:${size * (.75 + Math.random() * .4)}px;` +
        `border-radius:${[0, 0, 0, 0].map(() => 22 + Math.floor(Math.random() * 56)).join("% ")}% / ${[0, 0, 0, 0].map(() => 22 + Math.floor(Math.random() * 56)).join("% ")}%;` +
        "transform:translate(-50%,-50%)" });
      for (let i = 0; i < 2 + Math.floor(Math.random() * 3); i++) {   // lobes: a splat, not a ball
        const a = Math.random() * Math.PI * 2, r = size * (.3 + Math.random() * .25), ls = size * (.3 + Math.random() * .3);
        blob.append(h("u", { style: `left:calc(50% + ${Math.cos(a) * r - ls / 2}px);top:calc(50% + ${Math.sin(a) * r - ls / 2}px);width:${ls}px;height:${ls * (.6 + Math.random() * .5)}px` }));
      }
      for (let i = 0; i < 3 + Math.floor(Math.random() * 4); i++)   // drips
        blob.append(h("i", { class: "drip", style: `left:${20 + Math.random() * 60}%;width:${3 + Math.random() * 4}px;--len:${size * (.4 + Math.random() * .9)}px` }));
      for (let i = 0; i < 7 + Math.floor(Math.random() * 7); i++) {   // specks around
        const a = Math.random() * Math.PI * 2, r = size * (.6 + Math.random() * .7);
        blob.append(h("b", { style: `left:calc(50% + ${Math.cos(a) * r}px);top:calc(50% + ${Math.sin(a) * r}px);width:${3 + Math.random() * 8}px;height:${3 + Math.random() * 8}px` }));
      }
      document.body.append(blob);
      blob.animate([{ opacity: 0, scale: .3 }, { opacity: 1, scale: 1.1, offset: .03 }, { opacity: 1, scale: 1, offset: .06 }, { opacity: 1, scale: 1, offset: .4 }, { opacity: 0, scale: 1, translate: "0 24px" }],
        { duration: 5000, easing: "ease-in", fill: "forwards" });
      setTimeout(() => blob.remove(), 5100);
    }
    function cast(kind) {
      casting = true; clearInterval(trail);
      const legendary = kind === "legend";
      let kinds = [kind];
      if (legendary) { const a = pickSpell(); kinds = [a, pickSpell(a)]; }
      castKinds = kinds; castLegend = legendary;
      el.classList.remove("run"); el.classList.add("cast", ...kinds.map(k => `cast-${k}`)); if (legendary) el.classList.add("cast-legend");
      el.style.pointerEvents = "none"; el.title = "";
      if (look) look.style.transform = "translate(0px, -2px)";
      const tx = (A.left + A.right) / 2, ty = (A.top + A.bottom) / 2, D = Math.min(Math.max(A.right - A.left, A.bottom - A.top) * .8, 1100);
      const ox = pos[0] + W * 104 / 120, oy = pos[1] + H * 104 / 163;   // the orb in the robot's hand
      const later = (ms, fn) => setTimeout(() => { if (!ended) fn(); }, ms);
      const shared = [];
      let pending = kinds.length, vanished = false;
      // the robot disappears once, whichever spell gets there first
      const blinkOut = (ring, spark) => {
        if (vanished) return; vanished = true;
        const r = box(), px = r.left + r.width / 2, py = r.top + r.height / 2;
        el.animate([{ transform: el.style.transform, opacity: 1 }, { transform: `${el.style.transform} scale(1.15, .8)`, opacity: 1, offset: .3 }, { transform: `${el.style.transform} scale(0, 1.6)`, opacity: 0 }],
          { duration: 380, easing: "ease-in", fill: "forwards" });
        later(300, () => { const rg = fxEl(`gb-ring ${ring}`, `left:${px}px;top:${py}px`); shared.push(rg);
          rg.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(2.2)", opacity: 0 }], { duration: 500, easing: "ease-out", fill: "forwards" });
          burst(px, py, 12, 60, spark, 600); });
      };
      const vanishFlash = () => { if (vanished) return; vanished = true; el.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, fill: "forwards" }); };
      const name = k => { const x = SPELLS.find(y => y[0] === k); return x ? `${x[1]} ${x[2]}` : k; };
      const finish = msg => {
        if (--pending > 0) return;
        shared.forEach(n => n.remove());
        toast(caughtIt ? `🎯 Caught! …but it still cast ${kinds.map(name).join(" + ")}${legendary ? " — 🌟 LEGENDARY!" : ""}`
          : legendary ? `🌟 LEGENDARY CAST! ${kinds.map(name).join(" + ")} — the Greatness bot escaped!` : msg);
        end();
      };
      if (legendary) {   // golden aura around the whole screen
        const aura = fxEl("gb-legend");
        aura.animate([{ opacity: 0 }, { opacity: 1, offset: .15 }, { opacity: .6, offset: .5 }, { opacity: 1, offset: .7 }, { opacity: 0 }], { duration: 3800, fill: "forwards" });
        setTimeout(() => aura.remove(), 3900);
      }
      // a jagged lightning bolt from a to b (with a few side branches), flickering for `dur` ms
      const zig = (a, b, depth, k) => {
        if (!depth) return [a, b];
        const L = Math.hypot(b[0] - a[0], b[1] - a[1]) || 1, off = (Math.random() - .5) * L * k;
        const mid = [(a[0] + b[0]) / 2 - (b[1] - a[1]) / L * off, (a[1] + b[1]) / 2 + (b[0] - a[0]) / L * off];
        return zig(a, mid, depth - 1, k).concat(zig(mid, b, depth - 1, k).slice(1));
      };
      const strike = (a, b, dur, w) => {
        const NS = "http://www.w3.org/2000/svg", svg = document.createElementNS(NS, "svg");
        svg.setAttribute("class", "gb-fx gb-strike"); svg.setAttribute("width", innerWidth); svg.setAttribute("height", innerHeight);
        const main = zig(a, b, 7, .32), paths = [[main, 1]];
        const L = Math.hypot(b[0] - a[0], b[1] - a[1]), ang = Math.atan2(b[1] - a[1], b[0] - a[0]);
        for (let i = 0; i < 3 + Math.floor(w * 2); i++) {
          const p = main[Math.floor(main.length * (.2 + Math.random() * .6))], t = ang + (Math.random() < .5 ? 1 : -1) * (.4 + Math.random() * .5), bl = L * (.12 + Math.random() * .2);
          paths.push([zig(p, [p[0] + Math.cos(t) * bl, p[1] + Math.sin(t) * bl], 4, .4), .5]);
        }
        for (const [pts, k] of paths) {
          const d = "M" + pts.map(q => q.map(v => v.toFixed(1)).join(",")).join("L");
          for (const [cls, sw] of [["glow", 16], ["mid", 6], ["core", 2.2]]) {
            const p = document.createElementNS(NS, "path"); p.setAttribute("d", d); p.setAttribute("class", cls); p.setAttribute("stroke-width", sw * w * k); svg.append(p);
          }
        }
        document.body.append(svg);
        svg.animate([{ opacity: 0 }, { opacity: 1, offset: .08 }, { opacity: .15, offset: .22 }, { opacity: 1, offset: .32 }, { opacity: .45, offset: .5 }, { opacity: 1, offset: .62 }, { opacity: 0 }], { duration: dur, fill: "forwards" });
        setTimeout(() => svg.remove(), dur + 50);
      };
      const run = kind => {
      const charge = fxEl(`gb-charge ${kind}`, `left:${ox}px;top:${oy}px`);
      charge.animate([{ transform: "translate(-50%,-50%) scale(.2)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1.6)", opacity: 1 }], { duration: 900, easing: "ease-in", fill: "forwards" });
      const clean = [charge];
      if (kind === "rock") {
        // Stone barrage: a volley of rocks from the orb; every hit punches a small hole with little cracks
        const N = 11 + Math.floor(Math.random() * 4);
        later(700, () => charge.remove());
        for (let i = 0; i < N; i++) later(700 + i * 110, () => {
          const a = Math.random() * Math.PI * 2, r = Math.sqrt(Math.random());
          const x = tx + Math.cos(a) * r * D * .5, y = ty + Math.sin(a) * r * D * .34, sz = 36 + Math.random() * 26;
          const rock = fxEl(`gb-rock s${i % 4}`, `left:${ox}px;top:${oy}px;width:${sz}px;height:${sz}px`);
          const dur = 380 + Math.random() * 160, mx = (ox + x) / 2, my = Math.min(oy, y) - 70 - Math.random() * 60, spin = (Math.random() < .5 ? -1 : 1) * (360 + Math.random() * 360);
          rock.animate([
            { transform: "translate(-50%,-50%) scale(.5) rotate(0deg)" },
            { transform: `translate(${mx - ox}px, ${my - oy}px) translate(-50%,-50%) scale(.9) rotate(${spin / 2}deg)`, offset: .5 },
            { transform: `translate(${x - ox}px, ${y - oy}px) translate(-50%,-50%) scale(1.25) rotate(${spin}deg)` }
          ], { duration: dur, easing: "cubic-bezier(.45,0,.9,.6)", fill: "forwards" });
          setTimeout(() => {
            rock.remove();
            if (ended) return;
            burst(x, y, 14, 60, "dust", 600);
            const puff = fxEl("gb-ring rock", `left:${x}px;top:${y}px`);
            puff.animate([{ transform: "translate(-50%,-50%) scale(.2)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(2.6)", opacity: 0 }], { duration: 450, easing: "ease-out", fill: "forwards" });
            setTimeout(() => puff.remove(), 480);
            cracks(x, y, 85 + Math.random() * 55, "rock");
          }, dur);
        });
        later(700 + N * 110 + 600, () => blinkOut("rock", "dust"));
        later(700 + N * 110 + 1300, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped under a hail of stones 🪨"); });
      } else if (kind === "air") {
        // Tornado: a bluish twister grows in the middle of the page, sways, flings dirt on the "glass" and dissolves
        later(700, () => {
          charge.remove();
          const Ht = Math.min(D * .95, (A.bottom - A.top) * .88), Wt = Math.min(D * .55, 460), RINGS = 28;
          const tint = fxEl("gb-dim", `background: radial-gradient(ellipse at ${tx}px ${ty}px, rgba(120,190,255,.38) 0, rgba(60,120,200,.24) ${D * .35}px, rgba(20,50,110,.12) ${D * .7}px, rgba(20,50,110,0) ${D}px)`);
          tint.animate([{ opacity: 0 }, { opacity: 1, offset: .2 }, { opacity: 1, offset: .8 }, { opacity: 0 }], { duration: 2600, fill: "forwards" });
          const tw = fxEl("gb-tornado", `left:${tx}px;top:${ty + Ht / 2}px;width:${Wt}px;height:${Ht}px`);
          tw.append(h("div", { class: "core" }), h("div", { class: "core inner" }));   // the dense funnel body
          for (let i = 0; i < RINGS; i++) {
            const k = i / (RINGS - 1), w = Wt * (1 - k * .86), hh = Math.max(8, w * .2);
            tw.append(h("i", { style: `width:${w}px;height:${hh}px;top:${k * (Ht - hh)}px;animation-delay:${(-i * .05).toFixed(2)}s;opacity:${(1 - k * .25).toFixed(2)}` }));
          }
          for (let i = 0; i < 70; i++) {   // debris whirling around the funnel
            const k = Math.random(), w = Wt * (1 - k * .86) / 2 + 6, dur = 500 + Math.random() * 400;
            const d = h("b", { class: Math.random() < .55 ? "mud" : "", style: `top:${k * Ht}px` });
            tw.append(d);
            d.animate([{ transform: `translateX(${-w}px) scale(.6)`, opacity: .4 }, { transform: "translateX(0) scale(1.2)", opacity: 1, offset: .25 },
              { transform: `translateX(${w}px) scale(.6)`, opacity: .4, offset: .5 }, { transform: "translateX(0) scale(.4)", opacity: .15, offset: .75 },
              { transform: `translateX(${-w}px) scale(.6)`, opacity: .4 }], { duration: dur, iterations: Infinity, delay: -Math.random() * dur });
          }
          const drift = (Math.random() < .5 ? -1 : 1) * (40 + Math.random() * 50);
          tw.animate([
            { transform: "translate(-50%,-100%) scale(.2, 0)", opacity: 0 },
            { transform: "translate(-50%,-100%) scale(1, 1)", opacity: 1, offset: .2 },
            { transform: `translate(calc(-50% + ${drift}px),-100%) scale(1, 1)`, opacity: 1, offset: .8 },
            { transform: `translate(calc(-50% + ${drift * 1.3}px),-100%) scale(1.35, .9)`, opacity: 0 }
          ], { duration: 2600, easing: "ease-in-out", fill: "forwards" });
          clean.push(tint, tw);
          // dirt flung onto the screen while it spins
          for (let i = 0; i < 38; i++) later(450 + i * 48, () => {
            const a = Math.random() * Math.PI * 2, r = .15 + Math.random() * .85;
            mud(tx + Math.cos(a) * r * D * .65, ty + Math.sin(a) * r * D * .45, 40 + Math.random() * 85);
          });
          later(1500, () => blinkOut("air", "air"));
          later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped on a whirlwind 🌪️"); });
        });
      } else if (kind === "bolt") {
        // Lightning: the robot calls it with a bolt from its orb up to the sky, then a violet strike hits the middle of the page
        later(750, () => { charge.remove(); strike([ox, oy], [ox + (Math.random() * 80 - 40), -20], 320, .55); });
        later(950, () => {
          const dim = fxEl("gb-dim", "background: rgba(14, 6, 40, .55)");
          dim.animate([{ opacity: 0 }, { opacity: 1, offset: .2 }, { opacity: 1, offset: .6 }, { opacity: 0 }], { duration: 1300, fill: "forwards" });
          setTimeout(() => dim.remove(), 1350);
        });
        later(1200, () => {
          strike([tx + (Math.random() * 240 - 120), -20], [tx, ty], 520, 1);
          const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(248,242,255,.97) 0, rgba(185,140,255,.7) ${D * .16}px, rgba(130,80,255,.3) ${D * .42}px, rgba(110,60,255,0) ${D * .8}px)`);
          flash.animate([{ opacity: 0 }, { opacity: 1, offset: .08 }, { opacity: .35, offset: .2 }, { opacity: .9, offset: .3 }, { opacity: 0 }], { duration: 900, easing: "ease-out", fill: "forwards" });
          const ring = fxEl("gb-ring bolt", `left:${tx}px;top:${ty}px`);
          ring.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(10)", opacity: 0 }], { duration: 800, easing: "cubic-bezier(.1,.6,.3,1)", fill: "forwards" });
          burst(tx, ty, 34, D * .45, "volt", 900); cracks(tx, ty, D * .55, "bolt");
          setTimeout(() => { flash.remove(); ring.remove(); }, 950);
        });
        later(1450, () => strike([tx + (Math.random() * 300 - 150), -20], [tx + (Math.random() * 40 - 20), ty], 260, .6));   // an after-strike
        later(1600, () => blinkOut("bolt", "volt"));
        later(2300, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — with a thunderclap ⚡"); });
      } else if (kind === "hole") {
        // Black hole: grows in the middle of the page, swallows sparks and then the robot, collapses with a flash
        later(800, () => {
          charge.remove();
          const dim = fxEl("gb-dim", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(4,0,12,.92) 0, rgba(10,2,30,.7) ${D * .35}px, rgba(10,2,30,0) ${D * .75}px)`);
          dim.animate([{ opacity: 0 }, { opacity: 1, offset: .25 }, { opacity: 1, offset: .85 }, { opacity: 0 }], { duration: 3300, fill: "forwards" });
          const hole = fxEl("gb-hole", `left:${tx}px;top:${ty}px;width:${D * .5}px;height:${D * .5}px`);
          hole.innerHTML = '<i class="disk"></i><i class="lens"></i><i class="core"></i>';
          hole.animate([{ transform: "translate(-50%,-50%) scale(0)" }, { transform: "translate(-50%,-50%) scale(1)", offset: .45 }, { transform: "translate(-50%,-50%) scale(1.08)", offset: .88 }, { transform: "translate(-50%,-50%) scale(0)" }],
            { duration: 3200, easing: "ease-in-out", fill: "forwards" });
          clean.push(dim, hole);
          for (let i = 0; i < 40; i++) later(200 + i * 45, () => {   // debris spiralling in
            const a = Math.random() * Math.PI * 2, r = D * (.45 + Math.random() * .3), spin = (Math.random() < .5 ? 1 : -1) * 1.6;
            const s = h("i", { class: `cat-spark big${i % 3 ? "" : " blue"}`, style: `left:${tx}px;top:${ty}px` });
            document.body.append(s);
            const k = [0, .35, .7, 1].map(t => ({ transform: `translate(${Math.cos(a + spin * t) * r * (1 - t)}px, ${Math.sin(a + spin * t) * r * (1 - t)}px) scale(${1.3 - t})`, opacity: t < 1 ? 1 : 0 }));
            s.animate(k, { duration: 1000, easing: "ease-in" }); setTimeout(() => s.remove(), 1000);
          });
          later(2500, () => blinkOut("hole", "blue"));   // and the robot blinks out right where it stands
          later(3000, () => {
            const flash = fxEl("gb-ring hole", `left:${tx}px;top:${ty}px`);
            flash.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(9)", opacity: 0 }], { duration: 700, easing: "ease-out", fill: "forwards" });
            burst(tx, ty, 26, D * .45, "blue", 900); clean.push(flash); cracks(tx, ty, D * .5, "hole");
          });
          later(3900, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — and left a black hole behind 🌀"); });
        });
      } else if (kind === "ice") {
        // Ice storm: the page edges freeze over with frost ferns; the frost melts into drops that run down
        later(700, () => {
          charge.remove();
          const vig = fxEl("gb-frostvig");
          vig.animate([{ opacity: 0 }, { opacity: 1, offset: .2 }, { opacity: 1, offset: .5 }, { opacity: 0 }], { duration: 4300, fill: "forwards" });
          setTimeout(() => vig.remove(), 4400);
          frost(A);
          burst(ox, oy, 16, 80, "ice", 800);
          for (let i = 0; i < 26; i++) later(i * 40, () => burst(A.left + Math.random() * (A.right - A.left), A.top + Math.random() * (A.bottom - A.top), 1, 30, "ice", 900));
          setTimeout(() => waterDrops(16, () => {   // melting
            const e = Math.floor(Math.random() * 4), W2 = A.right - A.left, H2 = A.bottom - A.top;
            return e === 0 ? [A.left + Math.random() * W2, A.top + Math.random() * 90] : e === 1 ? [A.left + Math.random() * W2, A.bottom - 60 - Math.random() * 80]
              : e === 2 ? [A.left + Math.random() * 110, A.top + Math.random() * H2] : [A.right - Math.random() * 110, A.top + Math.random() * H2];
          }), 1900);
        });
        later(1700, () => blinkOut("ice", "ice"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in an ice storm ❄️"); });
      } else if (kind === "meteor") {
        // Meteor: a flaming rock falls from a corner and smashes into the middle — a glowing crater and smoke
        later(700, () => {
          charge.remove();
          const fromLeft = Math.random() < .5, sx = fromLeft ? A.left - 140 : A.right + 140, sy = A.top - 200;
          const m = fxEl("gb-meteor", "left:0;top:0"); clean.push(m);
          const t0 = performance.now(), dur = 850;
          const step = now => {
            if (ended) return;
            const t = Math.min(1, (now - t0) / dur), k = Math.pow(t, 1.5), x = sx + (tx - sx) * k, y = sy + (ty - sy) * k;
            m.style.transform = `translate(${x}px, ${y}px) translate(-50%,-50%) rotate(${t * 540}deg) scale(${.7 + t * .6})`;
            for (let i = 0; i < 2; i++) {
              const sp = h("i", { class: "cat-spark fire", style: `left:${x + Math.random() * 30 - 15}px;top:${y + Math.random() * 30 - 15}px` });
              document.body.append(sp); setTimeout(() => sp.remove(), 800);
            }
            if (Math.random() < .25) smoke(x, y, 1, 20);
            if (t < 1) requestAnimationFrame(step); else impact();
          };
          requestAnimationFrame(step);
        });
        const impact = () => {
          clean.forEach(n => n.remove());
          const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(255,250,230,.98) 0, rgba(255,170,70,.8) ${D * .15}px, rgba(255,90,30,.4) ${D * .4}px, rgba(255,60,20,0) ${D * .8}px)`);
          flash.animate([{ opacity: 0 }, { opacity: 1, offset: .1 }, { opacity: 0 }], { duration: 1000, easing: "ease-out", fill: "forwards" });
          for (const [sc, du] of [[16, 900], [10, 1300]]) {
            const ring = fxEl("gb-ring fire", `left:${tx}px;top:${ty}px`);
            ring.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: `translate(-50%,-50%) scale(${sc})`, opacity: 0 }], { duration: du, easing: "cubic-bezier(.1,.6,.3,1)", fill: "forwards" });
            setTimeout(() => ring.remove(), du + 50);
          }
          burst(tx, ty, 50, D * .55, "fire", 1100); burst(tx, ty, 30, D * .35, "dust", 1000);
          cracks(tx, ty, D * .5, "meteor"); smoke(tx, ty, 12, 160);
          setTimeout(() => flash.remove(), 1050);
          blinkOut("fire", "fire");
          later(1300, () => finish("The Greatness bot escaped — and a meteor fell ☄️"));
        };
      } else if (kind === "water") {
        // Tidal wave: a translucent wave with foam rolls across the page and leaves drops on the glass
        later(700, () => {
          charge.remove();
          const NS = "http://www.w3.org/2000/svg", Wd = (A.right - A.left) * 1.2, Hw = (A.bottom - A.top) * .8;
          const svg = document.createElementNS(NS, "svg");
          svg.setAttribute("class", "gb-fx gb-wave"); svg.setAttribute("width", Wd); svg.setAttribute("height", Hw); svg.setAttribute("viewBox", `0 0 ${Wd} ${Hw}`);
          svg.style.left = `${A.left - Wd}px`; svg.style.top = `${A.bottom - Hw}px`;
          let top = `M0,${Hw}`, crest = "";
          for (let x = 0; x <= Wd; x += 20) {
            const y = Hw * .18 + Math.sin(x / Wd * Math.PI * 5) * 26 + Math.sin(x / Wd * Math.PI * 13) * 9 + (x > Wd * .82 ? (x - Wd * .82) * .9 : 0);
            top += ` L${x},${y.toFixed(1)}`; crest += `${crest ? " L" : "M"}${x},${y.toFixed(1)}`;
          }
          svg.innerHTML = `<defs><linearGradient id="gbWave" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="rgba(140,210,255,.75)"/><stop offset=".35" stop-color="rgba(60,140,240,.55)"/><stop offset="1" stop-color="rgba(20,70,190,.35)"/></linearGradient></defs>
            <path d="${top} L${Wd},${Hw} Z" fill="url(#gbWave)"/><path d="${crest}" class="foam"/><path d="${crest}" class="foam thin" transform="translate(0 14)"/>`;
          document.body.append(svg); clean.push(svg);
          const dx = A.right - A.left + Wd;
          svg.animate([{ transform: `translate(0, ${Hw * .45}px)` }, { transform: `translate(${dx * .5}px, 0)`, offset: .5 }, { transform: `translate(${dx}px, ${Hw * .3}px)` }],
            { duration: 1600, easing: "ease-in-out", fill: "forwards" });
          for (let i = 0; i < 14; i++) later(i * 100, () => burst(A.left + (i / 14) * (A.right - A.left), A.bottom - Hw * .55 - Math.random() * 60, 4, 50, "water", 700));
          later(900, () => waterDrops(20, () => [A.left + Math.random() * (A.right - A.left), A.top + Math.random() * (A.bottom - A.top) * .85]));
        });
        later(1500, () => blinkOut("water", "water"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped on a tidal wave 🌊"); });
      } else if (kind === "seal") {
        // Seal of Greatness: a golden rune circle with the G spins up over the page and is stamped onto the glass
        later(700, () => {
          charge.remove();
          const S = Math.min(D * .75, 620), NS = "http://www.w3.org/2000/svg", svg = document.createElementNS(NS, "svg");
          svg.setAttribute("class", "gb-fx gb-seal"); svg.setAttribute("width", S); svg.setAttribute("height", S); svg.setAttribute("viewBox", "-100 -100 200 200");
          svg.style.left = `${tx - S / 2}px`; svg.style.top = `${ty - S / 2}px`;
          const runes = "ᚠᚢᚦᚨᚱᚲᚷᚹᚺᚾᛁᛃᛇᛈᛉᛊᛏᛒᛖᛗᛚᛜᛞᛟ".repeat(2);
          const tri = (r, rot) => [0, 1, 2].map(i => { const a = rot + i * Math.PI * 2 / 3 - Math.PI / 2; return `${(Math.cos(a) * r).toFixed(1)},${(Math.sin(a) * r).toFixed(1)}`; }).join(" ");
          svg.innerHTML = `<defs><path id="gbRunePath" d="M0,-80 A80,80 0 1,1 -0.1,-80"/></defs>
            <circle r="94" class="ring"/><circle r="86" class="ring thin"/><circle r="70" class="ring thin"/>
            <g class="spin"><text class="runes"><textPath href="#gbRunePath">${runes}</textPath></text></g>
            <g class="spin rev"><polygon points="${tri(66, 0)}" class="tri"/><polygon points="${tri(66, Math.PI)}" class="tri"/></g>
            <circle r="34" class="ring"/><text class="g" y="13">G</text>`;
          document.body.append(svg);
          svg.animate([{ transform: "scale(.2) rotate(-140deg)", opacity: 0 }, { transform: "scale(1) rotate(0deg)", opacity: 1, offset: .55 },
            { transform: "scale(1) rotate(0deg)", opacity: 1, offset: .82 }, { transform: "scale(1.14)", opacity: 1, offset: .9 }, { transform: "scale(.97)", opacity: 1 }],
            { duration: 1600, easing: "ease-out", fill: "forwards" });
          later(1550, () => {   // the stamp
            const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(255,245,200,.9) 0, rgba(255,200,80,.55) ${S * .3}px, rgba(255,180,60,0) ${S * .75}px)`);
            flash.animate([{ opacity: 0 }, { opacity: 1, offset: .15 }, { opacity: 0 }], { duration: 700, fill: "forwards" });
            setTimeout(() => flash.remove(), 750);
            const ring = fxEl("gb-ring seal", `left:${tx}px;top:${ty}px`);
            ring.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(9)", opacity: 0 }], { duration: 800, easing: "ease-out", fill: "forwards" });
            setTimeout(() => ring.remove(), 850);
            burst(tx, ty, 36, S * .6, "gold", 900);
            svg.classList.add("imprint");   // the trace: a glowing imprint that slowly fades
            svg.animate([{ opacity: 1 }, { opacity: .85, offset: .2 }, { opacity: 0 }], { duration: 3400, easing: "ease-in", fill: "forwards", composite: "replace" });
            setTimeout(() => svg.remove(), 3500);
          });
        });
        later(2250, () => blinkOut("seal", "gold"));
        later(2900, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — sealed with Greatness 👑"); });
      } else if (kind === "acid") {
        // Acid cloud: a toxic green cloud spits acid drops that burn little holes into the glass
        later(700, () => {
          charge.remove();
          for (let i = 0; i < 8; i++) {
            const a = Math.random() * Math.PI * 2, r = Math.random() * D * .18, sz = D * (.22 + Math.random() * .18);
            const c = fxEl("gb-cloud", `left:${tx + Math.cos(a) * r}px;top:${ty + Math.sin(a) * r}px;width:${sz}px;height:${sz * .75}px`); clean.push(c);
            c.animate([{ transform: "translate(-50%,-50%) scale(.2)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: .3 },
              { transform: `translate(calc(-50% + ${(Math.random() - .5) * 120}px), calc(-50% + ${(Math.random() - .5) * 60}px)) scale(1.5)`, opacity: 0 }],
              { duration: 2700, easing: "ease-out", fill: "forwards" });
          }
          for (let i = 0; i < 13; i++) later(250 + i * 85, () => {
            const a = Math.random() * Math.PI * 2, r = .25 + Math.random() * .75, x = tx + Math.cos(a) * r * D * .55, y = ty + Math.sin(a) * r * D * .4;
            const b = fxEl("gb-acidball", `left:${tx}px;top:${ty}px`);
            b.animate([{ transform: "translate(-50%,-50%) scale(.4)" }, { transform: `translate(calc(-50% + ${x - tx}px), calc(-50% + ${y - ty}px)) scale(1.3)` }], { duration: 380, easing: "ease-in", fill: "forwards" });
            setTimeout(() => {
              b.remove(); if (ended) return;
              cracks(x, y, 40 + Math.random() * 35, "acid"); burst(x, y, 9, 34, "acid", 700);
              for (let k = 0; k < 6; k++) setTimeout(() => burst(x + (Math.random() - .5) * 20, y + (Math.random() - .5) * 20, 1, 18, "acid", 600), k * 220);   // fizz
            }, 380);
          });
        });
        later(1900, () => blinkOut("acid", "acid"));
        later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in a cloud of acid ☠️"); });
      } else if (kind === "laser") {
        // Orbital beam: a red beam from above sweeps across the page and burns a scorched line into the glass
        later(700, () => {
          charge.remove();
          const sgn = Math.random() < .5 ? 1 : -1, p1 = [tx - D * .45, ty - sgn * D * .16], p2 = [tx + D * .45, ty + sgn * D * .16];
          const beam = fxEl("gb-beam", "left:0;top:0"), hit = fxEl("gb-beamhit", "left:0;top:0"); clean.push(beam, hit);
          const NS = "http://www.w3.org/2000/svg", svg = document.createElementNS(NS, "svg");
          svg.setAttribute("class", "gb-fx gb-scorch"); svg.setAttribute("width", innerWidth); svg.setAttribute("height", innerHeight);
          const layers = ["gl", "sh", "hi"].map(c => { const pa = document.createElementNS(NS, "path"); pa.setAttribute("class", c); svg.append(pa); return pa; });
          document.body.append(svg);
          const pts = [], t0 = performance.now(), dur = 1000;
          const step = now => {
            if (ended) return;
            const t = Math.min(1, (now - t0) / dur), e = t < .5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            const x = p1[0] + (p2[0] - p1[0]) * e + (Math.random() - .5) * 4, y = p1[1] + (p2[1] - p1[1]) * e + (Math.random() - .5) * 4;
            beam.style.transform = `translate(${x - 15}px, -20px)`; beam.style.height = `${y + 20}px`;
            hit.style.transform = `translate(${x}px, ${y}px) translate(-50%,-50%) scale(${.9 + Math.random() * .3})`;
            pts.push([x, y]);
            const d = "M" + pts.map(q => q.map(v => v.toFixed(1)).join(",")).join("L");
            layers.forEach(l => l.setAttribute("d", d));
            const sp = h("i", { class: "cat-spark laser", style: `left:${x}px;top:${y}px` }); document.body.append(sp);
            sp.animate([{ transform: "translate(0,0)", opacity: 1 }, { transform: `translate(${(Math.random() - .5) * 90}px, ${-20 - Math.random() * 70}px)`, opacity: 0 }], { duration: 500 });
            setTimeout(() => sp.remove(), 520);
            if (t < 1) requestAnimationFrame(step);
            else { beam.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 250, fill: "forwards" }); hit.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 250, fill: "forwards" }); smoke(x, y, 4, 40); }
          };
          requestAnimationFrame(step);
          svg.animate([{ opacity: 1 }, { opacity: 1, offset: .35 }, { opacity: 0 }], { duration: 4200, easing: "ease-in", fill: "forwards" });
          setTimeout(() => svg.remove(), 4300);
        });
        later(1500, () => blinkOut("laser", "laser"));
        later(2200, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — the orbital beam fired 🔴"); });
      } else if (kind === "claws") {
        // Shadow claws: dark tentacles crawl in from the edges, then a claw slashes across the page — deep scratches stay
        later(700, () => {
          charge.remove();
          const NS = "http://www.w3.org/2000/svg", svg = document.createElementNS(NS, "svg");
          svg.setAttribute("class", "gb-fx gb-shadow"); svg.setAttribute("width", innerWidth); svg.setAttribute("height", innerHeight);
          const dim = fxEl("gb-dim", `background: radial-gradient(ellipse at ${tx}px ${ty}px, rgba(10,4,20,.15) 0, rgba(8,2,18,.55) ${D * .6}px, rgba(4,0,10,.8) ${D}px)`);
          dim.animate([{ opacity: 0 }, { opacity: 1, offset: .25 }, { opacity: 1, offset: .7 }, { opacity: 0 }], { duration: 2600, fill: "forwards" });
          clean.push(dim);
          const tent = [];
          for (let i = 0; i < 8; i++) {
            const side = i % 4, t = .15 + Math.random() * .7, W2 = A.right - A.left, H2 = A.bottom - A.top;
            const s0 = side === 0 ? [A.left + t * W2, A.top - 10] : side === 1 ? [A.left + t * W2, A.bottom + 10] : side === 2 ? [A.left - 10, A.top + t * H2] : [A.right + 10, A.top + t * H2];
            const e = [s0[0] + (tx - s0[0]) * (.45 + Math.random() * .2), s0[1] + (ty - s0[1]) * (.45 + Math.random() * .2)];
            const nx = -(e[1] - s0[1]), ny = e[0] - s0[0], L = Math.hypot(nx, ny) || 1, w1 = (Math.random() - .5) * .5, w2 = (Math.random() - .5) * .5;
            const c1 = [s0[0] + (e[0] - s0[0]) * .33 + nx * w1, s0[1] + (e[1] - s0[1]) * .33 + ny * w1], c2 = [s0[0] + (e[0] - s0[0]) * .66 + nx * w2, s0[1] + (e[1] - s0[1]) * .66 + ny * w2];
            const d = `M${s0.join(",")} C${c1.join(",")} ${c2.join(",")} ${e.join(",")}`;
            for (const [cls, sw] of [["tg", 34], ["tb", 20], ["tc", 9]]) {
              const pa = document.createElementNS(NS, "path"); pa.setAttribute("d", d); pa.setAttribute("class", cls); pa.setAttribute("stroke-width", sw); svg.append(pa); tent.push(pa);
            }
          }
          document.body.append(svg); clean.push(svg);
          tent.forEach(pa => { const L = pa.getTotalLength(); pa.style.strokeDasharray = `${L} ${L}`;
            pa.animate([{ strokeDashoffset: L }, { strokeDashoffset: 0, offset: .35 }, { strokeDashoffset: 0, offset: .75 }, { strokeDashoffset: L }], { duration: 2400, easing: "ease-in-out", fill: "forwards" }); });
          later(900, () => {   // the slash
            const ns = document.createElementNS(NS, "svg");
            ns.setAttribute("class", "gb-fx gb-claw"); ns.setAttribute("width", innerWidth); ns.setAttribute("height", innerHeight);
            const tilt = (Math.random() - .5) * .5;
            for (let i = 0; i < 4; i++) {
              const off = (i - 1.5) * D * .07, a = [tx - D * .34 + off, ty - D * .3 + tilt * 100], b = [tx + D * .3 + off, ty + D * .3 - tilt * 100];
              const c = [(a[0] + b[0]) / 2 + D * .08, (a[1] + b[1]) / 2 - D * .06];
              const d = `M${a.join(",")} Q${c.join(",")} ${b.join(",")}`;
              for (const cls of ["sg", "sh", "hi"]) {
                const pa = document.createElementNS(NS, "path"); pa.setAttribute("d", d); pa.setAttribute("class", cls); ns.append(pa);
                const L = pa.getTotalLength(); pa.style.strokeDasharray = `${L} ${L}`;
                pa.animate([{ strokeDashoffset: L }, { strokeDashoffset: 0, offset: .05 }, { strokeDashoffset: 0, offset: .35 }, { strokeDashoffset: -L }],
                  { duration: 3600, delay: i * 40, easing: "ease-in-out", fill: "both" });
              }
            }
            document.body.append(ns); setTimeout(() => ns.remove(), 3800);
            const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(220,200,255,.55) 0, rgba(140,90,255,.25) ${D * .3}px, rgba(100,50,220,0) ${D * .7}px)`);
            flash.animate([{ opacity: 0 }, { opacity: 1, offset: .15 }, { opacity: 0 }], { duration: 500, fill: "forwards" }); setTimeout(() => flash.remove(), 550);
            burst(tx, ty, 26, D * .4, "shadow", 800);
          });
        });
        later(1600, () => blinkOut("claws", "shadow"));
        later(2400, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped into the shadows 🖤"); });
      } else if (kind === "glitch") {
        // Glitch: the page "breaks" — shifted colour slices, scanlines, noise; dead pixels stay and get fixed one by one
        later(700, () => {
          charge.remove();
          const box2 = fxEl("gb-glitch", `left:${A.left}px;top:${A.top}px;width:${A.right - A.left}px;height:${A.bottom - A.top}px`); clean.push(box2);
          const slices = Array.from({ length: 16 }, () => { const sl = h("i"); box2.append(sl); return sl; });
          const shuffle = () => slices.forEach(sl => {
            const top = Math.random() * 100, hgt = 1 + Math.random() * 9;
            sl.style.cssText = `top:${top}%;height:${hgt}%;transform:translateX(${(Math.random() - .5) * 60}px);` +
              `backdrop-filter:hue-rotate(${Math.floor(Math.random() * 360)}deg) saturate(${2 + Math.random() * 3}) contrast(1.6)${Math.random() < .3 ? " invert(1)" : ""};` +
              `background:${["rgba(255,0,80,.18)", "rgba(0,255,200,.16)", "rgba(60,120,255,.18)", "rgba(255,255,255,.08)"][Math.floor(Math.random() * 4)]}`;
          });
          shuffle();
          const tick2 = setInterval(shuffle, 80); setTimeout(() => clearInterval(tick2), 1200);
          box2.animate([{ opacity: 0 }, { opacity: 1, offset: .05 }, { opacity: .3, offset: .3 }, { opacity: 1, offset: .4 }, { opacity: 1, offset: .85 }, { opacity: 0 }], { duration: 1250, fill: "forwards" });
          // the trace: dead pixels and stuck colour blocks, repaired one by one
          const colors = ["#ff0050", "#00ff9c", "#2f6bff", "#ffffff", "#ffe600", "#ff00ff"];
          for (let i = 0; i < 70; i++) {
            const big = i < 10, w = big ? 18 + Math.random() * 60 : 3 + Math.random() * 5, hh = big ? 5 + Math.random() * 12 : w;
            const px = h("div", { class: `gb-fx gb-deadpx${big ? " block" : ""}`, style: `left:${A.left + Math.random() * (A.right - A.left)}px;top:${A.top + Math.random() * (A.bottom - A.top)}px;width:${w}px;height:${hh}px;background:${colors[Math.floor(Math.random() * colors.length)]}` });
            document.body.append(px);
            const life = 1400 + Math.random() * 3200;
            px.animate([{ opacity: 0 }, { opacity: 1, offset: .02 }, { opacity: 1, offset: .96 }, { opacity: 0 }], { duration: life, fill: "forwards", easing: "steps(1)" });
            setTimeout(() => px.remove(), life);
          }
        });
        later(1500, () => blinkOut("glitch", "volt"));
        later(2100, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — have you tried turning it off and on again? 📺"); });
      } else if (kind === "sakura") {
        // Sakura storm: a whirl of petals; some stick to the glass and slide down one by one
        later(700, () => {
          charge.remove();
          const tint = fxEl("gb-dim", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(255,170,210,.22) 0, rgba(255,140,200,.1) ${D * .5}px, rgba(255,140,200,0) ${D}px)`);
          tint.animate([{ opacity: 0 }, { opacity: 1, offset: .2 }, { opacity: 1, offset: .7 }, { opacity: 0 }], { duration: 2600, fill: "forwards" }); clean.push(tint);
          for (let i = 0; i < 90; i++) {
            const sz = 10 + Math.random() * 10, petal = h("div", { class: "gb-fx gb-petal", style: `left:${ox}px;top:${oy}px;width:${sz}px;height:${sz * .7}px` });
            document.body.append(petal);
            const stick = i < 34, a0 = Math.random() * Math.PI * 2, spin = (Math.random() < .5 ? 1 : -1) * (2 + Math.random() * 2), R = D * (.15 + Math.random() * .35);
            const fx2 = stick ? A.left + Math.random() * (A.right - A.left) : tx + Math.cos(a0 + spin) * R * 1.6, fy2 = stick ? A.top + Math.random() * (A.bottom - A.top) : ty + Math.sin(a0 + spin) * R * 1.2;
            const k = [0, .33, .66].map((t, j) => ({ transform: `translate(${tx - ox + Math.cos(a0 + spin * t) * R * (.4 + t)}px, ${ty - oy + Math.sin(a0 + spin * t) * R * .7 * (.4 + t)}px) rotate(${j * 240}deg)`, opacity: 1 }));
            const dur = 1500 + Math.random() * 700;
            petal.animate([{ transform: "translate(0,0) rotate(0deg)", opacity: 0 }, ...k, { transform: `translate(${fx2 - ox}px, ${fy2 - oy}px) rotate(${720 + Math.random() * 180}deg)`, opacity: stick ? 1 : 0 }],
              { duration: dur, delay: i * 12, easing: "ease-out", fill: "forwards" });
            if (stick) {   // stuck on the glass: slides down later
              const wait = dur + i * 12 + 300 + Math.random() * 2400;
              setTimeout(() => petal.animate([{ transform: `translate(${fx2 - ox}px, ${fy2 - oy}px) rotate(760deg)`, opacity: 1 }, { transform: `translate(${fx2 - ox + (Math.random() - .5) * 30}px, ${fy2 - oy + 90 + Math.random() * 80}px) rotate(${790 + Math.random() * 90}deg)`, opacity: 0 }],
                { duration: 1300, easing: "ease-in", fill: "forwards" }), wait);
              setTimeout(() => petal.remove(), wait + 1350);
            } else setTimeout(() => petal.remove(), dur + i * 12 + 50);
          }
        });
        later(1700, () => blinkOut("sakura", "petal"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in a sakura storm 🌸"); });
      } else if (kind === "fireworks") {
        // Fireworks: rockets burst over the page; confetti and glitter settle down
        const palette = [["#ff4d6d", "#ffd166"], ["#7cf2ff", "#ffffff"], ["#b892ff", "#ff7bd5"], ["#7dff9b", "#fff07a"], ["#ffa64d", "#ff4d4d"]];
        later(700, () => charge.remove());
        for (let r = 0; r < 7; r++) later(650 + r * 230, () => {
          const x = A.left + (A.right - A.left) * (.12 + Math.random() * .76), y = A.top + (A.bottom - A.top) * (.12 + Math.random() * .35);
          const rk = fxEl("gb-rocket", `left:${x}px;top:${A.bottom}px`);
          rk.animate([{ transform: "translate(-50%,0)" }, { transform: `translate(-50%, ${y - A.bottom}px)` }], { duration: 520, easing: "cubic-bezier(.2,.7,.4,1)", fill: "forwards" });
          for (let k = 0; k < 8; k++) setTimeout(() => burst(x + (Math.random() - .5) * 6, A.bottom + (y - A.bottom) * (k / 8), 1, 12, "gold", 500), k * 60);
          setTimeout(() => {
            rk.remove(); if (ended) return;
            const [c1, c2] = palette[Math.floor(Math.random() * palette.length)];
            const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${x}px ${y}px, rgba(255,255,255,.35) 0, rgba(255,255,255,0) 160px)`);
            flash.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 400, fill: "forwards" }); setTimeout(() => flash.remove(), 420);
            for (let i = 0; i < 48; i++) {
              const a = i / 48 * Math.PI * 2 + Math.random() * .1, rr = 90 + Math.random() * 90;
              const p = h("i", { class: "gb-fx gb-fw", style: `left:${x}px;top:${y}px;background:${i % 2 ? c1 : c2};box-shadow:0 0 8px ${i % 2 ? c1 : c2}` });
              document.body.append(p);
              p.animate([{ transform: "translate(-50%,-50%) scale(1.2)", opacity: 1 }, { transform: `translate(calc(-50% + ${Math.cos(a) * rr}px), calc(-50% + ${Math.sin(a) * rr}px)) scale(1)`, opacity: 1, offset: .55 },
                { transform: `translate(calc(-50% + ${Math.cos(a) * rr * 1.1}px), calc(-50% + ${Math.sin(a) * rr * 1.1 + 60}px)) scale(.4)`, opacity: 0 }], { duration: 1300, easing: "cubic-bezier(.1,.7,.4,1)", fill: "forwards" });
              setTimeout(() => p.remove(), 1350);
            }
          }, 520);
        });
        later(2300, () => {   // confetti and glitter settle
          const cols = ["#ff4d6d", "#ffd166", "#7cf2ff", "#b892ff", "#7dff9b", "#ffffff"];
          for (let i = 0; i < 60; i++) {
            const x = A.left + Math.random() * (A.right - A.left), y = A.top + Math.random() * (A.bottom - A.top) * .5, glitter = i % 3 === 0;
            const c = h("i", { class: `gb-fx ${glitter ? "gb-glitter" : "gb-confetti"}`, style: `left:${x}px;top:${y}px;background:${cols[i % cols.length]}` });
            document.body.append(c);
            const dur = 2500 + Math.random() * 1800, fall = 120 + Math.random() * 260;
            c.animate([{ transform: "translate(0,0) rotate(0deg)", opacity: 0 }, { transform: `translate(${(Math.random() - .5) * 40}px, ${fall * .3}px) rotate(${Math.random() * 360}deg)`, opacity: 1, offset: .15 },
              { transform: `translate(${(Math.random() - .5) * 80}px, ${fall}px) rotate(${360 + Math.random() * 360}deg)`, opacity: 0 }], { duration: dur, easing: "ease-in", fill: "forwards" });
            setTimeout(() => c.remove(), dur + 50);
          }
        });
        later(2200, () => blinkOut("fireworks", "gold"));
        later(2900, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped with a fireworks show 🎆"); });
      } else if (kind === "lava") {
        // Eruption: a lava fountain bursts from the bottom; glowing blobs land on the glass and cool into black rock
        later(700, () => {
          charge.remove();
          const bx = tx + (Math.random() - .5) * D * .3, by = A.bottom;
          const glow = fxEl("gb-flash", `background: radial-gradient(ellipse at ${bx}px ${by}px, rgba(255,140,40,.7) 0, rgba(255,70,20,.35) ${D * .25}px, rgba(255,60,20,0) ${D * .6}px)`);
          glow.animate([{ opacity: 0 }, { opacity: 1, offset: .2 }, { opacity: 0 }], { duration: 1900, fill: "forwards" }); setTimeout(() => glow.remove(), 1950);
          for (let i = 0; i < 16; i++) later(i * 70, () => {
            const x = bx + (Math.random() - .5) * D * 1.1, y = A.top + (A.bottom - A.top) * (.15 + Math.random() * .6), sz = 22 + Math.random() * 30;
            const b = fxEl("gb-lavaball", `left:${bx}px;top:${by}px;width:${sz * .6}px;height:${sz * .6}px`);
            const peak = Math.min(y, A.top + 40) - 30;
            b.animate([{ transform: "translate(-50%,-50%)" }, { transform: `translate(calc(-50% + ${(x - bx) * .5}px), calc(-50% + ${peak - by}px))`, offset: .5 },
              { transform: `translate(calc(-50% + ${x - bx}px), calc(-50% + ${y - by}px)) scale(1.3)` }], { duration: 650, easing: "cubic-bezier(.3,0,.7,1)", fill: "forwards" });
            setTimeout(() => {
              b.remove(); if (ended) return;
              burst(x, y, 6, 30, "fire", 500);
              const splat = h("div", { class: "gb-fx gb-lava", style: `left:${x}px;top:${y}px;width:${sz}px;height:${sz * (.7 + Math.random() * .4)}px;border-radius:${[0, 0, 0, 0].map(() => 30 + Math.floor(Math.random() * 40)).join("% ")}%` });
              document.body.append(splat);
              splat.animate([{ transform: "translate(-50%,-50%) scale(.3)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: .05 }, { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: .8 },
                { transform: "translate(-50%, calc(-50% + 30px)) scale(.6)", opacity: 0 }], { duration: 4200, easing: "ease-in", fill: "forwards" });
              setTimeout(() => splat.remove(), 4300);
            }, 650);
          });
          for (let k = 0; k < 10; k++) later(k * 60, () => burst(bx, by - 10, 4, 120, "fire", 700));
        });
        later(1700, () => blinkOut("fire", "fire"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — the floor is lava 🌋"); });
      } else if (kind === "bubbles") {
        // Bubbles: a swarm of iridescent soap bubbles floats up and pops — rainbow rings dry on the glass
        later(600, () => charge.remove());
        for (let i = 0; i < 46; i++) later(600 + i * 35, () => {
          const sz = 18 + Math.random() * 44, b = fxEl("gb-bubble", `left:${ox}px;top:${oy}px;width:${sz}px;height:${sz}px`);
          const x = A.left + Math.random() * (A.right - A.left), y = A.top + Math.random() * (A.bottom - A.top) * .85, dur = 1100 + Math.random() * 900;
          b.animate([{ transform: "translate(-50%,-50%) scale(.2)" }, { transform: `translate(calc(-50% + ${(x - ox) * .5 + (Math.random() - .5) * 80}px), calc(-50% + ${(y - oy) * .5}px)) scale(.9)`, offset: .5 },
            { transform: `translate(calc(-50% + ${x - ox}px), calc(-50% + ${y - oy}px)) scale(1)` }], { duration: dur, easing: "ease-out", fill: "forwards" });
          setTimeout(() => {
            b.remove(); if (ended) return;
            burst(x, y, 6, sz * .7, "water", 350);
            if (Math.random() < .55) {
              const ring = h("div", { class: "gb-fx gb-bubblering", style: `left:${x}px;top:${y}px;width:${sz}px;height:${sz}px` });
              document.body.append(ring);
              ring.animate([{ opacity: .9 }, { opacity: .7, offset: .3 }, { opacity: 0 }], { duration: 3600, fill: "forwards" }); setTimeout(() => ring.remove(), 3650);
            }
          }, dur);
        });
        later(1800, () => blinkOut("water", "water"));
        later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in a cloud of bubbles 🫧"); });
      } else if (kind === "eclipse") {
        // Eclipse: a dark disc covers the light, the corona blazes — an afterimage stays in your eyes
        later(700, () => {
          charge.remove();
          const R = Math.min(D * .22, 200);
          const dark = fxEl("gb-dim", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(0,0,0,.6) 0, rgba(2,0,10,.85) ${R * 2}px, rgba(0,0,6,.92) 100%)`);
          dark.animate([{ opacity: 0 }, { opacity: 1, offset: .3 }, { opacity: 1, offset: .75 }, { opacity: 0 }], { duration: 2400, fill: "forwards" });
          const sun = fxEl("gb-eclipse", `left:${tx}px;top:${ty}px;width:${R * 2}px;height:${R * 2}px`);
          sun.animate([{ transform: "translate(-50%,-50%) scale(.6)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1)", opacity: 1, offset: .3 }, { transform: "translate(-50%,-50%) scale(1.05)", opacity: 1, offset: .75 },
            { transform: "translate(-50%,-50%) scale(1.2)", opacity: 0 }], { duration: 2400, fill: "forwards" });
          clean.push(dark, sun);
          later(1700, () => {   // the afterimage
            const ai = h("div", { class: "gb-fx gb-afterimage", style: `left:${tx}px;top:${ty}px;width:${R * 2.4}px;height:${R * 2.4}px` });
            document.body.append(ai);
            ai.animate([{ opacity: 0 }, { opacity: .85, offset: .1 }, { opacity: .5, offset: .5 }, { opacity: 0 }], { duration: 4000, fill: "forwards" }); setTimeout(() => ai.remove(), 4050);
          });
        });
        later(1700, () => blinkOut("claws", "shadow"));
        later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in a total eclipse 🌑"); });
      } else if (kind === "spores") {
        // Spores: glowing spores drift over the page, little luminous mushrooms sprout; the glowing mould fades
        later(700, () => {
          charge.remove();
          for (let i = 0; i < 60; i++) {
            const s = h("i", { class: "gb-fx gb-spore", style: `left:${ox}px;top:${oy}px` }); document.body.append(s);
            const x = A.left + Math.random() * (A.right - A.left), y = A.top + Math.random() * (A.bottom - A.top), dur = 1600 + Math.random() * 1400;
            s.animate([{ transform: "translate(0,0)", opacity: 0 }, { transform: `translate(${(x - ox) * .5 + (Math.random() - .5) * 120}px, ${(y - oy) * .5 - 40}px)`, opacity: 1, offset: .4 },
              { transform: `translate(${x - ox}px, ${y - oy}px)`, opacity: 0 }], { duration: dur, delay: i * 15, easing: "ease-out", fill: "forwards" });
            setTimeout(() => s.remove(), dur + i * 15 + 50);
          }
          const spots = [[A.left + 30, A.bottom - 10], [A.right - 30, A.bottom - 10], [A.left + 30, A.top + 60], [A.right - 30, A.top + 60], [tx, A.bottom - 10]];
          spots.forEach(([x, y], k) => later(500 + k * 150, () => {
            for (let j = 0; j < 4; j++) {
              const mx = x + (Math.random() - .5) * 90, sz = 18 + Math.random() * 18;
              const m = h("div", { class: "gb-fx gb-shroom", style: `left:${mx}px;top:${y - (k > 1 && k < 4 ? -sz : 0)}px;width:${sz}px;height:${sz}px;${k > 1 && k < 4 ? "transform-origin:top;rotate:180deg" : ""}` }, h("i"), h("b"));
              document.body.append(m);
              m.animate([{ scale: "0" }, { scale: "1.1", offset: .08 }, { scale: "1", offset: .12 }, { scale: "1", opacity: 1, offset: .75 }, { scale: ".8", opacity: 0 }], { duration: 4200, fill: "forwards" });
              setTimeout(() => m.remove(), 4250);
            }
            const mould = h("div", { class: "gb-fx gb-mould", style: `left:${x}px;top:${y}px;width:160px;height:110px` }); document.body.append(mould);
            mould.animate([{ opacity: 0 }, { opacity: 1, offset: .15 }, { opacity: 1, offset: .5 }, { opacity: 0 }], { duration: 4200, fill: "forwards" }); setTimeout(() => mould.remove(), 4250);
          }));
        });
        later(1800, () => blinkOut("acid", "acid"));
        later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in a cloud of spores 🍄"); });
      } else if (kind === "portal") {
        // Portal: two rune portals open; sparks jump between them, the robot slips through — burnt rings stay
        later(700, () => {
          charge.remove();
          const S = Math.min(D * .3, 260), p1 = [tx - D * .32, ty], p2 = [tx + D * .32, ty];
          const rings = [p1, p2].map(([x, y], i) => {
            const r = fxEl(`gb-portal ${i ? "b" : "a"}`, `left:${x}px;top:${y}px;width:${S}px;height:${S * 1.25}px`);
            r.animate([{ transform: "translate(-50%,-50%) scale(0)" }, { transform: "translate(-50%,-50%) scale(1)", offset: .2 }, { transform: "translate(-50%,-50%) scale(1)", offset: .8 }, { transform: "translate(-50%,-50%) scale(.2)", opacity: 0 }],
              { duration: 2300, easing: "ease-out", fill: "forwards" });
            clean.push(r); return r;
          });
          for (let i = 0; i < 18; i++) later(300 + i * 70, () => {
            const [a, b] = i % 2 ? [p1, p2] : [p2, p1], s = h("i", { class: "cat-spark big shadow", style: `left:${a[0]}px;top:${a[1] + (Math.random() - .5) * S * .8}px` });
            document.body.append(s);
            s.animate([{ transform: "translate(0,0) scale(1.3)", opacity: 1 }, { transform: `translate(${b[0] - a[0]}px, ${(Math.random() - .5) * 60}px) scale(.4)`, opacity: .2 }], { duration: 420, easing: "ease-in" });
            setTimeout(() => s.remove(), 430);
          });
          later(2200, () => [p1, p2].forEach(([x, y]) => {   // burnt rune rings
            const t = h("div", { class: "gb-fx gb-portal burnt", style: `left:${x}px;top:${y}px;width:${S}px;height:${S * 1.25}px;transform:translate(-50%,-50%)` });
            document.body.append(t);
            t.animate([{ opacity: .9 }, { opacity: .7, offset: .3 }, { opacity: 0 }], { duration: 3400, fill: "forwards" }); setTimeout(() => t.remove(), 3450);
          }));
          void rings;
        });
        later(1500, () => blinkOut("claws", "shadow"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped through a portal 🌌"); });
      } else if (kind === "pixel") {
        // Pixel art: the page turns 8-bit for a moment; a few big stuck pixels come back one by one
        later(700, () => {
          charge.remove();
          const box3 = fxEl("gb-pixelate", `left:${A.left}px;top:${A.top}px;width:${A.right - A.left}px;height:${A.bottom - A.top}px`); clean.push(box3);
          const pal = ["#0f380f", "#306230", "#8bac0f", "#9bbc0f", "#e0f8d0", "#ff004d", "#29adff", "#ffec27"];
          for (let i = 0; i < 90; i++) {
            const w = 16 * (1 + Math.floor(Math.random() * 4));
            box3.append(h("i", { style: `left:${Math.floor(Math.random() * (A.right - A.left) / 16) * 16}px;top:${Math.floor(Math.random() * (A.bottom - A.top) / 16) * 16}px;width:${w}px;height:${w}px;background:${pal[Math.floor(Math.random() * pal.length)]}` }));
          }
          box3.animate([{ opacity: 0 }, { opacity: 1, offset: .1 }, { opacity: 1, offset: .8 }, { opacity: 0 }], { duration: 1500, fill: "forwards", easing: "steps(6)" });
          later(1300, () => {
            for (let i = 0; i < 24; i++) {
              const w = 16 * (1 + Math.floor(Math.random() * 2)), px = h("div", { class: "gb-fx gb-deadpx", style: `left:${A.left + Math.floor(Math.random() * (A.right - A.left) / 16) * 16}px;top:${A.top + Math.floor(Math.random() * (A.bottom - A.top) / 16) * 16}px;width:${w}px;height:${w}px;background:${pal[Math.floor(Math.random() * pal.length)]};opacity:.85` });
              document.body.append(px);
              const life = 600 + Math.random() * 3200;
              px.animate([{ opacity: .85 }, { opacity: .85, offset: .95 }, { opacity: 0 }], { duration: life, easing: "steps(1)", fill: "forwards" }); setTimeout(() => px.remove(), life);
            }
          });
        });
        later(1600, () => blinkOut("glitch", "volt"));
        later(2300, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped in 8-bit 👾"); });
      } else if (kind === "printer") {
        // Printer: a paper tape prints GREATNESS from the top, tears into scraps; some stick to the glass
        later(700, () => {
          charge.remove();
          const L = (A.bottom - A.top) * .7, tape = fxEl("gb-tape", `left:${tx}px;top:${A.top}px;height:${L}px`);
          tape.innerHTML = Array.from({ length: Math.ceil(L / 26) }, () => "<span>GREATNESS</span>").join("");
          tape.animate([{ clipPath: "inset(0 0 100% 0)" }, { clipPath: "inset(0 0 0 0)" }], { duration: 1000, easing: "steps(18)", fill: "forwards" });
          clean.push(tape);
          later(1150, () => {
            tape.remove();
            const n = 9;
            for (let i = 0; i < n; i++) {
              const y = A.top + (i + .5) * L / n, stick = i % 2 === 0;
              const sc = h("div", { class: "gb-fx gb-scrap", style: `left:${tx}px;top:${y}px;height:${L / n}px` }, h("span", {}, "GREATNESS"));
              document.body.append(sc);
              const fx2 = (Math.random() - .5) * D * .8, fy2 = stick ? (Math.random() - .5) * 120 : (A.bottom - y) + 60, rot = (Math.random() - .5) * 120;
              sc.animate([{ transform: "translate(-50%,-50%) rotate(0deg)" }, { transform: `translate(calc(-50% + ${fx2}px), calc(-50% + ${fy2}px)) rotate(${rot}deg)`, opacity: stick ? 1 : 0 }],
                { duration: 700 + Math.random() * 300, easing: "cubic-bezier(.2,.7,.4,1)", fill: "forwards" });
              const life = stick ? 1200 + Math.random() * 2600 : 1000;
              if (stick) setTimeout(() => sc.animate([{ opacity: 1 }, { opacity: 0, translate: "0 60px" }], { duration: 700, fill: "forwards" }), life);
              setTimeout(() => sc.remove(), life + 750);
            }
          });
        });
        later(1700, () => blinkOut("seal", "gold"));
        later(2400, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — paper jam 📠"); });
      } else if (kind === "banhammer") {
        // Banhammer: a giant hammer slams the middle of the page — cracks and a BANNED stamp
        later(700, () => {
          charge.remove();
          const S = Math.min(D * .5, 420), hm = fxEl("gb-hammer", `left:${tx}px;top:${ty}px;width:${S}px;height:${S}px`); hm.append(h("i", { class: "handle" }), h("i", { class: "head" }));
          hm.animate([{ transform: "translate(-20%,-95%) rotate(-75deg)", opacity: 0 }, { transform: "translate(-20%,-95%) rotate(-75deg)", opacity: 1, offset: .35 },
            { transform: "translate(-20%,-95%) rotate(8deg)", opacity: 1, offset: .55 }, { transform: "translate(-20%,-95%) rotate(0deg)", opacity: 1, offset: .65 }, { transform: "translate(-20%,-95%) rotate(0deg)", opacity: 0 }],
            { duration: 1500, easing: "cubic-bezier(.6,0,.9,.4)", fill: "forwards" });
          clean.push(hm);
          later(820, () => {
            const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(255,255,255,.9) 0, rgba(255,80,80,.4) ${D * .2}px, rgba(255,60,60,0) ${D * .6}px)`);
            flash.animate([{ opacity: 0 }, { opacity: 1, offset: .1 }, { opacity: 0 }], { duration: 600, fill: "forwards" }); setTimeout(() => flash.remove(), 650);
            const ring = fxEl("gb-ring laser", `left:${tx}px;top:${ty}px`);
            ring.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(12)", opacity: 0 }], { duration: 800, easing: "ease-out", fill: "forwards" }); setTimeout(() => ring.remove(), 850);
            burst(tx, ty, 30, D * .4, "dust", 800); cracks(tx, ty, D * .45, "rock");
            const stamp = h("div", { class: "gb-fx gb-banned", style: `left:${tx}px;top:${ty}px` }, "BANNED");
            document.body.append(stamp);
            stamp.animate([{ transform: "translate(-50%,-50%) rotate(-12deg) scale(2.2)", opacity: 0 }, { transform: "translate(-50%,-50%) rotate(-12deg) scale(1)", opacity: 1, offset: .08 },
              { transform: "translate(-50%,-50%) rotate(-12deg) scale(1)", opacity: 1, offset: .6 }, { transform: "translate(-50%,-50%) rotate(-12deg) scale(1)", opacity: 0 }], { duration: 3400, fill: "forwards" });
            setTimeout(() => stamp.remove(), 3450);
          });
        });
        later(1700, () => blinkOut("laser", "laser"));
        later(2400, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — BANNED 🔨"); });
      } else if (kind === "money") {
        // Money rain: golden G-coins rain down and bounce; they lie at the bottom and vanish one by one
        later(600, () => charge.remove());
        for (let i = 0; i < 44; i++) later(600 + i * 40, () => {
          const x = A.left + 20 + Math.random() * (A.right - A.left - 40), land = A.bottom - 14 - Math.random() * 30, sz = 22 + Math.random() * 12;
          const c = fxEl("gb-coin", `left:${x}px;top:${A.top - 40}px;width:${sz}px;height:${sz}px`); c.textContent = "G";
          const fall = land - (A.top - 40), dur = 800 + Math.random() * 500;
          c.animate([{ transform: "translate(-50%,0) rotateY(0deg)" }, { transform: `translate(-50%, ${fall}px) rotateY(540deg)`, offset: .75 },
            { transform: `translate(-50%, ${fall - 26}px) rotateY(630deg)`, offset: .87 }, { transform: `translate(-50%, ${fall}px) rotateY(720deg)` }], { duration: dur, easing: "ease-in", fill: "forwards" });
          const life = dur + 400 + Math.random() * 3000;
          setTimeout(() => { if (c.isConnected) c.animate([{ opacity: 1, scale: "1" }, { opacity: 0, scale: "1.5" }], { duration: 250, fill: "forwards" }); }, life);
          setTimeout(() => c.remove(), life + 260);
        });
        later(1900, () => blinkOut("seal", "gold"));
        later(2600, () => finish("The Greatness bot escaped — and made it rain 💸"));
      } else if (kind === "swarm") {
        // Swarm: glowing bees chase your cursor for a moment, then scatter — honey drips on the glass
        later(700, () => {
          charge.remove();
          const bees = Array.from({ length: 34 }, (_, i) => { const b = fxEl("gb-bee", `left:0;top:0`);
            return { b, x: ox + (Math.random() - .5) * 60, y: oy + (Math.random() - .5) * 60, vx: (Math.random() - .5) * 14, vy: (Math.random() - .5) * 14, r: 30 + Math.random() * 110, ph: Math.random() * 6.3, sp: 120 + Math.random() * 160 }; });
          clean.push(...bees.map(z => z.b));
          const t0 = performance.now();
          const step = now => {
            if (ended) return;
            const t = now - t0, target = ptr || [tx, ty], scatter = t > 1700;
            bees.forEach((z, i) => {
              // each bee circles the cursor on its own orbit, then they scatter
              const ax = scatter ? (z.x - tx) * .02 : (target[0] + Math.cos(t / z.sp + z.ph) * z.r - z.x) * .01, ay = scatter ? (z.y - ty) * .02 : (target[1] + Math.sin(t / (z.sp * .8) + z.ph) * z.r * .7 - z.y) * .01;
              z.vx = (z.vx + ax + (Math.random() - .5) * 2.4) * .93; z.vy = (z.vy + ay + (Math.random() - .5) * 2.4) * .93;
              z.x += z.vx; z.y += z.vy;
              z.b.style.transform = `translate(${z.x}px, ${z.y}px) translate(-50%,-50%) scaleX(${z.vx < 0 ? -1 : 1})`;
            });
            if (t < 2300) requestAnimationFrame(step);
          };
          requestAnimationFrame(step);
          later(1500, () => waterDrops(10, () => [A.left + Math.random() * (A.right - A.left), A.top + Math.random() * (A.bottom - A.top) * .8], "honey"));
        });
        later(1900, () => blinkOut("seal", "gold"));
        later(2700, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped — buzz buzz 🐝"); });
      } else if (kind === "rainbow") {
        // Rainbow: a rainbow arcs across the page with sparkles; a prism glare slides over the glass
        later(700, () => {
          charge.remove();
          const NS = "http://www.w3.org/2000/svg", Wd = A.right - A.left, Hh = A.bottom - A.top, svg = document.createElementNS(NS, "svg");
          svg.setAttribute("class", "gb-fx gb-rainbow"); svg.setAttribute("width", Wd); svg.setAttribute("height", Hh); svg.style.left = `${A.left}px`; svg.style.top = `${A.top}px`;
          const cols = ["#ff3b3b", "#ff9a2e", "#ffe14a", "#4ade80", "#4f9dff", "#6b5bff", "#b26bff"], R = Math.min(Wd * .48, Hh * .95);
          cols.forEach((c, i) => {
            const r = R - i * 14, p = document.createElementNS(NS, "path");
            p.setAttribute("d", `M${Wd / 2 - r},${Hh} A${r},${r} 0 0 1 ${Wd / 2 + r},${Hh}`); p.setAttribute("stroke", c); p.setAttribute("stroke-width", "15"); p.setAttribute("fill", "none"); svg.append(p);
            const L = Math.PI * r; p.style.strokeDasharray = `${L} ${L}`;
            p.animate([{ strokeDashoffset: L }, { strokeDashoffset: 0, offset: .4 }, { strokeDashoffset: 0 }], { duration: 2400, easing: "ease-out", fill: "forwards" });
          });
          document.body.append(svg); clean.push(svg);
          svg.animate([{ opacity: 0 }, { opacity: .9, offset: .1 }, { opacity: .9, offset: .7 }, { opacity: 0 }], { duration: 2400, fill: "forwards" });
          for (let i = 0; i < 20; i++) later(i * 70, () => burst(A.left + Math.random() * Wd, A.top + Math.random() * Hh * .7, 2, 30, ["gold", "water", "petal", "acid"][i % 4], 700));
          later(1700, () => {   // prism glare
            const g = h("div", { class: "gb-fx gb-prism", style: `left:${A.left}px;top:${A.top}px;width:${Wd}px;height:${Hh}px` }); document.body.append(g);
            g.animate([{ backgroundPosition: "-60% 0", opacity: 0 }, { opacity: .8, offset: .15 }, { backgroundPosition: "160% 0", opacity: 0 }], { duration: 3200, easing: "ease-in-out", fill: "forwards" });
            setTimeout(() => g.remove(), 3250);
          });
        });
        later(1700, () => blinkOut("sakura", "petal"));
        later(2500, () => { clean.forEach(n => n.remove()); finish("The Greatness bot escaped over a rainbow 🌈"); });
      } else if (kind === "leaves") {
        // Leaf storm: a gust blows autumn leaves across; some stick to the glass and get blown off one by one
        later(600, () => charge.remove());
        const fromLeft = Math.random() < .5, cols = ["#d9622b", "#e8a13a", "#b83a1c", "#c98a2b", "#8a4b1e"];
        for (let i = 0; i < 70; i++) later(600 + i * 22, () => {
          const sz = 14 + Math.random() * 14, y0 = A.top + Math.random() * (A.bottom - A.top), stick = i % 3 === 0;
          const lf = h("div", { class: "gb-fx gb-leaf", style: `left:${fromLeft ? A.left - 30 : A.right + 30}px;top:${y0}px;width:${sz}px;height:${sz * .7}px;background:${cols[i % cols.length]}` });
          document.body.append(lf);
          const W2 = A.right - A.left + 60, dx = (fromLeft ? 1 : -1) * (stick ? W2 * (.2 + Math.random() * .6) : W2 + 40), dy = (Math.random() - .3) * 160, dur = 900 + Math.random() * 600;
          lf.animate([{ transform: "translate(0,0) rotate(0deg)" }, { transform: `translate(${dx * .5}px, ${dy * .5 - 40}px) rotate(${360 + Math.random() * 360}deg)`, offset: .5 },
            { transform: `translate(${dx}px, ${dy}px) rotate(${700 + Math.random() * 360}deg)` }], { duration: dur, easing: "ease-out", fill: "forwards" });
          if (stick) {
            const wait = dur + 400 + Math.random() * 2600;
            setTimeout(() => lf.animate([{ transform: `translate(${dx}px, ${dy}px) rotate(720deg)`, opacity: 1 }, { transform: `translate(${dx + (fromLeft ? 1 : -1) * 260}px, ${dy - 60}px) rotate(1080deg)`, opacity: 0 }],
              { duration: 700, easing: "ease-in", fill: "forwards" }), wait);
            setTimeout(() => lf.remove(), wait + 750);
          } else setTimeout(() => lf.remove(), dur + 50);
        });
        later(1600, () => blinkOut("rock", "dust"));
        later(2400, () => finish("The Greatness bot escaped on an autumn breeze 🍂"));
      } else if (kind === "tomato" || kind === "poop") {
        // Tomatoes (and the ultra-rare poop barrage): thrown at the screen — they splat and slowly slide down
        const poop = kind === "poop";
        later(600, () => charge.remove());
        const N = poop ? 16 : 12;
        for (let i = 0; i < N; i++) later(600 + i * 120, () => {
          const a = Math.random() * Math.PI * 2, r = Math.sqrt(Math.random()), x = tx + Math.cos(a) * r * D * .5, y = ty + Math.sin(a) * r * D * .36;
          const b = fxEl(poop ? "gb-throw poop" : "gb-throw tomato", `left:${ox}px;top:${oy}px`); b.textContent = poop ? "💩" : "🍅";
          const dur = 420 + Math.random() * 140, my = Math.min(oy, y) - 80;
          b.animate([{ transform: "translate(-50%,-50%) scale(.6) rotate(0deg)" }, { transform: `translate(calc(-50% + ${(x - ox) / 2}px), calc(-50% + ${my - oy}px)) scale(1.1) rotate(200deg)`, offset: .5 },
            { transform: `translate(calc(-50% + ${x - ox}px), calc(-50% + ${y - oy}px)) scale(1.8) rotate(400deg)` }], { duration: dur, easing: "ease-in", fill: "forwards" });
          setTimeout(() => {
            b.remove(); if (ended) return;
            const size = 60 + Math.random() * 60;
            mud(x, y, size, poop ? "poop" : "tomato");
            burst(x, y, 8, 50, poop ? "dust" : "fire", 500);
            if (poop) {
              // flies buzzing around it
              for (let f = 0; f < 4 + Math.floor(Math.random() * 3); f++) {
                const fly = h("i", { class: "gb-fx gb-fly", style: `left:${x}px;top:${y}px` }, h("b"), h("b"));
                document.body.append(fly);
                const R = size * (.4 + Math.random() * .4), ph = Math.random() * 6.3;
                const k = Array.from({ length: 9 }, (_, j) => ({ transform: `translate(${Math.cos(ph + j * 1.3) * R + (Math.random() - .5) * 14}px, ${Math.sin(ph + j * 1.9) * R * .7 - 12 + (Math.random() - .5) * 14}px)` }));
                fly.animate(k, { duration: 900 + Math.random() * 500, iterations: Infinity, direction: "alternate", easing: "ease-in-out" });
                setTimeout(() => fly.remove(), 4800);
              }
              // a worm crawling out of it
              if (Math.random() < .7) {
                const wx = x + (Math.random() - .5) * size * .4, wy = y + (Math.random() - .5) * size * .3, dir = Math.random() < .5 ? -1 : 1;
                const worm = h("div", { class: "gb-fx gb-worm", style: `left:${wx}px;top:${wy}px;${dir < 0 ? "scale:-1 1" : ""}` }, ...Array.from({ length: 6 }, (_, i) => h("i", { style: `animation-delay:${-i * .08}s` })));
                document.body.append(worm);
                worm.animate([{ translate: "0 0", opacity: 0 }, { opacity: 1, offset: .1 }, { translate: `${dir * size * .7}px ${size * .15}px`, opacity: 1, offset: .85 }, { translate: `${dir * size * .8}px ${size * .2}px`, opacity: 0 }],
                  { duration: 4600, easing: "linear", fill: "forwards" });
                setTimeout(() => worm.remove(), 4700);
              }
              // stink lines
              for (let w = 0; w < 3; w++) {
                const st = h("i", { class: "gb-fx gb-stink", style: `left:${x + (w - 1) * size * .22}px;top:${y - size * .35}px;animation-delay:${w * .25}s` });
                document.body.append(st); setTimeout(() => st.remove(), 4000);
              }
            }
          }, dur);
        });
        later(600 + N * 120 + 500, () => blinkOut(poop ? "rock" : "laser", poop ? "dust" : "fire"));
        later(600 + N * 120 + 1100, () => finish(poop ? "💩 ULTRA RARE! The Greatness bot escaped under a… poop barrage" : "The Greatness bot escaped — tough crowd 🍅"));
      } else {
        // Fireball: charged in the orb, flies to the middle of the page and explodes with a big flash
        later(900, () => {
          charge.remove();
          const ball = fxEl("gb-fireball", `left:${ox}px;top:${oy}px`);
          clean.push(ball);
          const t0 = performance.now(), dur = 650;
          const step = now => {
            if (ended) return;
            const t = Math.min(1, (now - t0) / dur), k = t * t, x = ox + (tx - ox) * k, y = oy + (ty - oy) * k - Math.sin(t * Math.PI) * 60;
            ball.style.transform = `translate(${x}px, ${y}px) translate(-50%,-50%) scale(${1 + t * 1.4})`;
            const s = h("i", { class: "cat-spark fire", style: `left:${x + Math.random() * 16 - 8}px;top:${y + Math.random() * 16 - 8}px` });
            document.body.append(s); setTimeout(() => s.remove(), 800);
            if (t < 1) requestAnimationFrame(step); else boom();
          };
          ball.style.left = ball.style.top = "0px";
          requestAnimationFrame(step);
        });
        const boom = () => {
          clean.forEach(n => n.remove());
          const flash = fxEl("gb-flash", `background: radial-gradient(circle at ${tx}px ${ty}px, rgba(255,252,235,.97) 0, rgba(255,190,90,.75) ${D * .2}px, rgba(255,90,30,.35) ${D * .45}px, rgba(255,60,20,0) ${D * .8}px)`);
          flash.animate([{ opacity: 0 }, { opacity: 1, offset: .12 }, { opacity: 0 }], { duration: 1100, easing: "ease-out", fill: "forwards" });
          const core = fxEl("gb-boom", `left:${tx}px;top:${ty}px;width:${D * .7}px;height:${D * .7}px`);
          core.animate([{ transform: "translate(-50%,-50%) scale(.05)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(1)", opacity: .9, offset: .35 }, { transform: "translate(-50%,-50%) scale(1.15)", opacity: 0 }],
            { duration: 1100, easing: "ease-out", fill: "forwards" });
          const ring = fxEl("gb-ring fire", `left:${tx}px;top:${ty}px`);
          ring.animate([{ transform: "translate(-50%,-50%) scale(0)", opacity: 1 }, { transform: "translate(-50%,-50%) scale(14)", opacity: 0 }], { duration: 1000, easing: "cubic-bezier(.1,.6,.3,1)", fill: "forwards" });
          burst(tx, ty, 40, D * .55, "fire", 1100); cracks(tx, ty, D * .55, "fire");
          vanishFlash();   // vanishes in the flash
          setTimeout(() => { [flash, core, ring].forEach(n => n.remove()); }, 1250);
          later(1300, () => finish("The Greatness bot escaped with a bang 💥"));
        };
      }
      };
      kinds.forEach((k, i) => i ? later(i * 450, () => run(k)) : run(k));
    }
    el.addEventListener("click", () => {
      if (casting) return;
      stopWatch(); clearInterval(trail);
      const r = box(), cx = r.left + r.width / 2, cy = r.top + r.height / 2;
      for (let i = 0; i < 16; i++) {
        const a = i / 16 * Math.PI * 2, d = 40 + Math.random() * 40;
        const s = h("i", { class: `cat-spark big${i % 3 === 0 ? " blue" : ""}`, style: `left:${cx}px;top:${cy}px` });
        document.body.append(s);
        s.animate([{ transform: "translate(0,0) scale(1)", opacity: 1 }, { transform: `translate(${Math.cos(a) * d}px, ${Math.sin(a) * d}px) scale(.2)`, opacity: 0 }], { duration: 700, easing: "ease-out" });
        setTimeout(() => s.remove(), 720);
      }
      caughtIt = true;
      run = Number(sess.get(runKey) || 0) + 1; sess.set(runKey, String(run));
      if (!golden) sess.set(streakKey, String(Number(sess.get(streakKey) || 0) + 1));   // a catch streak for the golden robot
      if (!rainbow) sess.set(rbKey, String(Number(sess.get(rbKey) || 0) + 1));          // …and for the rainbow one
      // the streak pops up right where it was caught: a big number that floats up; gold once you're in the golden zone
      {
        const r = box(), zone = Number(sess.get(streakKey) || 0) >= 10;
        const pop = h("div", { class: `gb-fx gb-streak${zone ? " zone" : run >= 5 ? " hot" : ""}`, style: `left:${r.left + r.width / 2}px;top:${r.top + r.height * .35}px` },
          h("b", {}, `🔥 ${run}`), zone ? h("span", {}, "✨ GOLDEN ZONE") : run >= 5 ? h("span", {}, "streak") : null);
        document.body.append(pop);
        pop.animate([{ transform: "translate(-50%,-50%) scale(.3)", opacity: 0 }, { transform: "translate(-50%,-50%) scale(1.25)", opacity: 1, offset: .12 },
          { transform: "translate(-50%,-60%) scale(1)", opacity: 1, offset: .55 }, { transform: "translate(-50%,-140%) scale(.95)", opacity: 0 }], { duration: 2200, easing: "ease-out", fill: "forwards" });
        setTimeout(() => pop.remove(), 2250);
      }
      // The count lives on the server (same as Achievements); catches this browser counted before are handed over once
      let legacy = null;
      try { if (!localStorage.getItem("gb.mascotsLegacySent")) { legacy = Number(localStorage.getItem("gb.mascotsCaught") || localStorage.getItem("gb.catsCaught") || 0) || null; localStorage.setItem("gb.mascotsLegacySent", "1"); } } catch { /* private mode */ }
      // Caught — but sometimes it strikes back with a spell as it vanishes (the owner's summon always does)
      const spell = forced ? finale : rollSpell(true);
      if (spell) {
        cast(spell);
        report({ kind: "caught", quick: !tired, spells: castKinds, legendary: castLegend, legacy, golden, rainbow });
        return;
      }
      el.classList.add("caught");
      const line = M.lines[Math.floor(Math.random() * M.lines.length)];
      report({ kind: "caught", quick: !tired, legacy, golden, rainbow }).then(r => toast(`${line}${r && r.caught > 1 ? ` (caught: ${r.caught})` : ""}`));
      setTimeout(end, 650);
    }, { once: true });
  }
  // Frequency comes from Settings (per server): on average every N minutes, 0 = off
  function schedule(first) {
    clearTimeout(timer);
    const g = state.guilds && state.guilds.find(x => x.id === state.guildId);
    const avg = g && g.mascotSeconds != null ? g.mascotSeconds : 900;   // seconds, 0 = off
    if (!avg) { timer = setTimeout(() => schedule(first), 60000); return; }
    timer = setTimeout(fly, Math.max(5, (first ? avg * .5 : avg) * (0.6 + Math.random() * 0.8)) * 1000);
  }
  window.__neonCat = fly;
  window.__mascotFinale = kind => fly({ finale: kind });   // owner summon / test hook: a spell key or "random"
  window.__neonCatReschedule = () => schedule(false);
  window.__mascotStart = () => schedule(true);   // started from boot(), once the server's frequency is known
})();

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

  // Tab icon = the bot's avatar straight from Discord (falls back to /favicon.png); a new URL also beats a cached "no icon"
  if (state.bot && state.bot.avatarUrl) {
    const link = document.querySelector('link[rel="icon"]');
    if (link) { link.removeAttribute("type"); link.href = state.bot.avatarUrl; }
  }
  const saved = store.get("gb.guild");
  state.guildId = state.guilds.some(g => g.id === saved) ? saved : (state.guilds[0] && state.guilds[0].id) || null;
  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = (visibleTabs()[0] || ["overview"])[0];
  window.__mascotStart && window.__mascotStart();
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
  mediaLib.items = []; mediaLib.map = new Map(); mediaLib.loaded = null;
  if (id && MEDIA_PERMS.some(can)) loadMedia().catch(() => {});
  renderShell();
}

// ----- Sidebar helpers: remembered scroll, "needs attention" counters on items
let navScrollTop = 0;
const navBadges = { at: 0, data: {}, timer: 0, guild: null };
/** What a counter means and what to do — shown on hover over the pink badge. */
function navBadgeTip(key) {
  const a = navBadges.raw;
  if (!a) return "";
  const list = (items, fmt, max = 4) => items.slice(0, max).map(x => "• " + fmt(x)).join("\n") + (items.length > max ? `\n…and ${items.length - max} more` : "");
  if (key === "tickets") return `${a.tickets.length} ticket(s) wait for a decision:\n` +
    list(a.tickets, t => `${t.member.name} — ${t.project} (opened ${fmtRelative(t.openedAt)})`) + "\n\nOpen Tickets → Approve or Deny.";
  if (key === "lfg") return `${a.groups.length} group(s) start within 3 hours and aren't full:\n` +
    list(a.groups, g => `${g.activity} — ${g.filled}/${g.size}, starts ${fmtRelative(g.startsAt)}${g.reserve ? `, ${g.reserve} in reserve` : ""}`) +
    "\n\nPing people, move someone from the reserve or change the time.";
  if (key === "birthdays") return `Birthday today: ${a.birthdays.map(m => m.name).join(", ")}\n\nThe greeting is posted at the set time — check it's turned on in Birthdays → Greeting post.`;
  if (key === "announce") return `${a.failedPosts.length} post(s) failed in the last 3 days:\n` +
    list(a.failedPosts, f => `#${f.channel || "unknown"} — ${f.error || "error"}`) + "\n\nUsually the bot lacks permissions in that channel. Fix them and send again from Announcements.";
  return "";
}

function applyNavBadges() {
  for (const el of document.querySelectorAll(".nav-item[data-key]")) {
    const n = navBadges.data[el.dataset.key] || 0;
    const b = el.querySelector(".nav-badge");
    if (b) {
      b.textContent = n ? String(n > 99 ? "99+" : n) : ""; b.classList.toggle("on", !!n);
      if (n) b.dataset.tip = navBadgeTip(el.dataset.key); else delete b.dataset.tip;
    }
  }
  // A folded group shows the sum of its counters instead of its page count, so waiting work isn't hidden
  for (const g of document.querySelectorAll(".nav-group")) {
    const sum = [...g.querySelectorAll(".nav-item[data-key]")].reduce((t, el) => t + (navBadges.data[el.dataset.key] || 0), 0);
    g.classList.toggle("has-badge", sum > 0);
    // A folded group shows only what waits for you (no page count); hover explains it
    const c = g.querySelector(".nav-count");
    if (c) {
      c.textContent = sum > 0 ? String(sum) : "";
      const tips = [...g.querySelectorAll(".nav-item[data-key]")].filter(el => navBadges.data[el.dataset.key]).map(el => navBadgeTip(el.dataset.key));
      if (sum > 0) c.dataset.tip = tips.join("\n\n"); else delete c.dataset.tip;
    }
  }
}
async function loadNavBadges() {
  if (!isAdmin() || !state.guildId || state.me.viewAsMember) { navBadges.data = {}; applyNavBadges(); return; }
  try {
    const a = await gApi("GET", "/attention");
    navBadges.data = { tickets: a.tickets.length, lfg: a.groups.length, birthdays: a.birthdays.length, announce: a.failedPosts.length };
    navBadges.raw = a;
    navBadges.at = Date.now(); navBadges.guild = state.guildId;
  } catch { /* badges are optional */ }
  applyNavBadges();
}
// Icon-only sidebar: the page name (and its counter) flies out next to the hovered icon
(function navFlyout() {
  const fly = h("div", { class: "nav-flyout", "aria-hidden": "true" });
  document.body.append(fly);
  document.addEventListener("pointerover", e => {
    const it = e.target.closest && e.target.closest(".layout.collapsed .nav .nav-item, .layout.collapsed .nav .nav-section");
    if (!it || innerWidth <= 900) { fly.classList.remove("on"); return; }
    const r = it.getBoundingClientRect();
    const label = it.dataset.label || it.getAttribute("aria-label") || (it.querySelector(".nav-section-text") || {}).textContent || "";
    const n = it.dataset.key ? navBadges.data[it.dataset.key] : 0;
    fly.replaceChildren(label, n ? h("span", { class: "nav-badge" }, String(n)) : "");
    fly.style.left = `${r.right + 12}px`; fly.style.top = `${r.top + r.height / 2}px`;
    fly.classList.add("on");
  });
  document.addEventListener("pointerout", e => { if (!e.relatedTarget || !e.relatedTarget.closest || !e.relatedTarget.closest(".layout.collapsed .nav")) fly.classList.remove("on"); });
})();

function scheduleNavBadges() {
  if (Date.now() - navBadges.at > 60000 || navBadges.guild !== state.guildId) loadNavBadges();
  clearInterval(navBadges.timer);
  navBadges.timer = setInterval(() => { if (!document.hidden) loadNavBadges(); }, 120000);
}

function renderShell() {
  pending.items = [];
  pending.deferred = false;
  if (pending.bar) drawPendingBar();
  const connected = state.connection === "Connected";
  if (!visibleTabs().some(t => t[0] === state.tab)) state.tab = (visibleTabs()[0] || ["overview"])[0];
  const collapsed = store.get("gb.navCollapsed") === "1";

  // ----- Sidebar: sections, Lucide icons, active route; collapses to icons on desktop, drawer on mobile
  const layout = h("div", { class: `layout${collapsed ? " collapsed" : ""}` });
  const closeDrawer = () => layout.classList.remove("drawer-open");
  const NAV_TONES = { voicenow: "teal", birthdays: "pink", pages: "blue", home: "violet", play: "violet", chars: "cyan", schedule: "teal", roster: "green", lfg: "violet", guild: "teal", tickets: "amber", voice: "teal", welcome: "pink", roles: "green", content: "blue",
    announce: "pink", overview: "green", log: "blue", settings: "cyan", access: "amber" };
  const navButton = ([key, ic, label]) => h("button", {
    // Tooltip only when the menu is collapsed to icons (labels are visible otherwise)
    class: `nav-item tone-${NAV_TONES[key] || "violet"}${state.tab === key ? " active" : ""}`, "data-key": key, "data-label": label, "aria-label": label,
    "aria-current": state.tab === key ? "page" : null,
    onclick: async () => {
      if (!await leavePage()) return;
      // Going to a page of another group: that group opens, all the others fold
      const from = (allTabs.find(t => t[0] === state.tab) || [])[4], to = (allTabs.find(t => t[0] === key) || [])[4];
      if (to && from !== to) store.set("gb.navFold", JSON.stringify(sections.map(sc => sc.name).filter(n => n !== to)));
      state.tab = key; store.set("gb.tab", key); closeDrawer(); renderShell();
    }
  }, icon(ic, 18), h("span", { class: "nav-label" }, label), h("span", { class: "nav-badge" }));
  // Groups fold by a click on their title (remembered); "Owner tools" is folded by default; the active tab's group stays open
  const folded = (() => { try { return new Set(JSON.parse(store.get("gb.navFold") || '["Owner tools"]')); } catch { return new Set(["Owner tools"]); } })();
  const navItems = [];
  const sections = [];
  for (const t of visibleTabs()) {
    let sec = sections[sections.length - 1];
    if (!sec || sec.name !== t[4]) { sec = { name: t[4], tabs: [] }; sections.push(sec); }
    sec.tabs.push(t);
  }
  // Each group has its own icon + tone: a chip in the header, the open tab's group sits in a tinted panel,
  // folded groups show how many pages they hide
  const SECTION_LOOK = { "Member": ["user-round", "violet"], "Games": ["gamepad-2", "teal"], "Community": ["users", "green"],
    "Posts & channels": ["megaphone", "blue"], "Administration": ["settings", "cyan"], "Owner tools": ["crown", "amber"] };
  for (const sec of sections) {
    const active = sec.tabs.some(t => t[0] === state.tab);
    const [secIcon, secTone] = SECTION_LOOK[sec.name] || ["layers", "violet"];
    const group = h("div", { class: `nav-group tone-${secTone}${active ? " current" : ""}${folded.has(sec.name) && !active ? " folded" : ""}` });
    const head = h("button", { class: "nav-section", type: "button", "aria-expanded": String(!group.classList.contains("folded")),
      title: collapsed || active ? null : sec.tabs.map(t => t[2]).join(" · "), "aria-label": sec.name,
      onclick: () => {
        if (active) return; // the open tab's group can't fold
        const isFolded = group.classList.toggle("folded");
        head.setAttribute("aria-expanded", String(!isFolded));
        if (isFolded) folded.add(sec.name); else folded.delete(sec.name);
        store.set("gb.navFold", JSON.stringify([...folded]));
      } },
      h("span", { class: "nav-chip" }, icon(secIcon, 14)),
      h("span", { class: "nav-section-text" }, sec.name),
      active ? h("span", { class: "nav-here", "aria-label": "Current section" })
        : [h("span", { class: "nav-count" }, String(sec.tabs.length)), icon("chevron-down", 14, "nav-fold")]);
    // Items sit in an inner box so folding can animate the height (grid 1fr ↔ 0fr)
    group.append(head, h("div", { class: "nav-group-items" }, h("div", { class: "nav-group-inner" }, sec.tabs.map(navButton))));
    navItems.push(group);
  }
  const collapseBtn = h("button", { class: "nav-item nav-collapse", title: collapsed ? "Expand menu" : "Collapse menu",
    onclick: async () => { if (!await leavePage()) return; store.set("gb.navCollapsed", collapsed ? "0" : "1"); renderShell(); } },
    icon(collapsed ? "panel-left-open" : "panel-left-close", 18), h("span", { class: "nav-label" }, "Collapse"));
  const nav = h("nav", { class: "nav", "aria-label": "Sections" }, navItems, h("div", { class: "nav-footer" }, collapseBtn));
  // Keep the sidebar where it was scrolled (the shell is rebuilt on every page switch) and keep the active page in view
  nav.addEventListener("scroll", () => { navScrollTop = nav.scrollTop; }, { passive: true });
  requestAnimationFrame(() => {
    nav.scrollTop = navScrollTop;
    const act = nav.querySelector(".nav-item.active");
    if (act) { const r = act.getBoundingClientRect(), n = nav.getBoundingClientRect(); if (r.top < n.top + 8 || r.bottom > n.bottom - 70) act.scrollIntoView({ block: "center" }); }
    applyNavBadges();
  });
  scheduleNavBadges();
  const main = h("main", { class: "main" });
  layout.append(nav, h("div", { class: "nav-backdrop", onclick: closeDrawer }), main);

  // ----- Header: menu (mobile), brand, server, bot status, account menu
  const guildSel = h("select", { "aria-label": "Server", onchange: async e => { if (await leavePage()) selectGuild(e.target.value); else e.target.value = state.guildId; } },
    state.guilds.map(g => h("option", { value: g.id, selected: g.id === state.guildId }, g.name)));
  // Owner: switch between the full view and the view of a normal member
  const viewAs = state.me.isOwner ? (() => {
    const sw = h("input", { type: "checkbox", class: "switch", checked: !!state.me.viewAsMember, "aria-label": "View as member" });
    sw.addEventListener("change", () => { store.set("gb.viewAs", sw.checked ? "member" : ""); state.tab = "home"; store.set("gb.tab", "home"); boot(); });
    return h("label", { class: "menu-item view-as" }, icon("eye", 16), h("span", { style: "flex:1" }, "View as member"), sw);
  })() : null;
  // Which Discord server the panel works with: only the bot owner switches it (in this menu)
  const g0 = currentGuild();
  const serverPick = state.me.isOwner && state.guilds.length > 1
    ? h("div", { class: "menu-server" }, h("span", { class: "menu-label" }, icon("server", 14), "Server"), h("div", { class: "server-select" }, guildSel)) : null;
  const userPop = h("div", { class: "user-pop", style: "display:none" },
    h("div", { class: "who" }, h("b", {}, state.me.name),
      h("span", { class: "muted small" }, state.me.viewAsMember ? "Owner · viewing as member" : state.me.isOwner ? "Owner" : isAdmin() ? "Admin access" : "Member"),
      g0 && !serverPick ? h("span", { class: "muted small who-server" }, icon("server", 12), g0.name) : null),
    serverPick,
    viewAs,
    h("button", { class: "menu-item danger", onclick: logout }, icon("log-out", 16), "Log out"));
  const userBtn = h("button", { class: "user-button", "aria-haspopup": "menu", "aria-expanded": "false" },
    state.me.avatarUrl ? h("img", { class: "avatar", src: state.me.avatarUrl, alt: "" }) : h("span", { class: "avatar" }),
    h("span", { class: "name" }, state.me.name), icon("chevron-down", 16, "muted"));
  const toggleUser = open => {
    userPop.style.display = open ? "" : "none";
    userBtn.setAttribute("aria-expanded", open ? "true" : "false");
  };
  userBtn.addEventListener("click", e => { e.stopPropagation(); toggleUser(userPop.style.display === "none"); });
  document.onclick = e => { if (!userPop.contains(e.target) && !(e.target.closest && e.target.closest(".select-pop"))) toggleUser(false); };

  document.getElementById("app").replaceChildren(
    h("header", { class: "topbar" },
      h("button", { class: "btn ghost icon-only menu-toggle", title: "Menu", "aria-label": "Menu",
        onclick: () => layout.classList.toggle("drawer-open") }, icon("menu", 20)),
      h("div", { class: "brand" }, h("span", { class: "brand-logo" }, h("img", { src: "/favicon.png", alt: "" })),
        h("span", { class: "brand-text" }, h("span", { class: "brand-name" }, "Greatness Bot"))),
      h("div", { class: "spacer" }),
      h("button", { type: "button", class: "search-trigger", title: "Search (Ctrl+K)", "aria-label": "Search", onclick: () => quickSearch() },
        icon("search", 16), h("span", { class: "search-trigger-text" }, "Search…"), h("kbd", {}, navigator.platform.includes("Mac") ? "⌘K" : "Ctrl K")),
      state.guilds.length ? null : h("span", { class: "muted" }, "The bot is on no servers"),
      h("span", { class: `status-pill ${connected ? "ok" : "bad"}`, title: `Bot: ${state.connection || "unknown"}` },
        h("span", { class: "dot" }), h("span", { class: "label" }, connected ? "Online" : (state.connection || "Offline"))),
      h("div", { class: "user-menu" }, userBtn, userPop)),
    layout);

  if (!state.guildId) { main.append(empty("No servers available for your account.")); return; }
  if (!visibleTabs().length) { main.append(empty("You have no sections on this server. Ask an admin for the member role.")); return; }
  siteTrack(MEMBER_TABS.includes(state.tab) ? "tab" : "visit", state.tab);
  const page = { home: pageHome, content: pageContent, play: pagePlay, chars: pageChars, overview: pageOverview, lfg: pageLfg, guild: pageGuild, schedule: pageSchedule, roster: pageMemberRoster, tickets: pageTickets, voice: pageVoice, welcome: pageWelcome, roles: pageRoles, texts: pageTexts, posts: pagePosts, announce: pageAnnounce, log: pageLog, settings: pageSettings, access: pageAccess, voicenow: pageVoiceNow, birthdays: pageBirthdays, pages: pageChannelPages, media: pageMedia, mascot: pageMascot, achievements: pageAchievements }[state.tab];
  page(main);
  addGuideButton(main, state.tab);
  decorateHeading(main);
}

/** Page heading: the section's icon instead of an emoji; Refresh / Guide with icons. */
function decorateHeading(main) {
  const head = main && main.querySelector(":scope > .card-head");
  if (!head || head.querySelector(":scope > .page-actions") || !head.querySelector(":scope > h1")) return;
  head.classList.add("page-head");
  const title = head.querySelector("h1");
  const tab = allTabs.find(t => t[0] === state.tab);
  if (title && tab) {
    const text = title.textContent.replace(/^[^\p{L}\p{N}]+/u, "").trim();
    title.replaceChildren(icon(tab[1], 24), text);
  }
  // Same order on every page: title (+ ?) · page actions · Refresh · Guide
  let refreshBtn = head.querySelector(".btn-refresh");
  if (!refreshBtn) refreshBtn = h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh });
  refreshBtn.replaceChildren(icon("refresh-cw", 16), h("span", { class: "btn-text" }, "Refresh"));
  const guideBtn = head.querySelector(".btn-guide");
  if (guideBtn) guideBtn.replaceChildren(icon("circle-help", 16), h("span", { class: "btn-text" }, "Guide"));
  const own = [...head.children].filter(el => el !== title && el !== refreshBtn && el !== guideBtn);
  head.append(h("div", { class: "page-actions" }, own, refreshBtn, guideBtn));
}

/** Re-renders the current page only (keeps the shell). */
function refresh() {
  if (pending.saving || pendingCheck().length) { pending.deferred = true; return; }
  renderShell();
}

// ================= Announcements: posts from the bot =================

async function pageAnnounce(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "✍️ Announcements"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));

  // Server / bot emoji, so the preview shows :name: as the real emoji (the post resolves them too when sent)
  const emojiMap = new Map();
  gApi("GET", "/announce/emojis").then(list => { for (const e of list) emojiMap.set(e.name.toLowerCase(), e.code); if (text.value) queueAnn(); }).catch(() => {});

  let editing = null; // scheduled post being edited
  let editingSent = null; // sent post being edited (messages are edited in Discord)
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
      ...ping.roles.map((r, i) => roleChip(r, () => removeLocal(ping.roles, i, drawPing, `@${r.name} won't be pinged.`))),
      ...ping.users.map((m, i) => h("span", { class: "chip" }, `@${m.name}`, h("button", { title: "Remove", onclick: () =>
        removeLocal(ping.users, i, drawPing, `${m.name} won't be pinged.`) }, "×")))];
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
    editingSent = null;
    channel.disabled = false;
    whenBox.style.display = when.value === "later" ? "" : "none";
    title.textContent = "New post";
    cancelEdit.style.display = "none";
    go.textContent = "👁 Preview & send";
    when.disabled = false;
  };
  cancelEdit.addEventListener("click", resetForm);

  // Preview: how the text is split → confirmation → send / schedule
  go.addEventListener("click", async () => {
    if (!channel.value) return toast("Choose a channel first.", "warn");
    const later = !editingSent && (editing || when.value === "later");
    if (later && (!date.value || !time.value)) return toast("Pick the date and time.", "warn");
    const preview = await act(go, () => gApi("POST", "/announce/preview", { content: text.value, ...pingBody() }));
    if (!preview) return;
    const chName = channel.selectedOptions[0].textContent.split("  ·  ")[0];
    const whenText = later ? `on ${date.value} at ${time.value}` : "now";
    const ok = await new Promise(resolve => {
      const d = dialog();
      d.classList.add("wide");
      d.replaceChildren(h("h2", {}, `${editing || editingSent ? "Save" : later ? "Schedule" : "Send"}: ${preview.count} message${preview.count > 1 ? "s" : ""} → ${chName}`),
        h("p", { class: "muted small" }, `${preview.length} characters · ${whenText} · ping: ${[ping.everyone && "@" + ping.everyone, ...ping.roles.map(r => "@" + r.name), ...ping.users.map(u => "@" + u.name)].filter(Boolean).join(", ") || "nobody"}${pings.checked ? " + all mentions in the text" : ""}. `,
          preview.count > 1 ? "The text is longer than Discord allows (2000) — it is split at paragraphs/lines; code blocks are kept whole." : ""),
        h("div", { class: "guide-body" }, preview.blocks.map((b, i) => h("div", { class: "card", style: "padding:10px;margin-bottom:8px" },
          h("div", { class: "muted small", style: "margin-bottom:4px" }, `Message ${i + 1}/${preview.count} · ${b.length} characters`),
          h("div", { style: "white-space:pre-wrap;word-break:break-word" }, renderMentions(b))))),
        h("div", { class: "actions" },
          h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(false); } }, "Cancel"),
          editingSent ? h("p", { class: "muted small" }, "The messages in Discord are edited in place. Editing doesn't ping anyone again. If the text now needs more messages, the extra ones are posted below; fewer — the extra old ones are deleted.") : null,
          h("button", { class: "btn", onclick: () => { d.close(); resolve(true); } }, editing || editingSent ? "💾 Save" : later ? "📅 Schedule" : "✅ Send")));
      d.onclose = () => { d.classList.remove("wide"); resolve(false); };
      d.showModal();
    });
    if (!ok) return;
    const body = { channelId: channel.value, content: text.value, allowMentions: pings.checked,
      date: later ? date.value : null, time: later ? time.value : null, ...pingBody() };
    const res = editingSent
      ? await act(go, () => gApi("PUT", `/announce/${editingSent}/sent`, body), r => r.added ? `Post edited in Discord (+${r.added} message(s) below).` : "Post edited in Discord.")
      : editing
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

  // Live preview of the post (first message — Discord cuts at 2000 characters, the bot splits the rest)
  const annFrame = h("div", { class: "dc-frame" });
  const annNote = h("p", { class: "muted small" });
  let annTimer = 0;
  const drawAnn = () => {
    const line = [ping.everyone ? `@${ping.everyone.replace(/^@/, "")}` : "", ...ping.roles.map(r => `<@&${r.id}>`), ...ping.users.map(u => `<@${u.id}>`)].filter(Boolean).join(" ");
    for (const u of ping.users) knownUsers.set(u.id, u.name);
    const full = resolveEmojiShortcodes([line, text.value].filter(x => x.trim()).join("\n"), emojiMap);
    annFrame.replaceChildren(full.trim() ? dcMessage({ content: full.slice(0, 2000) }) : h("div", { class: "muted small" }, "Write the text to see the post."));
    annNote.textContent = full.length > 2000 ? `First message shown — the bot splits the rest into ~${Math.ceil(full.length / 1900)} messages (see Preview & send).` : "How the post will look in Discord.";
  };
  const queueAnn = () => { clearTimeout(annTimer); annTimer = setTimeout(drawAnn, 200); };
  text.addEventListener("input", queueAnn);
  const annPreview = h("div", { class: "card preview-card" }, h("h3", {}, "Preview"), annFrame, annNote);
  const composer = h("div", { class: "card", style: "margin:0" }, h("div", { class: "card-head" }, title, cancelEdit),
    h("p", { class: "muted small" }, "The bot posts your text in a channel (or a voice channel's chat). Longer than 2000 characters — split into several messages automatically. You see the preview and confirm before anything is sent."),
    h("div", { class: "row", style: "margin-bottom:12px" }, h("div", { class: "grow" }, tplSel), saveTpl),
    h("div", { class: "field" }, h("label", {}, "Channel"), channel),
    h("div", { class: "field" }, h("label", {}, "Text — type @ or # for suggestions"), textBox,
      h("div", { class: "row", style: "gap:8px;margin-top:6px;flex-wrap:wrap" }, mediaTextButton(text),
        h("span", { class: "muted small" }, "Images and GIFs go under the message (paste / drop a picture into the text works too).")), counter, inText),
    h("div", { class: "field" }, h("label", {}, "🔔 Ping (a line with mentions at the top of the post)"), pingChips,
      h("div", { class: "row" }, everyoneSel, addRoles, addUser)),
    h("label", { class: "row", style: "margin-bottom:10px" }, pings, "Ping mentions written in the text (@ suggestions turn this on) — off: they are shown but nobody is notified"),
    h("div", { class: "row", style: "margin-bottom:12px" }, when, whenBox),
    h("div", { class: "row" }, go));
  composer.addEventListener("change", queueAnn);
  composer.addEventListener("click", queueAnn);
  main.append(h("div", { class: "editor-layout", style: "margin-bottom:16px" }, composer, h("div", { class: "editor-preview" }, annPreview)));
  drawAnn();

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
        del.addEventListener("click", () => removeWithUndo(del.closest("tr"), `Template «${t.name}» deleted.`,
          () => gApi("DELETE", `/announce/templates/${t.key}`), refresh));
        return h("tr", {}, h("td", {}, h("b", {}, t.name)),
          h("td", {}, t.channel ? `# ${t.channel}` : h("span", { class: "muted small" }, "any")),
          h("td", { class: "small" }, t.content.length > 90 ? t.content.slice(0, 90) + "…" : t.content),
          h("td", { class: "actions" }, use, " ", rename, " ", del));
      })))) : empty("No templates yet."));
  const schedCard = h("div", { class: "card" }, h("h2", {}, "📅 Scheduled"), h("div", { class: "muted" }, "Loading…"));
  const sentCard = h("div", { class: "card" }, h("h2", {}, "🗂️ Sent"), h("div", { class: "muted" }, "Loading…"));
  main.append(tplCard, schedCard, sentCard);

  let data;
  try { data = await gApi("GET", "/announce"); } catch (e) { schedCard.replaceChildren(h("h2", {}, "📅 Scheduled"), errorState(e.message)); return; }
  const where = p => p.channel ? `${p.voice ? "🔊" : "#"} ${p.channel}` : "deleted channel";
  const short = t => t.length > 90 ? t.slice(0, 90) + "…" : t;
  const loadIntoForm = (p, edit) => {
    channel.value = p.channelId;
    text.value = p.content;
    pings.checked = p.allowMentions;
    setPing(p);
    syncCounter();
    if (edit === "sent") {
      editingSent = p.id;
      title.textContent = "Edit sent post";
      cancelEdit.style.display = "";
      go.textContent = "👁 Preview & save";
      channel.disabled = true; // the messages stay where they are
      when.value = "now";
      when.disabled = true;
      whenBox.style.display = "none";
    } else if (edit) {
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
      // Main actions as buttons, the rest in «⋯»
      const editSent = p.status === "sent" && p.messages
        ? h("button", { class: "btn secondary sm", title: "Change the messages in Discord", onclick: () => loadIntoForm(p, "sent") }, icon("pencil", 14), "Edit") : null;
      const again = h("button", { class: "btn secondary sm", title: "Copy into the form", onclick: () => loadIntoForm(p, false) }, icon("undo-2", 14), "Reuse");
      const more = menuButton([
        p.jumpUrl ? { icon: "external-link", label: "Open in Discord", href: p.jumpUrl } : null,
        p.messages ? { icon: "trash-2", label: "Delete the messages in Discord", danger: true, onclick: async () => {
          if (!await confirmDialog("Delete this post in Discord?", `${p.messages} message(s) in ${where(p)} will be deleted.`, "Delete")) return;
          if (await act(null, () => gApi("POST", `/announce/${p.id}/delete-messages`), r => `Deleted ${r.deleted} message(s).`) !== undefined) refresh();
        } } : null,
        { icon: "x", label: "Remove from this list", onclick: () => removeWithUndo(more.closest("tr"),
          "Removed from the list (the messages in Discord stay).", () => gApi("DELETE", `/announce/${p.id}`), refresh) }
      ].filter(Boolean));
      return h("tr", {},
        h("td", {}, fmtDate(p.sentAt), p.sendAt ? h("div", { class: "muted small" }, "scheduled") : null),
        h("td", {}, where(p)),
        h("td", { class: "small" }, short(p.content), h("div", { class: "muted small" }, "by ", member(p.createdBy))),
        h("td", {}, p.status === "sent" ? h("span", { class: "badge ok" }, `${p.messages} msg`) : h("span", { class: "badge lock", title: p.error || "" }, "failed"),
          p.error ? h("div", { class: "error-text small" }, p.error) : null),
        h("td", { class: "actions icon-actions" }, editSent, again, more));
    })))) : empty("Nothing sent yet."));
}

// ================= Overview =================

// ================= Quick search (Ctrl+K): pages, members, groups, tickets =================

document.addEventListener("keydown", e => {
  if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "k" && state.me) { e.preventDefault(); quickSearch(); }
});
const searchCache = { at: 0, guild: null, groups: [], tickets: [] };

async function quickSearch() {
  siteTrack("search");
  const d = dialog();
  if (d.open && d.classList.contains("qs-dialog")) return;
  d.classList.remove("member-card-dialog", "wide", "xwide", "page-dialog");
  d.classList.add("qs-dialog");
  d.onclose = () => d.classList.remove("qs-dialog");
  const input = h("input", { type: "search", placeholder: "Search pages, members, groups, tickets…", autocomplete: "off", "aria-label": "Search" });
  const list = h("div", { class: "qs-list", role: "listbox" });
  d.replaceChildren(h("div", { class: "qs-input" }, icon("search", 18), input, h("kbd", {}, "Esc")), list);
  d.showModal(); input.focus();

  const goTab = async (key, before) => { d.close(); if (!await leavePage()) return; if (before) before(); state.tab = key; store.set("gb.tab", key); renderShell(); };
  let members = [], active = 0, timer = 0, items = [];
  const norm = t => String(t || "").toLowerCase();
  function results(q) {
    const has = (...xs) => xs.some(x => norm(x).includes(q));
    const out = [];
    for (const [key, ic, label, , section] of visibleTabs())
      if (!q || has(label, section, key)) out.push({ ic, title: label, sub: section, kind: "Page", run: () => goTab(key) });
    if (q) {
      for (const g of searchCache.groups) {
        const people = g.slots.filter(x => x.member).map(x => x.member.name + " " + (x.character || ""));
        if (has(g.activity, g.owner && g.owner.name, ...people)) out.push({ ic: "swords", title: g.activity, kind: "Group",
          sub: `${g.filled}/${g.size}${g.startsAt ? " · " + fmtDate(g.startsAt) : ""}`, run: () => goTab("lfg", () => { store.set("gb.groupsQuery", q); store.set("gb.lfgSection", "groups"); }) });
      }
      for (const t of searchCache.tickets)
        if (has(t.applicant && t.applicant.name, t.project)) out.push({ ic: "ticket", title: `${t.applicant.name} — ${t.project}`, kind: "Ticket",
          sub: t.status || "open", run: () => goTab("tickets") });
      for (const m of members) out.push({ ic: "user-round", title: m.name, kind: "Member", sub: m.username ? "@" + m.username : "",
        avatar: m.avatarUrl, run: () => { d.close(); memberCard(m.id); } });
    }
    return out.slice(0, 40);
  }
  function draw() {
    if (!d.open) return;
    items = results(norm(input.value.trim()));
    active = Math.min(active, Math.max(items.length - 1, 0));
    list.replaceChildren(...(items.length ? items.map((it, i) => h("button", { type: "button", class: `qs-item${i === active ? " active" : ""}`, role: "option",
      onmouseenter: () => { active = i; mark(); }, onclick: () => it.run() },
      it.avatar ? h("img", { class: "avatar sm", src: it.avatar, alt: "" }) : icon(it.ic, 16),
      h("span", { class: "qs-title" }, it.title), it.sub ? h("span", { class: "muted small qs-sub" }, it.sub) : null,
      h("span", { class: "qs-kind" }, it.kind))) : [h("div", { class: "muted small qs-empty" }, "Nothing found.")]));
  }
  const mark = () => [...list.children].forEach((el, i) => el.classList.toggle("active", i === active));
  input.addEventListener("input", () => {
    active = 0; draw();
    clearTimeout(timer);
    const q = input.value.trim();
    if (q.length < 2 || !isAdmin()) { members = []; return; }
    timer = setTimeout(async () => { members = await gApi("GET", `/members?q=${encodeURIComponent(q)}&scope=search`).catch(() => []); if (input.value.trim() === q) draw(); }, 220);
  });
  input.addEventListener("keydown", e => {
    if (e.key === "ArrowDown") { e.preventDefault(); active = Math.min(active + 1, items.length - 1); mark(); list.children[active]?.scrollIntoView({ block: "nearest" }); }
    else if (e.key === "ArrowUp") { e.preventDefault(); active = Math.max(active - 1, 0); mark(); list.children[active]?.scrollIntoView({ block: "nearest" }); }
    else if (e.key === "Enter") { e.preventDefault(); items[active]?.run(); }
  });
  draw();
  if (Date.now() - searchCache.at > 60000 || searchCache.guild !== state.guildId) {
    searchCache.at = Date.now(); searchCache.guild = state.guildId;
    const [groups, tickets] = await Promise.all([
      can("lfg") ? gApi("GET", "/lfg/groups").catch(() => []) : [],
      can("tickets") ? gApi("GET", "/tickets/open").catch(() => []) : []]);
    searchCache.groups = groups; searchCache.tickets = tickets;
    draw();
  }
}

/** «Needs attention»: what an admin should look at now — each line leads to its page. */
async function attentionCard(holder, go) {
  let a;
  try { a = await gApi("GET", "/attention"); } catch { return; }
  const tickets = can("tickets") ? a.tickets : [], groups = can("lfg") ? a.groups : [];
  const birthdays = can("birthdays") ? a.birthdays : [], failed = can("announce") ? a.failedPosts : [];
  const total = tickets.length + groups.length + birthdays.length + failed.length;
  const row = (ic, tone, title, sub, tab, extra) => h("button", { type: "button", class: `att-row tone-${tone}`, onclick: go(tab) || undefined, disabled: !go(tab) },
    h("span", { class: "att-ic" }, icon(ic, 16)), h("span", { class: "att-main" }, h("b", {}, title), sub ? h("span", { class: "muted small" }, sub) : null),
    extra || null, go(tab) ? icon("chevron-right", 16, "att-go") : null);
  const items = [
    ...tickets.slice(0, 4).map(t => row("ticket", "amber", `${t.member.name} — ${t.project}`, `ticket waiting for a decision · opened ${fmtRelative(t.openedAt)}`, "tickets")),
    tickets.length > 4 ? row("ticket", "amber", `+${tickets.length - 4} more open ticket(s)`, null, "tickets") : null,
    ...groups.map(g => row("swords", "violet", `${g.activity} — ${g.filled}/${g.size}`, `starts ${fmtRelative(g.startsAt)}${g.reserve ? ` · ${g.reserve} in reserve` : ""}${g.owner ? ` · ${g.owner.name}` : ""}`, "lfg")),
    birthdays.length ? row("cake", "pink", `Birthday today: ${birthdays.map(m => m.name).join(", ")}`, null, "birthdays") : null,
    ...failed.map(f => row("triangle-alert", "red", `Post to #${f.channel || "unknown"} failed`, f.error || fmtRelative(f.at), "announce"))
  ].filter(Boolean);
  holder.replaceChildren(h("div", { class: `card attention-card${total ? "" : " calm"}` },
    h("div", { class: "card-head" }, h("h2", {}, icon(total ? "bell-ring" : "circle-check", 18), total ? "Needs attention" : "All clear"),
      total ? h("span", { class: "badge wait" }, String(total)) : null),
    total ? h("div", { class: "att-list" }, items) : h("p", { class: "muted small", style: "margin:0" }, "No waiting tickets, no groups short of people in the next 3 hours, no failed posts.")));
}

async function pageOverview(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Dashboard"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const go = tab => can(tab) ? () => { state.tab = tab; store.set("gb.tab", tab); renderShell(); } : null;
  const attention = h("div", {});
  attentionCard(attention, go);
  const PERIODS = [[30, "30 days"], [90, "90 days"], [180, "6 months"], [365, "Year"], [0, "All time"]];
  let days = Number(store.get("gb.dashDays") || 90);
  if (!PERIODS.some(p => p[0] === days)) days = 90;
  const segBtns = PERIODS.map(([d, label]) => h("button", { type: "button", class: `seg-btn${d === days ? " active" : ""}`, onclick: () => {
    days = d; store.set("gb.dashDays", String(d)); segBtns.forEach((b, i) => b.classList.toggle("active", PERIODS[i][0] === d)); load(); } }, label));
  const body = h("div", {}, h("div", { class: "stats" }, [0, 1, 2, 3].map(() => skeletonStat())));
  const connected = state.connection === "Connected";
  const footer = h("div", {},
    sectionLabel("Bot"),
    h("div", { class: "card" }, cardHead("bot", "Bot status"),
      h("div", { class: "row" },
        state.me.viewAsMember ? h("button", { class: "view-as-pill", title: "Back to my own view",
          onclick: () => { store.set("gb.viewAs", ""); boot(); } }, icon("eye", 14), "Member view", icon("x", 13)) : null,
        h("span", { class: `status-pill ${connected ? "ok" : "bad"}` }, h("span", { class: "dot" }), connected ? "Connected to Discord" : (state.connection || "Offline")),
        state.bot ? h("span", { class: "chip" }, icon("bot", 13), state.bot.name) : null)),
    sectionLabel("Sections"),
    h("div", { class: "quick-actions" }, allTabs.filter(t => t[3] !== "member" && t[0] !== "overview" && can(t[3])).map(([key, ic, label]) =>
      h("button", { class: `quick-action ${toneOf(ic)}`, onclick: async () => { if (!await leavePage()) return; state.tab = key; store.set("gb.tab", key); renderShell(); } },
        h("span", { class: "qa-icon" }, icon(ic, 18)), h("span", { class: "qa-text" }, h("b", {}, label), h("span", { class: "muted small" }, "Open"))))));
  main.append(attention, h("div", { class: "dash-bar" }, h("div", { class: "seg dash-seg" }, segBtns), h("span", { class: "muted small dash-since" })), body, footer);

  async function load() {
    body.style.opacity = ".55";
    let d;
    try { d = await gApi("GET", `/dashboard?days=${days}`); } catch (e) { body.replaceChildren(errorState(e.message)); body.style.opacity = ""; return; }
    body.style.opacity = "";
    renderDashboard(body, d, go);
  }
  load();
}

// ----- Dashboard: charts (plain SVG, one axis, hover tooltips) -----

const DASH = { a: "#4A90E8", b: "#1FA06C", c: "#8A6CF0", d: "#C2841A" }; // validated for the dark surface
const fmtBucket = (key, monthly) => {
  const [y, m, dd] = key.split("-").map(Number);
  const dt = new Date(y, m - 1, dd || 1);
  return monthly ? dt.toLocaleDateString(undefined, { month: "short", year: "2-digit" }) : dt.toLocaleDateString(undefined, { day: "numeric", month: "short" });
};
let chartTipEl = null;
function chartTip(e, html) {
  if (!chartTipEl) { chartTipEl = h("div", { class: "chart-tip" }); document.body.append(chartTipEl); }
  if (!html) { chartTipEl.style.display = "none"; return; }
  chartTipEl.replaceChildren(...html);
  chartTipEl.style.display = "block";
  const r = chartTipEl.getBoundingClientRect();
  chartTipEl.style.left = Math.min(e.clientX + 14, innerWidth - r.width - 8) + "px";
  chartTipEl.style.top = Math.max(e.clientY - r.height - 10, 8) + "px";
}
const svgEl = (tag, attrs) => { const n = document.createElementNS("http://www.w3.org/2000/svg", tag); for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, v); return n; };
const niceMax = v => { if (v <= 5) return 5; const p = Math.pow(10, Math.floor(Math.log10(v))); return Math.ceil(v / p * 2) / 2 * p; };

/** Bars per period bucket; series = [{key, label, color}], rows = [{key(bucket), [series.key]: n}] */
function barChart(rows, series, monthly, height = 180, W = 640) {
  const H = height, L = 34, B = 22, T = 8;
  const max = niceMax(Math.max(1, ...rows.flatMap(r => series.map(s => r[s.key] || 0))));
  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, class: "chart", width: W, height: H });
  for (let i = 0; i <= 4; i++) {
    const y = T + (H - T - B) * (1 - i / 4);
    svg.append(svgEl("line", { x1: L, x2: W, y1: y, y2: y, class: "grid" }));
    const t = svgEl("text", { x: L - 6, y: y + 4, class: "axis", "text-anchor": "end" }); t.textContent = Math.round(max * i / 4); svg.append(t);
  }
  const slot = (W - L) / Math.max(rows.length, 1), gap = 2;
  const bw = Math.max(2, Math.min(26, (slot * .72 - gap * (series.length - 1)) / series.length));
  const every = Math.ceil(rows.length / Math.max(2, Math.floor((W - L) / 70)));
  rows.forEach((r, i) => {
    const x0 = L + slot * i + (slot - (bw * series.length + gap * (series.length - 1))) / 2;
    series.forEach((s, j) => {
      const v = r[s.key] || 0, hh = (H - T - B) * v / max;
      if (v > 0) svg.append(svgEl("path", { d: roundTop(x0 + j * (bw + gap), H - B - hh, bw, hh), fill: s.color, class: "bar" }));
    });
    const hit = svgEl("rect", { x: L + slot * i, y: T, width: slot, height: H - T - B, class: "hit" });
    hit.addEventListener("mousemove", e => chartTip(e, [h("b", {}, fmtBucket(r.key, monthly)),
      ...series.map(s => h("div", { class: "tip-row" }, h("i", { style: `background:${s.color}` }), s.label, h("b", {}, String(r[s.key] || 0))))]));
    hit.addEventListener("mouseleave", () => chartTip());
    svg.append(hit);
    if (i % every === 0) { const t = svgEl("text", { x: L + slot * i + slot / 2, y: H - 6, class: "axis", "text-anchor": "middle" }); t.textContent = fmtBucket(r.key, monthly); svg.append(t); }
  });
  return svg;
}
function roundTop(x, y, w, hh) {
  const r = Math.min(4, w / 2, hh);
  return `M${x},${y + hh}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + hh}Z`;
}

/** Line + soft area for one daily series [{date, value}] */
function lineChart(points, color, label, height = 170, W = 640) {
  const H = height, L = 44, B = 22, T = 10;
  const vals = points.map(p => p.value);
  let min = Math.min(...vals), max = Math.max(...vals);
  const pad = Math.max(1, Math.round((max - min) * .15)); min = Math.max(0, min - pad); max = max + pad;
  const X = i => L + (W - L - 6) * (points.length === 1 ? .5 : i / (points.length - 1));
  const Y = v => T + (H - T - B) * (1 - (v - min) / (max - min || 1));
  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, class: "chart", width: W, height: H });
  for (let i = 0; i <= 3; i++) {
    const v = min + (max - min) * i / 3, y = Y(v);
    svg.append(svgEl("line", { x1: L, x2: W, y1: y, y2: y, class: "grid" }));
    const t = svgEl("text", { x: L - 6, y: y + 4, class: "axis", "text-anchor": "end" }); t.textContent = Math.round(v); svg.append(t);
  }
  const id = "g" + Math.random().toString(36).slice(2, 8);
  const defs = svgEl("defs"); const lg = svgEl("linearGradient", { id, x1: 0, x2: 0, y1: 0, y2: 1 });
  lg.append(svgEl("stop", { offset: "0", "stop-color": color, "stop-opacity": ".28" }), svgEl("stop", { offset: "1", "stop-color": color, "stop-opacity": "0" }));
  defs.append(lg); svg.append(defs);
  const pts = points.map((p, i) => `${X(i).toFixed(1)},${Y(p.value).toFixed(1)}`);
  svg.append(svgEl("path", { d: `M${X(0)},${H - B}L${pts.join("L")}L${X(points.length - 1)},${H - B}Z`, fill: `url(#${id})` }));
  svg.append(svgEl("polyline", { points: pts.join(" "), fill: "none", stroke: color, "stroke-width": 2, "stroke-linejoin": "round", "vector-effect": "non-scaling-stroke" }));
  const dot = svgEl("circle", { r: 4, fill: color, stroke: "#161a28", "stroke-width": 2, style: "display:none" });
  const cross = svgEl("line", { y1: T, y2: H - B, class: "cross", style: "display:none" });
  svg.append(cross, dot);
  const every = Math.ceil(points.length / Math.max(2, Math.floor((W - L) / 80)));
  points.forEach((p, i) => { if ((i % every === 0 && points.length - 1 - i >= every * .6) || i === points.length - 1) { const t = svgEl("text", { x: X(i), y: H - 6, class: "axis", "text-anchor": i === 0 ? "start" : i === points.length - 1 ? "end" : "middle" }); t.textContent = fmtBucket(p.date, false); svg.append(t); } });
  const hit = svgEl("rect", { x: L, y: T, width: W - L, height: H - T - B, class: "hit" });
  hit.addEventListener("mousemove", e => {
    const r = svg.getBoundingClientRect(); const fx = (e.clientX - r.left) / r.width * W;
    const i = Math.max(0, Math.min(points.length - 1, Math.round((fx - L) / (W - L - 6) * (points.length - 1))));
    dot.setAttribute("cx", X(i)); dot.setAttribute("cy", Y(points[i].value)); dot.style.display = ""; cross.setAttribute("x1", X(i)); cross.setAttribute("x2", X(i)); cross.style.display = "";
    chartTip(e, [h("b", {}, new Date(points[i].date).toLocaleDateString()), h("div", { class: "tip-row" }, h("i", { style: `background:${color}` }), label, h("b", {}, String(points[i].value)))]);
  });
  hit.addEventListener("mouseleave", () => { dot.style.display = cross.style.display = "none"; chartTip(); });
  svg.append(hit);
  return svg;
}

/** Draws a chart at the real width of its box and redraws when the box changes size (text never stretches). */
function fitChart(draw) {
  const box = h("div", { class: "chart-box" });
  let last = 0;
  const ro = new ResizeObserver(() => {
    const w = Math.round(box.clientWidth);
    if (!w || Math.abs(w - last) < 4) return;
    last = w; box.replaceChildren(draw(w));
  });
  ro.observe(box);
  return box;
}

/** Horizontal bars for a top list. */
function hBars(items, color, unit = "") {
  const max = Math.max(1, ...items.map(i => i.value));
  return h("div", { class: "hbars" }, items.map(i => h("div", { class: "hbar" },
    h("span", { class: "hbar-label", title: i.label }, i.label),
    h("span", { class: "hbar-track" }, h("i", { style: `width:${Math.max(3, 100 * i.value / max)}%;background:${color}` })),
    h("b", { class: "hbar-val" }, `${i.value}${unit}`), i.sub ? h("span", { class: "muted small hbar-sub" }, i.sub) : null)));
}

/** Weekday × hour heat map (local time of the server). */
function heatMap(heat) {
  const max = Math.max(1, ...heat.flat());
  const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
  return h("div", { class: "heat" },
    h("div", { class: "heat-hours" }, h("span", {}), ...Array.from({ length: 24 }, (_, hr) => h("span", {}, hr % 3 === 0 ? String(hr) : ""))),
    ...heat.map((row, d) => h("div", { class: "heat-row" }, h("span", { class: "heat-day" }, DAYS[d]),
      ...row.map((n, hr) => { const c = h("span", { class: "heat-cell", style: n ? `--a:${(.15 + .85 * n / max).toFixed(2)}` : "" });
        c.addEventListener("mousemove", e => chartTip(e, [h("b", {}, `${DAYS[d]} ${String(hr).padStart(2, "0")}:00`), h("div", {}, `${n} group(s)`)]));
        c.addEventListener("mouseleave", () => chartTip()); return c; }))));
}

function kpi(ic, label, value, sub, delta, onClick) {
  const deltaEl = delta == null ? null : h("span", { class: `kpi-delta ${delta > 0 ? "up" : delta < 0 ? "down" : "flat"}`, title: "Compared with the previous period of the same length" },
    delta > 0 ? `▲ ${delta}%` : delta < 0 ? `▼ ${Math.abs(delta)}%` : "= 0%");
  return h(onClick ? "button" : "div", { class: `stat ${toneOf(ic)}${onClick ? " clickable" : ""}`, onclick: onClick || null },
    h("div", { class: "stat-top" }, h("span", { class: "label" }, label), h("span", { class: "stat-icon" }, icon(ic, 16))),
    h("div", { class: "value" }, value), h("div", { class: "sub" }, sub, deltaEl ? " " : null, deltaEl));
}
const pctChange = (now, prev) => prev == null || prev < 5 ? null : Math.round(100 * (now - prev) / prev);

function renderDashboard(body, d, go) {
  const per = d.days ? `last ${d.days} days` : "all time";
  const legend = series => h("div", { class: "chart-legend" }, series.map(s => h("span", {}, h("i", { style: `background:${s.color}` }), s.label)));
  const notEnough = [];
  const since = ms => ms ? new Date(ms).toLocaleDateString() : null;
  // Period longer than the data we have: say from when
  const bar = body.parentElement && body.parentElement.querySelector(".dash-since");
  const m = d.members, t = d.tickets, l = d.lfg;

  const stats = h("div", { class: "stats" },
    kpi("users", "Members", String(m.now), m.change == null ? "on the server" : `${m.change >= 0 ? "+" : ""}${m.change} in ${per}`, null),
    kpi("activity", "Active members", String(m.active7), `${m.now ? Math.round(100 * m.active7 / m.now) : 0}% wrote or talked in 7 days · ${m.active30} in 30`, null),
    kpi("user-plus", "Joined", String(m.joined), per, pctChange(m.joined, m.joinedPrev)),
    can("tickets") ? kpi("ticket", "Tickets created", String(t.created), `${t.processed} processed · ${t.openNow} open now`, pctChange(t.created, t.createdPrev), go("tickets")) : null,
    can("lfg") ? kpi("swords", "LFG groups", String(l.groups), `${l.players} player(s) · ${per}`, pctChange(l.groups, l.groupsPrev), go("lfg")) : null);

  const sections = [];
  // Members
  const series = m.series.map(s => ({ date: s.date, value: s.members }));
  const activeSeries = m.series.filter(s => s.active7 != null).map(s => ({ date: s.date, value: s.active7 }));
  const memberBlocks = [];
  if (series.length >= 7) memberBlocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Members on the server"), fitChart(w => lineChart(series, DASH.a, "members", 170, w))));
  else notEnough.push(`member chart (${series.length}/7 daily snapshots)`);
  if (activeSeries.length >= 7) memberBlocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Active in the last 7 days"), fitChart(w => lineChart(activeSeries, DASH.b, "active", 170, w))));
  if (m.joinsBy.length >= 2) memberBlocks.push(h("div", { class: "dash-chart" }, h("h3", {}, `Joined per ${d.monthly ? "month" : "week"}`),
    fitChart(w => barChart(m.joinsBy.map(x => ({ key: x.key, joined: x.count })), [{ key: "joined", label: "joined", color: DASH.c }], d.monthly, 170, w))));
  if (memberBlocks.length) sections.push(h("div", { class: "card dash-card" }, cardHead("users", "Members"), h("div", { class: "dash-grid" }, memberBlocks)));

  // Tickets
  if (can("tickets")) {
    if (t.created + t.processed >= 3) {
      const decided = t.approved + t.denied;
      const outcome = h("div", { class: "outcome" },
        h("div", { class: "outcome-bar" },
          t.approved ? h("i", { class: "ok", style: `flex:${t.approved}`, title: `${t.approved} approved` }) : null,
          t.denied ? h("i", { class: "no", style: `flex:${t.denied}`, title: `${t.denied} denied` }) : null,
          t.closed ? h("i", { class: "un", style: `flex:${t.closed}`, title: `${t.closed} closed without a decision` }) : null),
        h("div", { class: "outcome-legend" },
          h("span", {}, icon("circle-check", 13), `${t.approved} approved`), h("span", {}, icon("circle-x", 13), `${t.denied} denied`),
          h("span", {}, icon("circle-dot", 13), `${t.closed} closed`)));
      const facts = h("div", { class: "dash-facts" },
        h("div", {}, h("b", {}, String(t.created)), h("span", {}, "created")),
        h("div", {}, h("b", {}, String(t.processed)), h("span", {}, "processed")),
        h("div", {}, h("b", {}, decided ? `${Math.round(100 * t.approved / decided)}%` : "—"), h("span", {}, "approved of decided")),
        h("div", {}, h("b", {}, t.medianHours == null ? "—" : t.medianHours < 48 ? `${t.medianHours} h` : `${Math.round(t.medianHours / 24)} d`), h("span", {}, "median time to a decision")));
      const blocks = [h("div", { class: "dash-chart wide" }, h("h3", {}, `Created vs processed per ${d.monthly ? "month" : "week"}`),
        legend([{ label: "created", color: DASH.a }, { label: "processed", color: DASH.b }]),
        fitChart(w => barChart(t.by, [{ key: "created", label: "created", color: DASH.a }, { key: "processed", label: "processed", color: DASH.b }], d.monthly, 190, w)))];
      if (t.projects.length) blocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Processed by project"),
        hBars(t.projects.map(p => ({ label: p.name, value: p.total, sub: `${p.approved}✓ ${p.denied}✗` })), DASH.c)));
      if (t.reviewers.length) blocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Who decides"),
        h("div", { class: "dash-people" }, t.reviewers.map(r => h("div", { class: "dash-person" }, member(r.member),
          h("span", { class: "muted small" }, r.medianHours == null ? "" : `~${r.medianHours < 48 ? r.medianHours + " h" : Math.round(r.medianHours / 24) + " d"}`), h("b", {}, String(r.count)))))));
      sections.push(h("div", { class: "card dash-card" }, cardHead("ticket", "Tickets"), facts, outcome, h("div", { class: "dash-grid" }, blocks)));
    } else notEnough.push("tickets (fewer than 3 in this period)");
  }

  // LFG
  if (can("lfg")) {
    if (l.groups >= 3) {
      const facts = h("div", { class: "dash-facts" },
        h("div", {}, h("b", {}, String(l.groups)), h("span", {}, "groups")),
        h("div", {}, h("b", {}, String(l.players)), h("span", {}, "different players")),
        h("div", {}, h("b", {}, l.fill == null ? "—" : `${l.fill}%`), h("span", {}, "seats filled")),
        h("div", { title: `${l.marked} marked seat(s)` }, h("b", {}, l.attendance == null ? "—" : `${l.attendance}%`), h("span", {}, "came (of marked)")));
      const blocks = [h("div", { class: "dash-chart wide" }, h("h3", {}, `Groups and people per ${d.monthly ? "month" : "week"}`),
        legend([{ label: "groups", color: DASH.c }, { label: "people in them", color: DASH.d }]),
        fitChart(w => barChart(l.by, [{ key: "groups", label: "groups", color: DASH.c }, { key: "people", label: "people", color: DASH.d }], d.monthly, 190, w)))];
      if (l.activities.length) blocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Popular activities"),
        hBars(l.activities.map(a => ({ label: a.name, value: a.groups, sub: `~${a.avgFilled} ppl` })), DASH.c)));
      if (l.organizers.length) blocks.push(h("div", { class: "dash-chart" }, h("h3", {}, "Top organizers"),
        h("div", { class: "dash-people" }, l.organizers.map(o => h("div", { class: "dash-person" }, member(o.member), h("b", {}, String(o.count)))))));
      if (l.groups >= 15) blocks.push(h("div", { class: "dash-chart wide" }, h("h3", {}, "When groups play (server time)"), heatMap(l.heat)));
      else notEnough.push("play-time map (needs 15+ groups)");
      sections.push(h("div", { class: "card dash-card" }, cardHead("swords", "LFG"), facts, h("div", { class: "dash-grid" }, blocks)));
    } else notEnough.push("LFG (fewer than 3 groups in this period)");
  }

  if (bar) {
    const s = d.dataSince, firsts = [s.snapshot ? new Date(s.snapshot).getTime() : null, s.ticket, s.lfg].filter(Boolean);
    const oldest = firsts.length ? Math.min(...firsts) : null;
    bar.textContent = d.days && oldest && oldest > Date.now() - d.days * 864e5 ? `Data is collected since ${since(oldest)} — the period is shorter in fact.` : "";
  }
  body.replaceChildren(...[stats, ...sections,
    notEnough.length ? h("div", { class: "muted small dash-hidden" }, icon("eye-off", 13), `Hidden until there is enough data: ${notEnough.join(" · ")}.`) : null].filter(Boolean));
}

// ================= LFG =================

async function pageLfg(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📣 LFG"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));

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
  actCard.append(actList.length ? h("div", { class: "table-wrap" }, h("table", { class: "table act-table" },
    h("thead", {}, h("tr", {}, h("th", {}, "Activity"), h("th", {}, "Ping role"), h("th", { title: "Characters with a lower GS don't see the activity when creating a group" }, "Min GS"), projects.length ? h("th", {}, "Project") : null, h("th", {}))),
    h("tbody", {}, actList.map(a => {
      const edit = h("button", { class: "btn secondary icon-only sm", title: "Edit", "aria-label": "Edit", onclick: () => activityDialog(a) }, icon("pencil", 15));
      const rm = h("button", { class: "btn ghost icon-only sm danger-text", title: "Remove", "aria-label": "Remove" }, icon("trash-2", 15));
      rm.addEventListener("click", () => removeWithUndo(rm.closest("tr"), `${a.name} removed.`,
        () => gApi("DELETE", `/lfg/activities/${encodeURIComponent(a.key)}`), refresh));
      return h("tr", {}, h("td", {}, a.name, mode === "project" && !a.projectKey ? h("div", {}, shared.cloneNode(true)) : null),
        h("td", {}, roleChip(a.role)), h("td", {}, a.minGs != null ? h("span", { class: "chip" }, `GS ${a.minGs}+`) : h("span", { class: "muted" }, "—")),
        projects.length ? h("td", {}, moveSel(a)) : null, h("td", { class: "actions" }, edit, " ", rm));
    })))) : empty("No activities yet."));
  const nameIn = h("input", { placeholder: "Activity name, e.g. Fire Temple", maxlength: "100" });
  const roleIn = roleSelect("Role to ping…");
  const minGsIn = h("input", { type: "number", min: "0", max: "99999", placeholder: "min GS", style: "width:110px", title: "Optional: characters with a lower GS don't see the activity" });
  const addBtn = h("button", { class: "btn" }, "➕ Add");
  addBtn.addEventListener("click", async () => {
    const ok = await act(addBtn, () => gApi("POST", "/lfg/activities", { name: nameIn.value, roleId: roleIn.value, projectKey: P, minGs: minGsIn.value === "" ? null : Number(minGsIn.value) }),
      mode === "project" ? `Activity added to ${project.name}.` : "Activity added.");
    if (ok !== undefined) refresh();
  });
  actCard.append(h("div", { class: "row", style: "margin-top:12px" }, h("div", { class: "grow" }, nameIn), roleIn, minGsIn, addBtn));

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
    const tracker = track("Channel for groups", () => chSel.value, async () => {
      const ok = await act(chSave, () => gApi("PUT", "/lfg/channel", { channelId: chSel.value || null }), "Channel saved.") !== undefined;
      if (ok) refresh();
      return ok;
    });
    chSave.addEventListener("click", tracker.save);
    tracker.attach(chCard);
    chCard.append(h("div", { class: "row" }, h("div", { class: "grow" }, chSel), chSave), chInfo);
    gApi("GET", "/lfg/channel").then(c => {
      if (c.channelId) chSel.value = c.channelId;
      tracker.rebase();
      chInfo.textContent = c.effective
        ? `Now: #${c.effective.name}${c.fromPanel ? " (picked automatically: the LFG panel's channel or where the latest group was posted)" : ""}`
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
    const tracker = track("Voice room category", () => sel.value, async () => {
      const ok = await act(save, () => gApi("PUT", `/lfg/voice-category${q}`, { channelId: sel.value || null }), "Voice room category saved.") !== undefined;
      if (ok) refresh();
      return ok;
    });
    save.addEventListener("click", tracker.save);
    tracker.attach(vc);
    vc.append(h("div", { class: "row" }, h("div", { class: "grow" }, sel), save), info);
    gApi("GET", `/lfg/voice-category${q}`).then(c => {
      if (c.categoryId) sel.value = c.categoryId;
      tracker.rebase();
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
      h("span", { class: `chip${c.exists === false ? " warn" : ""}` }, label(c), h("button", { title: "Remove", onclick: () =>
        removeLocal(list, i, draw, `No more copies to ${label(c)} (after Save).`) }, "×"))) : [h("span", { class: "muted small" }, "No channels.")]));
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
    const tracker = track("Duplicate posts", () => list.map(c => c.id), async () => {
      const ok = await act(save, () => gApi("PUT", `/lfg/auto-mirrors${q}`, { channelIds: list.map(c => c.id) }), "Saved — new groups are duplicated there.") !== undefined;
      if (ok) refresh();
      return ok;
    });
    save.addEventListener("click", tracker.save);
    tracker.attach(mc);
    mc.append(chips, h("div", { class: "row" }, h("div", { class: "grow" }, add), save), info);
    gApi("GET", `/lfg/auto-mirrors${q}`).then(r => {
      list = r.channels;
      draw();
      tracker.rebase();
      info.textContent = r.inherited ? `Now uses the common list: ${r.inherited.map(label).join(", ")}` : "";
    }).catch(e => { info.textContent = e.message; });
    return mc;
  };

  if (mode === "shared") {
    // ⚙️ General: projects, settings, channel, shared activities / templates, common classes
    const projCard = card(), lfgSettingsCard = card(), lfgPrefsCard = card(), armoryCard = card();
    const sec = sectionTabs([
      ["activities", "swords", "Activities & channels", h("div", { class: "grid-main" }, actCard, h("div", {}, channelCard(), voiceCard(), mirrorCard()))],
      ["projects", "gamepad-2", "Projects", projCard],
      ["settings", "settings", "Settings", h("div", {}, lfgSettingsCard, lfgPrefsCard)],
      ["armory", "shield", "Official armory", armoryCard],
      ["classes", "drama", "Classes", classCard],
      ["templates", "clipboard-list", "Templates", tplCard]
    ], "gb.lfgSectionShared");
    main.append(sec.nav, ...sec.panels);
    renderArmoryPrefs(armoryCard);
    renderProjects(projCard, lfgProjects);
    renderLfgSettings(lfgSettingsCard);
    renderLfgPrefs(lfgPrefsCard);
    renderClasses(classCard, projects, "", { noTabs: true });
    renderTemplates(tplCard, acts, templates, null, "shared");
    return;
  }

  // Разделы проекта: группы, активности и панель, классы, шаблоны, повторяющиеся, статистика
  const lfgSections = [
    ["groups", "users-round", "Active groups", groupsCard],
    ["activities", "swords", "Activities & panel", h("div", { class: "grid-main" }, actCard, h("div", {}, panelCard(), mode === "all" ? channelCard() : null, voiceCard(), mirrorCard()))],
    ["recurring", "repeat", "Recurring", recCard],
    ["templates", "clipboard-list", "Templates", tplCard],
    ["classes", "drama", "Classes", classCard],
    ["stats", "chart-column", "Statistics", statsCard]
  ];
  if (mode === "all") {
    const projCard = card(), lfgSettingsCard = card();
    lfgSections.push(["settings", "settings", "Settings & projects", h("div", {}, lfgSettingsCard, projCard)]);
    renderLfgSettings(lfgSettingsCard);
    renderProjects(projCard, lfgProjects);
  }
  const lfgSec = sectionTabs(lfgSections, "gb.lfgSection");
  main.append(lfgSec.nav, ...lfgSec.panels);
  renderClasses(classCard, projects, P || "", { noTabs: true });
  renderTemplates(tplCard, acts, templates, P, mode);
  renderRecurring(recCard, acts, templates, projects, P);
  renderStats(statsCard, P);

  try {
    const groups = (await gApi("GET", "/lfg/groups")).filter(g => mode === "all" || ofProject(g.projectKey));
    groupsBody.replaceChildren(groups.length ? groupsBoard(groups, mode === "all" ? projects : [], defKey) : empty("No active groups."));
  } catch (e) { groupsBody.replaceChildren(errorState(e.message)); }
}

/** Rename an activity / change the role it pings. */
function activityDialog(a) {
  const d = dialog();
  const name = h("input", { maxlength: "100", value: a.name, style: "width:100%" });
  const role = roleSelect("Role to ping…");
  role.value = a.role.id;
  const minGs = h("input", { type: "number", min: "0", max: "99999", placeholder: "none", value: a.minGs ?? "", style: "width:120px" });
  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("PUT", `/lfg/activities/${encodeURIComponent(a.key)}`, { name: name.value, roleId: role.value, minGs: minGs.value === "" ? null : Number(minGs.value) }),
      "Activity saved.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, `Edit activity: ${a.name}`),
    h("div", { class: "field" }, h("label", {}, "Name"), name),
    h("div", { class: "field" }, h("label", {}, "Role to ping"), role),
    h("div", { class: "field" }, h("label", {}, "Minimum GS (optional)"), minGs,
      h("span", { class: "muted small" }, "When creating a group, the organizer picks a character first: with a lower GS the activity isn't shown. The group gets this GS requirement.")),
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
  // LFG admins only (the server sends the list only to people with the LFG permission): chosen channels as chips + a searchable picker
  const mirrorPool = (opts.mirrorChannels || []).map(c => {
    const voice = c.name.startsWith("🔊 ");
    return { id: c.id, name: voice ? c.name.slice(3) : c.name, voice };
  });
  let mirrorIds = [];
  const mirrorChips = h("div", { class: "row", style: "gap:6px" });
  const drawMirrors = () => mirrorChips.replaceChildren(...mirrorIds.map(id => {
    const c = mirrorPool.find(x => x.id === id);
    return h("span", { class: "chip" }, icon(c.voice ? "volume-2" : "hash", 13), c.name,
      h("button", { type: "button", title: "Remove", onclick: () => { mirrorIds = mirrorIds.filter(x => x !== id); drawMirrors(); } }, "×"));
  }), h("button", { type: "button", class: "btn secondary sm", onclick: async () => {
    const picked = await pickChannelsDialog("Also post in…", mirrorPool, mirrorIds);
    if (picked) { mirrorIds = picked; drawMirrors(); }
  } }, icon("plus", 14), mirrorIds.length ? "Change channels…" : "Add channels…"));
  drawMirrors();
  const voice = h("input", { type: "checkbox", checked: !!v.createVoice });
  const nameIn = h("input", { placeholder: "Template name, e.g. Dungeon 4: Tank/Sup/DD/DD", maxlength: "80", value: v.name || "" });

  const activity = h("select", {});
  // Members: first the character they go with — activities with a higher min GS than its GS are hidden
  const charSel = opts.characters ? h("select", { "aria-label": "Your character" }) : null;
  const charHint = h("span", { class: "muted small" });
  const myChars = () => (opts.characters && opts.characters[currentProject() || ""]) || [];
  const ownerGs = () => { const c = myChars().find(x => x.name === (charSel && charSel.value)); return c ? c.gs : null; };
  const gsFits = a => !charSel || !charSel.value || a.minGs == null || (ownerGs() ?? -1) >= a.minGs;
  const fillChars = () => {
    if (!charSel) return;
    const keep = charSel.value;
    const list = myChars();
    charSel.replaceChildren(...(list.length ? list.map(c => h("option", { value: c.name, "data-icon": c.icon || null },
      [c.name, c.gs != null ? `GS ${c.gs}` : "no GS", c.cp != null ? `CP ${fmtCp(c.cp)}` : null].filter(Boolean).join(" · ")))
      : [h("option", { value: "" }, "No characters in this project — add them in My characters")]));
    if (list.some(c => c.name === keep)) charSel.value = keep;
  };
  // Only activities of the chosen project (+ the ones for all projects) that the character's GS allows
  const fillActivities = projectKey => {
    const keep = activity.value;
    const ofProject = acts.filter(a => !projectKey || !a.projectKey || a.projectKey === projectKey);
    const ok = ofProject.filter(gsFits);
    activity.replaceChildren(h("option", { value: "" }, "✏️ Custom activity (no ping)"),
      ...ok.map(a => h("option", { value: a.key }, `${a.name}${a.minGs != null ? ` · GS ${a.minGs}+` : ""}  ·  @${a.role.name}`)));
    if ([...activity.options].some(o => o.value === keep)) activity.value = keep;
    else if (keep && ok.length) activity.value = ok[0].key; // the chosen activity is hidden now — take the first that fits
    const hidden = ofProject.length - ok.length;
    charHint.textContent = hidden ? `${hidden} activit${hidden === 1 ? "y is" : "ies are"} hidden — this character's GS is too low.` : "";
  };
  fillChars();
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
  // The activity's min GS becomes the group's GS requirement (can still be changed); cleared again when the activity has none
  let autoGs = null;
  const applyActGs = () => {
    if (typeof minGs === "undefined") return;
    const a = acts.find(x => x.key === activity.value);
    if (a && a.minGs != null) { minGs.value = a.minGs; autoGs = a.minGs; }
    else if (autoGs != null && Number(minGs.value) === autoGs) { minGs.value = ""; autoGs = null; }
  };
  activity.addEventListener("change", applyActGs);
  if (charSel) charSel.addEventListener("change", () => { fillActivities(currentProject()); syncCustom(); applyActGs(); if (typeof drawSlots === "function") drawSlots(); });
  syncCustom();

  if (projectSel) {
    projectSel.addEventListener("change", () => { fillChars(); fillActivities(projectSel.value); fillTemplates(); syncCustom(); });
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

  // Requirements: classes of the project (the class set on the character), minimum character GS — others go to the reserve.
  // Only the classes of the current project are offered (LFG → Classes).
  const rolePool = opts.roles || state.roles;
  let related = opts.relatedRoles || null;
  if (!related) gApi("GET", "/lfg/related-roles").then(r => { related = r; drawSlots(); }).catch(() => { related = {}; });
  const projectRoles = () => {
    const ids = related && related[currentProject() || ""];
    return ids ? rolePool.filter(r => ids.includes(r.id)) : [];
  };
  const makeReq = initial => roleListEditor(initial, projectRoles, () => drawSlots(), null, "➕ Add classes…");

  // Slots: role (DD/Tank/Sup/All) and the classes a slot accepts: as the group / any class / its own classes (several)
  let slots = v.slots ? v.slots.slice() : ["Any", "Any", "Any", "Any", "Any"]; // 5 — the usual AION 2 group
  let slotReqs = (v.slotRequirements || []).slice();
  // Any size 2–100 (more than 100 becomes 100)
  const size = h("input", { type: "number", min: "2", max: "100", style: "width:110px", title: "Players, 2–100", "aria-label": "Players" });
  // Favorite sizes as buttons; «Other…» shows the full choice (2–25)
  const FAV_SIZES = [5, 10, 20];
  let allSizes = false;
  const favBtns = FAV_SIZES.map(n => h("button", { type: "button", class: "seg-btn", onclick: () => {
    allSizes = false; size.value = String(n); size.dispatchEvent(new Event("change"));
  } }, `👥 ${n}`));
  const otherBtn = h("button", { type: "button", class: "seg-btn", onclick: () => { allSizes = true; syncSizeUi(); } }, "Other…");
  const sizeWrap = h("span", { class: "row", style: "gap:6px" }, size, h("span", { class: "muted small" }, "players (2–100)"));
  const syncSizeUi = () => {
    const n = Number(size.value);
    const fav = FAV_SIZES.includes(n);
    if (!fav) allSizes = true;
    favBtns.forEach((b, i) => b.classList.toggle("active", !allSizes && FAV_SIZES[i] === n));
    otherBtn.classList.toggle("active", allSizes);
    sizeWrap.style.display = allSizes ? "" : "none";
  };
  const sizeControl = h("div", { class: "row", style: "gap:8px;flex-wrap:wrap" }, h("div", { class: "seg" }, favBtns, otherBtn), sizeWrap);
  const slotBox = h("div", { class: "row", style: "gap:6px;align-items:flex-start" });
  let req = null;
  const roleName = id => (rolePool.find(r => r.id === id) || { name: "class" }).name;
  const slotReqLabel = x => {
    const groupHas = req && req.value().length;
    if (x === "none") return "🔓 any class";
    if (x) return "🔒 " + x.split(",").map(roleName).join(" / ");
    return groupHas ? "🔒 as the group" : "any class";
  };
  const editSlotReq = i => {
    const d = dialog();
    const current = slotReqs[i] || "";
    const pool = projectRoles();
    const mode = current === "none" ? "none" : current ? "own" : "group";
    const radio = (val, label) => {
      const r = h("input", { type: "radio", name: "slotreq", value: val, checked: mode === val });
      return { r, el: h("label", { class: "row", style: "margin-bottom:6px" }, r, label) };
    };
    const rg = radio("group", "Same as the group"), rn = radio("none", "Any class (no requirement)"), ro = radio("own", "Only these classes:");
    const chosen = new Set(mode === "own" ? current.split(",") : []);
    const boxes = pool.map(r => {
      const cb = h("input", { type: "checkbox", checked: chosen.has(r.id) });
      cb.addEventListener("change", () => { ro.r.checked = true; });
      return { r, cb, el: h("label", {}, cb, roleChip(r)) };
    });
    const ok = h("button", { class: "btn", onclick: () => {
      const ids = boxes.filter(x => x.cb.checked).map(x => x.r.id);
      slotReqs[i] = rn.r.checked ? "none" : ro.r.checked && ids.length ? ids.join(",") : "";
      d.close();
      drawSlots();
    } }, "OK");
    d.replaceChildren(h("h2", {}, `Slot ${i + 1} · ${slots[i] === "Any" ? "All" : (SLOT_ROLES.find(([v]) => v === slots[i]) || [, slots[i]])[1]}`),
      h("p", { class: "muted small" }, "The class is the one set on the character the player signs up with. Others go to the reserve."),
      rg.el, rn.el, ro.el,
      pool.length ? h("div", { class: "check-list", style: "margin-left:24px" }, boxes.map(x => x.el))
        : h("p", { class: "muted small" }, "This project has no classes yet — add them in LFG → Classes."),
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), ok));
    d.showModal();
  };
  // Members creating a group: slot 1 is theirs — role from the class of the chosen character, no requirements, not editable
  const ownerChar = () => charSel && charSel.value ? myChars().find(x => x.name === charSel.value) : null;
  const drawSlots = () => {
    size.value = String(slots.length);
    if (typeof syncSizeUi === "function") syncSizeUi();
    slotReqs = slots.map((_, i) => slotReqs[i] || "");
    const oc = ownerChar();
    if (oc && slots.length) { slots[0] = oc.slotRole || "Any"; slotReqs[0] = ""; }
    slotBox.replaceChildren(...slots.map((r, i) => {
      if (i === 0 && oc) return h("div", { class: "owner-slot", title: "Slot 1 is yours — the role comes from your character's class" },
        h("span", { class: "muted small" }, "1."), rolePill((SLOT_ROLES.find(([v]) => v === slots[0]) || [, "🎲 All"])[1].replace(/^\S+\s/, "")),
        h("span", { class: "small" }, icon("crown", 12), " you · ", oc.name));
      const sel = h("select", { title: `Slot ${i + 1}`, "data-render": "role" }, SLOT_ROLES.map(([val, label]) => h("option", { value: val }, `${i + 1}. ${label}`)));
      sel.value = r;
      sel.addEventListener("change", () => { slots[i] = sel.value; });
      const rbtn = h("button", { type: "button", class: `btn ghost sm slot-req${slotReqs[i] ? " set" : ""}`, title: `Classes for slot ${i + 1}`,
        style: "max-width:170px", onclick: () => editSlotReq(i) }, slotReqLabel(slotReqs[i]));
      return h("div", { style: "display:flex;flex-direction:column;gap:4px" }, sel, rbtn);
    }));
  };
  size.addEventListener("change", () => {
    const n = Math.min(100, Math.max(2, Math.round(Number(size.value)) || 5));
    size.value = String(n);
    slots = slots.slice(0, n);
    while (slots.length < n) slots.push("Any");
    drawSlots();
  });
  req = makeReq(v.requiredRoles || []);
  drawSlots();
  const reqBox = h("div", {}, req.el);
  const minGs = h("input", { type: "number", min: "0", max: "99999", placeholder: "none", value: v.minGs ?? "", style: "width:110px" });
  if (v.minGs == null) applyActGs();

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
    slotReqs = (t.slotRequirements || []).slice();
    req = makeReq(t.requiredRoles || []);
    reqBox.replaceChildren(req.el);
    drawSlots();
    minGs.value = t.minGs ?? "";
  });

  const dm = h("input", { type: "checkbox", checked: v.notifyByDm ?? true });
  const seat = h("input", { type: "checkbox", checked: v.seatOwner ?? true });
  const seatLabel = opts.hideSeat ? null : h("label", { class: "row" }, seat, "Organizer takes slot 1");

  const fields = [
    projectSel ? h("div", { class: "field" }, h("label", {}, "🎮 Project (game)"), projectSel) : null,
    charSel ? h("div", { class: "field" }, h("label", {}, "🧙 Your character (you take slot 1 with it)"), charSel, charHint) : null,
    tplSel ? h("div", { class: "field" }, h("label", {}, "📋 Template"), tplSel) : null,
    h("div", { class: "grid2" },
      tplMode ? h("div", { class: "field" }, h("label", {}, "Template name"), nameIn)
        : h("div", { class: "field" }, h("label", {}, opts.fixedChannel ? "Posted in" : "Channel"), channelEl),
      h("div", { class: "field" }, h("label", {}, "Activity"), activity, custom)),
    h("div", { class: "field" }, h("label", {}, "Slots"), h("div", { class: "row", style: "margin-bottom:6px" }, sizeControl), slotBox),
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, "Required class (any of)"), reqBox,
        h("span", { class: "muted small" }, "Only this project's classes (LFG → Classes). Checked by the class of the character the player signs up with. Under each slot you can set its own classes, e.g. Templar or Gladiator for a tank slot.")),
      h("div", { class: "field" }, h("label", {}, "Minimum GS"), minGs,
        h("span", { class: "muted small" }, "Players without the class / GS go to the reserve; the organizer can still move them in.")))
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
    // Организатор: человек или «без организатора» — сбор публикует бот, слот 1 свободен, управляют админы на сайте
    let ownerId = v.noOrganizer ? "none" : v.owner ? v.owner.id : null;
    const ownerView = h("span", {});
    const showOwner = m => {
      ownerView.replaceChildren(ownerId === "none" ? h("span", { class: "chip" }, "🤖 no organizer — the bot posts it") : m ? member(m) : "you");
      seat.disabled = ownerId === "none";
      if (ownerId === "none") seat.checked = false;
    };
    const ownerBtn = h("button", { class: "btn ghost", type: "button" }, "Change");
    ownerBtn.addEventListener("click", async () => {
      const m = await pickMemberDialog("Organizer of the scheduled groups");
      if (m) { ownerId = m.id; showOwner(m); }
    });
    const noOwnerBtn = h("button", { class: "btn ghost", type: "button", title: "Groups are posted by the bot without an organizer: nobody takes slot 1, members sign up themselves; admins manage the groups on the site" }, "No organizer");
    noOwnerBtn.addEventListener("click", () => { ownerId = "none"; showOwner(null); });
    showOwner(v.owner);
    fields.push(
      h("div", { class: "field" }, h("label", {}, "Days"), h("div", {}, days.map(x => x.el))),
      h("div", { class: "row", style: "margin-bottom:12px" },
        h("span", { class: "muted" }, "Starts at"), time, h("span", { class: "muted" }, "· post"), before, h("span", { class: "muted" }, "hours before")),
      h("div", { class: "row", style: "margin-bottom:12px" }, h("span", { class: "muted" }, "Organizer:"), ownerView, ownerBtn, noOwnerBtn),
      h("label", { class: "row", style: "margin-bottom:8px" }, enabled, "Enabled"));
    extra = () => ({
      days: days.filter(x => x.cb.checked).map(x => x.d), time: time.value, postHoursBefore: Number(before.value || 0),
      enabled: enabled.checked, ownerId
    });
  }

  // Organizer's note shown in the post (optional)
  const noteIn = h("textarea", { rows: "2", maxlength: "300", placeholder: "e.g. bring buff food, voice chat required", value: v.note || "" });
  if (!tplMode) fields.push(h("div", { class: "field" }, h("label", {}, "📝 Note (optional, shown in the post)"), noteIn));
  if (!tplMode) fields.push(h("div", { class: "row", style: "margin-bottom:12px" },
    h("label", { class: "row" }, dm, "DM members with the activity role"),
    opts.mode === "once" ? h("label", { class: "row" }, voice, "🔊 Create a voice room (everyone listens, the group speaks)") : null,
    seatLabel));
  if (mirrorPool.length) fields.push(h("div", { class: "field" },
    h("label", {}, "Also post in other channels (LFG admins only, optional)"), mirrorChips));

  return {
    el: h("div", {}, fields),
    value: () => ({
      channelId: channel.value || null, activityKey: activity.value || null, customName: activity.value ? null : custom.value,
      slots: slots.slice(), notifyByDm: dm.checked, seatOwner: seat.checked,
      requiredRoleIds: req.value(), slotRequirements: slotReqs.slice(),
      minGs: minGs.value === "" ? null : Number(minGs.value),
      projectKey: currentProject(), createVoice: voice.checked,
      mirrorChannelIds: mirrorIds.slice(), note: tplMode ? undefined : (noteIn.value.trim() || null),
      ownerCharacter: charSel && charSel.value ? charSel.value : undefined, ...extra()
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
  try { list = await gApi("GET", "/lfg/recurring"); } catch (e) { body.replaceChildren(errorState(e.message)); return; }
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
      rm.addEventListener("click", () => removeWithUndo(rm.closest("tr"), `${r.activity} schedule deleted.`,
        () => gApi("DELETE", `/lfg/recurring/${r.id}`), refresh));
      return h("tr", {},
        h("td", {}, r.activity, h("div", { class: "muted small" }, r.channel ? `#${r.channel}` : "deleted channel", ` · ${r.slots.length} slots`,
          r.noOrganizer ? " · 🤖 no organizer" : r.owner ? ` · by ${r.owner.name}` : "")),
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
  try { s = await gApi("GET", "/lfg/settings"); } catch (e) { card.replaceChildren(h("h2", {}, "⚙️ LFG settings"), errorState(e.message)); return; }

  const zones = h("datalist", { id: "zones" }, s.zones.map(z => h("option", { value: z })));
  const zone = h("input", { list: "zones", placeholder: s.defaults.timeZone, value: s.timeZone || "", style: "width:260px" });
  const hours = h("input", { type: "number", min: "1", max: "72", placeholder: String(s.defaults.autoDeleteHours), value: s.autoDeleteHours ?? "", style: "width:120px" });
  const reminder = h("input", { type: "number", min: "0", max: "180", placeholder: String(s.defaults.reminderMinutes), value: s.reminderMinutes ?? "", style: "width:120px" });
  const save = h("button", { class: "btn" }, "💾 Save");
  const body = () => ({
    timeZone: zone.value.trim() || null,
    autoDeleteHours: hours.value === "" ? null : Number(hours.value),
    reminderMinutes: reminder.value === "" ? null : Number(reminder.value)
  });
  const tracker = track("LFG settings", body, async () => {
    const res = await act(save, () => gApi("PUT", "/lfg/settings", body()));
    if (res === undefined) return false;
    toast(`Saved: ${res.timeZone}, auto-delete ${res.autoDeleteHours} h, reminder ${res.reminderMinutes ? res.reminderMinutes + " min" : "off"}.`);
    refresh();
    return true;
  });
  save.addEventListener("click", tracker.save);

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
  tracker.attach(card);
}

/** Ready check, auto-replace from the reserve, GS reminder. */
/** LFG → Official armory (AION 2): projects with the armory, default faction and server; bulk link/update. */
async function renderArmoryPrefs(card) {
  const title = "🔗 Official armory (AION 2)";
  card.replaceChildren(h("h2", {}, title), skeletonLines(3));
  let d;
  try { d = await gApi("GET", "/lfg/armory"); } catch (e) { card.replaceChildren(h("h2", {}, title), errorState(e.message)); return; }
  if (!d.projects.length) { card.replaceChildren(h("h2", {}, title), empty("Create a project first (LFG → Projects).")); return; }
  const rows = d.projects.map(p => {
    const on = h("input", { type: "checkbox", class: "switch", checked: p.enabled, "aria-label": `Armory for ${p.name}` });
    const realm = realmPicker(d.servers, p.race, p.serverId);
    const sync = () => { realm.race.disabled = realm.server.disabled = !on.checked; };
    on.addEventListener("change", sync); sync();
    return { p, on, realm, el: h("div", { class: "pref-row armory-proj" },
      h("div", { class: "grow" }, h("b", {}, p.name), h("div", { class: "muted small" }, `${p.linked} of ${p.characters} character(s) linked`)),
      realm.race, realm.server, on) };
  });
  const save = h("button", { class: "btn" }, "💾 Save");
  const body = () => ({ projects: rows.map(r => ({ key: r.p.key, enabled: r.on.checked, ...r.realm.value() })) });
  const tracker = track("Official armory", () => JSON.stringify(body()), async () => await act(save, () => gApi("PUT", "/lfg/armory", body()), "Armory settings saved.") !== undefined);
  save.addEventListener("click", tracker.save);

  const status = h("div", { class: "muted small", style: "margin-top:8px" });
  let timer = 0;
  const showStatus = b => {
    clearTimeout(timer);
    if (!b) { status.replaceChildren(); return; }
    const parts = [`${b.done}/${b.total} checked`, `linked ${b.linked}`, `updated ${b.updated}`];
    if (b.notFound) parts.push(`not found ${b.notFound}`);
    if (b.ambiguous) parts.push(`several with this name ${b.ambiguous}`);
    if (b.failed) parts.push(`failed ${b.failed}`);
    status.replaceChildren(...[b.running ? h("span", { class: "pulse" }) : null, b.running ? " Running: " : `Last run ${fmtRelative(b.finished || b.started)}: `,
      parts.join(" · "), b.error ? h("div", { class: "warn-text" }, b.error) : null].filter(Boolean));
    if (b.running) timer = setTimeout(async () => { try { showStatus(await gApi("GET", "/lfg/armory/bulk")); } catch { /* next time */ } }, 2500);
  };
  const run = linkNew => async e => {
    if (tracker.dirty) return toast("Save the settings first.", "warn");
    if (!await confirmDialog(linkNew ? "Link and update all characters?" : "Update all linked characters?",
      linkNew ? "Linked characters are updated. Others are searched by their exact name on their server (or the project's default) and linked when exactly one is found. About 3 seconds per character."
        : "Every linked character is refreshed from the official armory. About 3 seconds per character.", "Start", false)) return;
    const r = await act(e.currentTarget, () => gApi("POST", "/lfg/armory/bulk", { linkNew }));
    if (r) showStatus(r);
  };
  card.replaceChildren(h("h2", {}, title),
    h("p", { class: "muted small" }, "Members link their characters themselves in My characters (search on aion2.plaync.com, EU) and press Update when their gear changes — nothing updates automatically. Faction and server below are the defaults for new characters and for the search."),
    ...rows.map(r => r.el),
    h("div", { class: "row", style: "margin-top:10px;flex-wrap:wrap" }, save,
      h("button", { class: "btn secondary", onclick: run(true) }, icon("link", 15), "Link & update all"),
      h("button", { class: "btn secondary", onclick: run(false) }, icon("refresh-cw", 15), "Update linked")),
    status);
  tracker.attach(card);
  showStatus(d.bulk);
}

async function renderLfgPrefs(card) {
  card.replaceChildren(h("h2", {}, "🧩 Group options"), skeletonLines(3));
  let p;
  try { p = await gApi("GET", "/lfg/prefs"); } catch (e) { card.replaceChildren(h("h2", {}, "🧩 Group options"), errorState(e.message)); return; }
  const ready = toggle(p.readyCheck, "✅ “Ready” button in the reminder before the start — those who pressed it get 🟢 in the post, the organizer sees who didn't");
  const auto = h("input", { type: "number", min: "0", max: "1440", value: p.autoPromoteMinutes, style: "width:100px" });
  const gsOn = toggle(p.gsReminder, "📈 Weekly reminder to update GS");
  const gsDays = h("input", { type: "number", min: "1", max: "365", value: p.gsReminderDays, style: "width:90px" });
  const gsChannel = channelSelect("text", "No channel — DMs only");
  gsChannel.value = p.gsReminderChannelId || "";
  const gsDm = toggle(p.gsReminderDm, "Also by DM (each member gets a DM with their characters)");
  const armoryGs = h("select", { "aria-label": "GS of linked characters" },
    h("option", { value: "itemLevel" }, "Item level"), h("option", { value: "combatPower" }, "Combat power"), h("option", { value: "none" }, "Nothing — GS is entered by hand"));
  armoryGs.value = p.armoryGs || "itemLevel";
  const mTop = toggle(p.memberTop, "🏆 Members see the top active players of the last 30 days (no-shows not counted) — Member → Guild roster");
  const gsBox = h("div", { class: "subcard" },
    h("div", { class: "row", style: "margin-bottom:8px" }, h("span", { class: "muted" }, "GS is outdated after"), gsDays, h("span", { class: "muted" }, "days (or not set)")),
    h("div", { class: "field" }, h("label", {}, "Post the reminder in (mentions those members)"), gsChannel), gsDm.el,
    h("p", { class: "muted small" }, p.gsReminderLastRun ? `Last sent: ${new Date(p.gsReminderLastRun).toLocaleString()}. ` : "Not sent yet. ",
      "Sent once a week. The button in it opens a window to enter the GS of a character."));
  const syncGs = () => { gsBox.style.display = gsOn.cb.checked ? "" : "none"; };
  gsOn.cb.addEventListener("change", syncGs); syncGs();
  const save = h("button", { class: "btn" }, "💾 Save");
  const body = () => ({ readyCheck: ready.cb.checked, autoPromoteMinutes: Number(auto.value || 0), gsReminder: gsOn.cb.checked,
    gsReminderChannelId: gsChannel.value || null, gsReminderDays: Number(gsDays.value || 7), gsReminderDm: gsDm.cb.checked,
    memberTop: mTop.cb.checked, armoryGs: armoryGs.value });
  const tracker = track("Group options", body, async () => await act(save, () => gApi("PUT", "/lfg/prefs", body()), "Group options saved.") !== undefined);
  save.addEventListener("click", tracker.save);
  const runNow = h("button", { class: "btn secondary", title: "Send the GS reminder now (with the saved settings)" }, "📈 Send the GS reminder now");
  runNow.addEventListener("click", async () => {
    if (!await confirmDialog("Send the GS reminder now?", "Members whose GS is outdated are mentioned in the channel and/or get a DM — as saved.", "Send", false)) return;
    const r = await act(runNow, () => gApi("POST", "/lfg/gs-reminder/run"));
    if (r) toast(r.members ? `Reminded ${r.members} member(s).` : "Everyone's GS is up to date.");
  });
  card.replaceChildren(h("h2", {}, "🧩 Group options"),
    ready.el,
    h("div", { class: "field" }, h("label", {}, "🔁 Auto-replace from the reserve"),
      h("div", { class: "row" }, h("span", { class: "muted" }, "In the last"), auto, h("span", { class: "muted" }, "minutes before the start (0 = off)")),
      h("span", { class: "muted small" }, "When a slot frees up then, the first player from the reserve who fits (role, class, GS) moves in; the bot replies under the post. Earlier the organizer decides.")),
    gsOn.el, gsBox,
    h("div", { class: "field", style: "margin-top:10px" }, h("label", {}, "🔗 GS of characters linked to the official armory"), armoryGs,
      h("span", { class: "muted small" }, "Members link their characters in My characters (aion2.plaync.com). The chosen value becomes the GS when they link or press Update; the GS field is then read-only.")),
    h("label", { class: "field-label", style: "margin-top:10px" }, "For members on the site"), mTop.el,
    h("p", { class: "muted small" }, "Members always see the guild roster of their own projects (Member → Guild roster: character, class, server, roles, GS, CP)."),
    h("div", { class: "row", style: "margin-top:8px" }, save, runNow));
  tracker.attach(card);
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
  try { data = await gApi("GET", `/lfg/classes${q}`); } catch (e) { card.replaceChildren(h("h2", {}, "🎭 Classes"), errorState(e.message)); return; }
  // Common classes + each project's own (a project without its own uses the common list)
  const tabs = projectList.length && !opts.noTabs ? h("div", { class: "row", style: "margin-bottom:10px" },
    [["", "🌐 Common"], ...projectList.map(p => [p.key, `🎮 ${p.name}`])].map(([k, label]) =>
      h("button", { class: `btn secondary${k === projectKey ? " active-tab" : ""}`,
        onclick: () => { store.set("gb.classProject", k); renderClasses(card, projectList, k, opts); } }, label))) : null;
  const roleIcons = data.roleIcons || {};
  let rows = data.classes.map(c => ({ roleId: c.role.id, role: c.role, icon: c.icon, slots: c.slots || [] }));
  // Group roles a class can play: none ticked = any
  const SLOT_ROLES = [["Dps", "⚔️", "DD"], ["Tank", "🛡️", "Tank"], ["Healer", "💚", "Sup"]];
  const slotToggles = r => {
    const hint = h("span", { class: "muted small" }, r.slots.length ? "" : "any role");
    return h("span", { class: "class-slots", title: "Which group roles this class plays. None ticked = any role." },
      SLOT_ROLES.map(([k, em, label]) => {
        const b = h("button", { type: "button", class: `chip slot-chip${r.slots.includes(k) ? " on" : ""}` }, `${em} ${label}`);
        b.addEventListener("click", () => {
          r.slots = r.slots.includes(k) ? r.slots.filter(x => x !== k) : [...r.slots, k];
          b.classList.toggle("on", r.slots.includes(k));
          hint.textContent = r.slots.length ? "" : "any role";
        });
        return b;
      }), hint);
  };
  const list = h("div", {});
  const preview = icon => classMark({ classIcon: icon }) || h("span", { class: "muted small" }, "—");
  // The role's own icon (Discord role settings → Role icon)
  const roleIconEl = ri => ri.url ? h("img", { class: "gm-emo", src: ri.url, alt: "", style: "width:18px;height:18px" }) : h("span", {}, ri.emoji);
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
      const iconInput = h("input", { value: r.icon || "", placeholder: "emoji or <:name:id>", title: "Class icon: an emoji or <:name:id>" });
      const prev = h("span", { class: "class-prev", title: "How the icon looks" }, preview(r.icon));
      iconInput.addEventListener("input", () => { r.icon = iconInput.value; prev.replaceChildren(preview(iconInput.value)); });
      const pick = h("select", { style: "width:100%", "aria-label": "Pick a server emoji" }, h("option", { value: "" }, "Pick emoji…"),
        data.emojis.map(e => h("option", { value: e.code }, `:${e.name}:${e.app ? "  (bot)" : ""}`)));
      pick.addEventListener("change", () => { if (pick.value) { r.icon = iconInput.value = pick.value; prev.replaceChildren(preview(pick.value)); } });
      const ri = roleIcons[r.roleId];
      const useRole = ri ? h("button", { class: "btn secondary", title: "Use this role's icon from Discord" }, roleIconEl(ri), "Role icon") : null;
      if (useRole) useRole.addEventListener("click", () => importIcons(useRole, [r]));
      return h("div", { class: "class-row" }, h("div", { class: "class-role" }, roleChip(r.role)), prev, iconInput, pick,
        useRole || h("span", {}), slotToggles(r),
        h("button", { class: "btn ghost icon-only sm danger-text", title: "Remove", "aria-label": "Remove", onclick: () =>
          removeLocal(rows, i, draw, `@${r.role.name} removed from classes (after Save).`) }, icon("trash-2", 15)));
    }));
  };
  // Several class roles at once
  const addSel = h("button", { class: "btn secondary" }, "➕ Add class roles…");
  addSel.addEventListener("click", async () => {
    const picked = await pickRolesDialog("Add class roles", rows.map(r => r.roleId));
    if (!picked) return;
    for (const role of picked) {
      const ri = roleIcons[role.id];
      rows.push({ roleId: role.id, role: { ...role, exists: true }, icon: ri && ri.emoji ? ri.emoji : "", slots: [] });
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
  const classesBody = () => rows.map(r => ({ roleId: r.roleId, icon: r.icon || "", slots: [...r.slots].sort() }));
  const tracker = track("Classes", classesBody, async () => await act(save, () => gApi("PUT", `/lfg/classes${q}`, { classes: classesBody() }),
    "Classes saved. New signups use the icons; existing posts update when someone changes their class.") !== undefined);
  save.addEventListener("click", tracker.save);
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
      rows = common.classes.map(c => ({ roleId: c.role.id, role: c.role, icon: c.icon, slots: c.slots || [] }));
      draw();
      toast(rows.length ? "Copied — edit and press Save classes." : "The common list is empty.", rows.length ? "ok" : "warn");
    });
  }
  card.replaceChildren(...[h("h2", {}, title), tabs,
    projectKey && !rows.length ? h("div", { class: "chip", style: "margin-bottom:8px" }, "Now uses the common list (⚙️ General → Common classes)") : null,
    h("p", { class: "muted small" }, "Roles that count as classes and their icon shown next to the player's name. If the list is not empty, players pick any of these as their class (they don't need the role); if empty — only roles they have. ",
      projectKey ? "This project's own classes — if empty, the common list is used." : "Common classes — used by every project that has no classes of its own. ",
      "Use a unicode emoji, a server emoji, or 🎨 Use role icons — the roles' own icons from Discord (image icons are uploaded as the bot's emojis, no server emoji slots used). ",
      "⚔️ DD / 🛡️ Tank / 💚 Sup — which group roles the class plays (several allowed): signing up as DD shows only characters whose class plays DD. None ticked — any role."),
    list, h("div", { class: "row", style: "margin-top:8px" }, addSel, copy, fromRoles, save)].filter(Boolean));
  tracker.attach(card);
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
        rm.addEventListener("click", () => removeWithUndo(rm.closest("tr"), `Template ${t.name} deleted.`,
          () => gApi("DELETE", `/lfg/templates/${t.key}`), refresh));
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
  const saved = Number(store.get("gb.lfgStatsDays") || 90);
  const period = h("select", { "aria-label": "Period" }, [[30, "Last 30 days"], [90, "Last 90 days"], [180, "Last 6 months"], [365, "Last year"], [0, "All time"]]
    .map(([v, l]) => h("option", { value: v, selected: v === saved }, l)));
  const body = h("div", {});
  const load = async () => {
    store.set("gb.lfgStatsDays", period.value);
    body.style.opacity = ".55";
    let st;
    try { st = await gApi("GET", `/lfg/stats?days=${period.value}${projectKey ? `&project=${encodeURIComponent(projectKey)}` : ""}`); } catch (e) { body.replaceChildren(errorState(e.message)); body.style.opacity = ""; return; }
    body.style.opacity = "";
    if (st.groups < 3) { body.replaceChildren(emptyState("chart-column", "Not enough groups yet", `${st.groups} group(s) in this period — statistics appear from 3 closed groups. Try a longer period.`)); return; }
    const rel = p => p.reliability === null ? "—" : `${p.reliability}%`;
    const people = (title, rows, value) => h("div", { class: "dash-chart" }, h("h3", {}, title), rows.length
      ? h("div", { class: "dash-people" }, rows.map(r => h("div", { class: "dash-person" }, member(r.member), ...value(r)))) : empty("—"));
    body.replaceChildren(
      h("div", { class: "dash-facts" },
        h("div", {}, h("b", {}, String(st.groups)), h("span", {}, "groups")),
        h("div", {}, h("b", {}, String(st.players)), h("span", {}, "different players")),
        h("div", {}, h("b", {}, st.fill == null ? "—" : `${st.fill}%`), h("span", {}, "seats filled")),
        h("div", { title: `${st.attendanceMarked} marked seat(s)` }, h("b", {}, st.attendance == null ? "—" : `${st.attendance}%`), h("span", {}, "came (of marked)"))),
      h("div", { class: "dash-grid" },
        (st.by || []).length >= 2 ? h("div", { class: "dash-chart wide" }, h("h3", {}, `Groups and people per ${st.monthly ? "month" : "week"}`),
          h("div", { class: "chart-legend" }, h("span", {}, h("i", { style: `background:${DASH.c}` }), "groups"), h("span", {}, h("i", { style: `background:${DASH.d}` }), "people in them")),
          fitChart(w => barChart(st.by, [{ key: "groups", label: "groups", color: DASH.c }, { key: "people", label: "people", color: DASH.d }], st.monthly, 180, w))) : null,
        h("div", { class: "dash-chart" }, h("h3", {}, "By activity"),
          hBars(st.activities.slice(0, 10).map(a => ({ label: a.name, value: a.groups, sub: `~${a.avgFilled} ppl` })), DASH.c)),
        people("Top players", st.topPlayers.slice(0, 10), p => [h("span", { class: "muted small" }, `came ${p.attended} · ${rel(p)}`), h("b", {}, String(p.groups))]),
        people("Top organizers", st.topOrganizers, p => [h("b", {}, String(p.organized))]),
        people("Most no-shows", st.noShows, p => [h("span", { class: "muted small" }, rel(p)), h("b", { class: "danger-num" }, String(p.noShows))]),
        st.groups >= 15 && st.heat ? h("div", { class: "dash-chart wide" }, h("h3", {}, "When groups play (server time)"), heatMap(st.heat)) : null));
  };
  period.addEventListener("change", load);
  card.replaceChildren(h("div", { class: "card-head" }, h("h2", {}, "📈 Statistics"), period),
    h("p", { class: "muted small" }, "From closed and expired groups. Reliability = came / (came + no-show) of marked attendance. Short on data? Pick a longer period."), body);
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

/** Organizer's note of a group (shown in the post); empty = remove. */
function groupNoteDialog(g, url) {
  const d = dialog();
  const text = h("textarea", { rows: "3", maxlength: "300", placeholder: "e.g. bring buff food, voice chat required", value: g.note || "" });
  const save = h("button", { class: "btn" }, "📝 Save note");
  save.addEventListener("click", async () => {
    const value = text.value.trim();
    if (await act(save, () => gApi("POST", url, { note: value || null }), value ? "Note saved." : "Note removed.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, `Note: ${g.activity}`),
    h("p", { class: "muted small" }, "Shown in the group post. Leave empty to remove it."),
    text,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
}

/** The group's note under the meta line (if any). */
function groupNoteLine(g) {
  return g.note ? h("div", { class: "muted small group-note", style: "white-space:pre-wrap" }, icon("file-pen-line", 14), g.note) : null;
}

/** After marking attendance in place, update the row's highlight and the Came/No-show badge. */
function syncSlotAttendance(node, s) {
  const slot = node.closest(".slot");
  if (!slot) return;
  slot.classList.toggle("att-ok", s.attended === true);
  slot.classList.toggle("att-no", s.attended === false);
  slot.querySelector(".att-badge")?.remove();
  const badge = attBadge(s);
  if (badge) (slot.querySelector(".slot-char") || slot.querySelector(".slot-who"))?.append(badge);
  // the "ready" green dot shows only while unmarked
  if (s.attended != null) [...slot.querySelectorAll(".slot-who span[title='Ready'], .slot-who span[title=\"Pressed ✅ Ready in the reminder\"]")].forEach(x => x.remove());
}

/** Slot highlight class by attendance: came → att-ok, no-show → att-no, else none. */
function attClass(s) {
  return s.attended === true ? " att-ok" : s.attended === false ? " att-no" : "";
}

/** Small explicit badge of attendance (Came / No-show); null when not marked. */
function attBadge(s) {
  if (s.attended === true) return h("span", { class: "att-badge ok", title: "Marked as came" }, icon("circle-check", 12), "Came");
  if (s.attended === false) return h("span", { class: "att-badge no", title: "Marked as no-show" }, icon("circle-x", 12), "No-show");
  return null;
}

/** Attendance toggle for a seated member: not marked → came → no-show → not marked.
 *  Updates itself in place (no full page re-render) so the page doesn't jump to the top. */
function attendanceButton(s, url) {
  const build = () => {
    const next = s.attended === null || s.attended === undefined ? true : s.attended ? false : null;
    const ic = s.attended === true ? "circle-check" : s.attended === false ? "circle-x" : "circle-dot";
    const cls = s.attended === true ? " att-came" : s.attended === false ? " att-miss" : "";
    const title = s.attended === true ? "Came — click: no-show" : s.attended === false ? "No-show — click: clear" : "Not marked — click: came";
    const btn = h("button", { class: `btn ghost icon-only sm${cls}`, title, "aria-label": title,
      onclick: () => act(btn, () => gApi("POST", url, { userId: s.member.id, attended: next }))
        .then(r => { if (r !== undefined) { s.attended = next; const nb = build(); btn.replaceWith(nb); syncSlotAttendance(nb, s); } }) }, icon(ic, 16));
    return btn;
  };
  return build();
}

/** Own requirement of an open slot: 🔒 the role it needs, or 🔓 when this slot needs no role. */
function slotReqMark(s) {
  if (s.requiredClasses && s.requiredClasses.length)
    return h("span", { title: "Only characters of these classes can take this slot" }, "🔒 ", s.requiredClasses.map(r => roleChip(r)));
  if (s.requiredRole) return h("span", { title: "Only characters of this class can take this slot" }, "🔒 ", roleChip(s.requiredRole));
  if (s.noRequirement) return h("span", { class: "chip", title: "This slot needs no role" }, "🔓 any");
  return null;
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
  return m ? h("img", { class: "gm-emo", src: `https://cdn.discordapp.com/emojis/${m[2]}.webp?size=32`, alt: "", title, style: "width:18px;height:18px" })
    : h("span", { title }, e.classIcon);
}

/** Seat of a group in two lines: who (name, Came/No-show, ready) and their character (class, name, GS, CP, note) —
 *  so a narrow card never squeezes the texts on top of each other. */
function slotInfo(s, readyTitle, withNote) {
  const ready = readyTitle && s.ready && s.attended == null ? h("span", { class: "slot-ready", title: readyTitle }, "🟢") : null;
  const charLine = [classMark(s), s.character ? h("span", { class: "slot-charname", title: s.character }, s.character) : null,
    s.gs ? h("span", { class: "gs" }, `GS ${s.gs}`) : null, s.cp ? h("span", { class: "gs cp" }, `CP ${fmtCp(s.cp)}`) : null, attBadge(s)].filter(Boolean);
  return h("div", { class: "slot-main" },
    h("div", { class: "slot-who" }, member(s.member), ready),
    charLine.length ? h("div", { class: "slot-char" }, charLine) : null,
    withNote && s.comment ? h("div", { class: "slot-comment", title: s.comment }, icon("message-square-text", 12), h("span", {}, s.comment)) : null);
}
const slotTools = (...tools) => h("span", { class: "slot-tools" }, tools);

/** Active groups with filters: status, search by member / character / activity, project (General), full groups folded. */
function groupsBoard(groups, projects = [], defKey = null) {
  const saved = (() => { try { return JSON.parse(store.get("gb.groupsFilter") || "{}"); } catch { return {}; } })();
  const f = { status: saved.status || "all", project: saved.project || "", foldFull: saved.foldFull !== false, q: "" };
  const opened = new Set();
  const isFull = g => g.filled >= g.size;
  const startMs = g => g.startsAt ? +new Date(g.startsAt) : 9e15;
  const statusOf = g => g.started ? "started" : isFull(g) ? "full" : "recruiting";
  const STATUSES = [["all", "All"], ["recruiting", "Recruiting"], ["started", "Started"], ["full", "Full"]];
  const keep = () => store.set("gb.groupsFilter", JSON.stringify({ status: f.status, project: f.project, foldFull: f.foldFull }));
  const text = g => [g.activity, g.owner && g.owner.name, ...g.slots.flatMap(s => s.member ? [s.member.name, s.member.username, s.character] : []),
    ...g.reserve.flatMap(r => [r.member.name, r.member.username, r.character])].filter(Boolean).join(" ").toLowerCase();

  const segBtns = STATUSES.map(([k, label]) => {
    const n = k === "all" ? groups.length : groups.filter(g => statusOf(g) === k).length;
    const b = h("button", { type: "button", class: "seg-btn", "data-k": k }, label, h("span", { class: "seg-badge" }, String(n)));
    b.addEventListener("click", () => { f.status = k; keep(); draw(); });
    return b;
  });
  const search = h("input", { type: "search", placeholder: "Search a member, character or activity…", "aria-label": "Search groups" });
  // Coming from Ctrl+K: start with that query
  const fromSearch = store.get("gb.groupsQuery");
  if (fromSearch) { store.set("gb.groupsQuery", ""); search.value = fromSearch; f.q = fromSearch.toLowerCase(); }
  search.addEventListener("input", () => { f.q = search.value.trim().toLowerCase(); draw(); });
  const projSel = projects.length > 1 ? h("select", { "aria-label": "Project", style: "width:200px" }, h("option", { value: "" }, "All projects"),
    projects.map(p => h("option", { value: p.key }, p.name))) : null;
  if (projSel) { projSel.value = f.project; projSel.addEventListener("change", () => { f.project = projSel.value; keep(); draw(); }); }
  const fold = h("input", { type: "checkbox", class: "switch", checked: f.foldFull });
  fold.addEventListener("change", () => { f.foldFull = fold.checked; keep(); draw(); });
  const toolbar = h("div", { class: "groups-toolbar" }, h("div", { class: "seg groups-seg" }, segBtns),
    h("div", { class: "groups-search" }, icon("search", 15), search), projSel,
    h("label", { class: "groups-fold", title: "Full and started groups take one line; click to open" }, fold, "Fold full / started"));
  const body = h("div", {});

  const mini = g => h("button", { type: "button", class: "group-mini", onclick: () => { opened.add(g.id); draw(); } },
    h("b", { class: "group-mini-name" }, g.activity),
    g.started ? h("span", { class: "badge wait" }, icon("timer", 12), "Started") : h("span", { class: "badge ok" }, icon("check", 12), "Full"),
    h("span", { class: "muted small group-mini-meta" }, g.owner ? g.owner.name : "no organizer", " · ", g.startsAt ? fmtDate(g.startsAt) : "no time"),
    h("span", { class: "group-mini-count" }, `${g.filled}/${g.size}`, g.reserve.length ? ` +${g.reserve.length}` : ""),
    icon("chevron-down", 15));

  function draw() {
    segBtns.forEach(b => b.classList.toggle("active", b.dataset.k === f.status));
    const list = groups.filter(g => (f.status === "all" || statusOf(g) === f.status)
      && (!f.project || (g.projectKey || defKey) === f.project)
      && (!f.q || text(g).includes(f.q)));
    // Recruiting first (they need people), then by start time
    list.sort((a, b) => (isFull(a) || a.started ? 1 : 0) - (isFull(b) || b.started ? 1 : 0) || startMs(a) - startMs(b));
    const folded = list.filter(g => f.foldFull && !f.q && (isFull(g) || g.started) && !opened.has(g.id));
    const open = list.filter(g => !folded.includes(g));
    body.replaceChildren(!list.length ? empty("No groups match the filter.") : h("div", {},
      folded.length ? h("div", { class: "group-mini-list" }, folded.map(mini)) : null,
      open.length ? h("div", { class: "grid2" }, open.map(groupCard)) : null));
  }
  draw();
  return h("div", {}, toolbar, body);
}

function groupCard(g) {
  const gid = encodeURIComponent(g.id);
  const close = h("button", { class: "btn danger" }, icon("trash-2", 15), "Force close");
  close.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The group is closed and its post is deleted in Discord.", "Close & delete")) return;
    if (await act(close, () => gApi("POST", `/lfg/groups/${gid}/close`), "Group closed.") !== undefined) refresh();
  });

  const roster = (action, m, okText, extra) => act(null,
    () => gApi("POST", `/lfg/groups/${gid}/roster`, { action, userId: m.id, ...(extra || {}) }), okText)
    .then(r => { if (r !== undefined) refresh(); });
  const tool = (ic, title, fn) => h("button", { class: "btn ghost icon-only sm", title, "aria-label": title, onclick: fn }, icon(ic, 15));
  const rename = (m, current) => async () => {
    const name = await promptDialog(`Character of ${m.name}`, current || "");
    if (name) roster("character", m, "Character changed.", { character: name });
  };

  const full = g.filled >= g.size;
  return h("div", { class: `card group-card${full ? " is-full" : ""}`, style: "margin:0" },
    h("div", { class: "group-head" },
      h("div", { class: "group-title" }, h("h3", {}, g.activity)),
      h("div", { class: "group-state" },
        g.started ? h("span", { class: "badge wait" }, icon("timer", 12), "Started") : full ? h("span", { class: "badge ok" }, icon("check", 12), "Full")
          : h("span", { class: "badge recruiting" }, h("span", { class: "pulse" }), "Recruiting"),
        capacity(g.filled, g.size))),
    h("div", { class: "group-meta" },
      h("div", { class: "meta-item" }, icon("crown", 14), g.owner ? member(g.owner) : h("span", { class: "muted" }, "no organizer")),
      h("div", { class: "meta-item" }, icon("calendar-clock", 14), g.startsAt ? fmtDate(g.startsAt) : "No time set"),
      g.channel ? h("div", { class: "meta-item" }, icon("hash", 14), g.channel) : null,
      h("div", { class: "meta-item" }, icon("trash-2", 14), `auto-delete ${fmtRelative(g.expiresAt)}`),
      g.requirements ? h("div", { class: "meta-item" }, icon("shield", 14), g.requirements.roles.map(r => roleChip(r)),
        g.requirements.minGs ? h("span", { class: "chip" }, `GS ${g.requirements.minGs}+`) : null) : null),
    groupNoteLine(g),
    h("div", { class: "roster" }, g.slots.map((s, i) => s.member
      ? h("div", { class: `slot taken${attClass(s)}` },
          h("span", { class: "slot-num" }, i + 1), rolePill(s.role || s.slotRole),
          slotInfo(s, "Pressed ✅ Ready in the reminder", true), slotTools(
          g.started ? attendanceButton(s, `/lfg/groups/${gid}/attendance`) : null,
          tool("pencil", "Change character", rename(s.member, s.character)),
          (!g.owner || s.member.id !== g.owner.id) ? tool("arrow-right", "Move to reserve", () => roster("toReserve", s.member, "Moved to the reserve.")) : null,
          (!g.owner || s.member.id !== g.owner.id) ? tool("x", "Remove from the group", e => removeWithUndo(e.currentTarget.closest(".slot"), `${s.member.name} removed from the group.`,
            () => gApi("POST", `/lfg/groups/${gid}/roster`, { action: "remove", userId: s.member.id }), refresh)) : null))
      : h("div", { class: "slot open" }, h("span", { class: "slot-num" }, i + 1), rolePill(s.slotRole), slotReqMark(s),
          h("span", { class: "slot-open-text" }, icon("circle-plus", 14), "Open slot")))),
    g.reserve.length ? h("details", { class: "reserve", open: true }, h("summary", {}, icon("clock", 14), `Reserve · ${g.reserve.length}`),
      h("div", { class: "roster" }, g.reserve.map(r => h("div", { class: "slot taken reserve-row" },
        rolePill(r.role || "All"), slotInfo(r, null, true), slotTools(
        tool("arrow-left", "Move into the group (sends a DM)", () => roster("promote", r.member, "Moved into the group, DM sent.")),
        tool("pencil", "Change character", rename(r.member, r.character)),
        tool("x", "Remove from the reserve", e => removeWithUndo(e.currentTarget.closest(".slot"), `${r.member.name} removed from the reserve.`,
          () => gApi("POST", `/lfg/groups/${gid}/roster`, { action: "removeReserve", userId: r.member.id }), refresh))))))) : null,
    h("div", { class: "group-actions" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 15), "Open in Discord"),
      g.voiceUrl ? h("a", { class: "btn secondary voice-btn", href: g.voiceUrl, target: "_blank", rel: "noopener", title: "Open the group's voice room in Discord" }, icon("volume-2", 15), "Voice room") : null,
      h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/lfg/groups/${gid}/time`) }, icon("clock", 15), "Time"),
      h("button", { class: "btn secondary", onclick: () => groupNoteDialog(g, `/lfg/groups/${gid}/note`) }, icon("file-pen-line", 15), "Note"),
      h("span", { class: "spacer" }), close));
}

// ================= Tickets =================

// ----- Sub-project ticket panels: a separate message with a button per ticket type of one project -----

function ticketPanelPreview(panel, types) {
  const chosen = panel.ticketKeys.map(k => types.find(t => t.key === k)).filter(Boolean);
  const rows = [];
  for (let i = 0; i < chosen.length; i += 5) rows.push(chosen.slice(i, i + 5).map(t => dcButton(t.name, "🎫", "Blurple")));
  return dcMessage({ embeds: [{ title: panel.title || "Tickets", description: panel.text || "", color: panel.color || "#5865f2" }], rows });
}

/**
 * Панели подпроектов: внутри вкладки основного проекта (scope = его ключ) — только его панели, новая создаётся в нём;
 * scope = null (General) — панели без проекта (старые), если такие есть.
 */
async function ticketPanelsSection(main, scope = null) {
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(3)));
  main.append(holder);
  async function load() {
    let d;
    try { d = await gApi("GET", "/tickets/panels"); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }
    const mainOf = k => d.groups.find(g => g.key === k);
    const mine = scope ? d.panels.filter(p => p.projectKey === scope) : d.panels.filter(p => !mainOf(p.projectKey));
    if (!scope && !mine.length) { holder.replaceChildren(); return; }
    const proj = scope ? mainOf(scope) : null;
    const add = h("button", { class: "btn" }, icon("plus", 15), "New panel");
    add.addEventListener("click", async () => {
      const title = await promptDialog("New sub-project panel", proj ? `${proj.name} — applications` : "Applications");
      if (!title) return;
      const p = await act(add, () => gApi("POST", "/tickets/panels", { title, projectKey: scope }));
      if (p) ticketPanelEditor({ ...p, projectKey: scope || p.projectKey }, d, load);
    });
    holder.replaceChildren(
      h("div", { class: "card" }, h("div", { class: "card-head" },
          h("h2", {}, proj ? `🧩 ${proj.name} — sub-project panels` : "🧩 Sub-project panels without a project"), scope ? add : null),
        h("p", { class: "muted small" }, proj
          ? `A message with a button for each sub-project of ${proj.name} (GvG application, POV review…). Post it in the project's channel. Discord can't hide buttons per member, so access is checked on click; to hide the panel completely, post it in a channel only the project's members see.`
          : "These panels have no main project — open Edit and choose one, then they move into that project's tab.")),
      mine.length ? h("div", { class: "panel-grid" }, mine.map(p => {
        const ch = channelSelect("text", "Channel…");
        if (p.channelId) ch.value = p.channelId;
        const post = p.jumpUrl ? null : h("button", { class: "btn" }, icon("pin", 15), "Post");
        if (post) post.addEventListener("click", () => act(post, async () => {
          if (!ch.value) return toast("Choose a channel first.", "warn");
          await gApi("POST", `/tickets/panels/${p.id}/post`, { channelId: ch.value });
          toast("Panel posted."); load();
        }));
        const more = menuButton([
          p.jumpUrl ? { icon: "repeat", label: "Re-post", danger: true, onclick: () => act(null, async () => {
            if (!ch.value) return toast("Choose a channel first.", "warn");
            if (!await confirmDialog("Re-post this panel?", "The old message is deleted and the panel is posted in the chosen channel. To change it, just Edit and Save — the posted message is updated in place.", "Re-post")) return;
            await gApi("POST", `/tickets/panels/${p.id}/post`, { channelId: ch.value });
            toast("Panel re-posted."); load();
          }) } : null,
          { icon: "trash-2", label: "Delete panel", danger: true, onclick: () => act(null, async () => {
            if (!await confirmDialog(`Delete “${p.title}”?`, "The posted message is deleted too. Sub-projects and open tickets stay.", "Delete")) return;
            await gApi("DELETE", `/tickets/panels/${p.id}`); toast("Panel deleted."); load();
          }) }
        ].filter(Boolean));
        return h("div", { class: "card panel-card" },
          h("div", { class: "card-head" }, h("h3", {}, p.title),
            p.jumpUrl ? h("a", { class: "chip ok", href: p.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 12), `#${p.channel || "posted"}`)
              : h("span", { class: "chip warn" }, "not posted")),
          h("div", { class: "dc-frame compact" }, ticketPanelPreview(p, d.types)),
          p.ticketKeys.length ? null : h("p", { class: "error-text small" }, "No sub-projects on it yet — Edit and pick them."),
          h("div", { class: "row", style: "margin-top:10px;gap:8px" }, ch, post, h("span", { class: "spacer" }),
            h("button", { class: p.jumpUrl ? "btn" : "btn secondary", onclick: () => ticketPanelEditor(p, d, load) }, icon("pencil", 15), "Edit"), more));
      })) : h("div", { class: "card" }, emptyState("layers", "No panels yet", `Create one, tick the sub-projects and post it in ${proj ? proj.name + "'s" : "the project's"} channel.`)));
  }
  await load();
}

function ticketPanelEditor(p, d, reload) {
  const dlg = dialog();
  dlg.classList.add("xwide");
  dlg.onclose = () => dlg.classList.remove("xwide");
  const v = { title: p.title, text: p.text || "", color: p.color || null, projectKey: p.projectKey || "", ticketKeys: [...(p.ticketKeys || [])] };
  const preview = h("div", { class: "dc-frame" });
  const update = () => preview.replaceChildren(ticketPanelPreview({ ...v, color: color.value() }, d.types));
  const title = h("input", { maxlength: "256", value: v.title, style: "width:100%" });
  title.addEventListener("input", () => { v.title = title.value; update(); });
  const text = h("textarea", { maxlength: "4096", rows: "5", placeholder: "Text above the buttons — what each ticket is for, who can apply", style: "width:100%" });
  text.value = v.text;
  text.addEventListener("input", () => { v.text = text.value; update(); });
  const color = embedColorField(v.color, update);
  const proj = h("select", {}, h("option", { value: "" }, "Any project"), d.groups.map(g => h("option", { value: g.key }, g.name)));
  if (v.projectKey && !d.groups.some(g => g.key === v.projectKey)) v.projectKey = "";
  proj.value = v.projectKey;
  const list = h("div", { class: "check-list" });
  const draw = () => {
    // выбранные — в порядке кнопок, затем остальные системы этого проекта
    const pool = d.types.filter(t => v.projectKey ? t.parentKey === v.projectKey || v.ticketKeys.includes(t.key) : true);
    const ordered = [...v.ticketKeys.map(k => pool.find(t => t.key === k)).filter(Boolean), ...pool.filter(t => !v.ticketKeys.includes(t.key))];
    list.replaceChildren(...(ordered.length ? ordered.map(t => {
      const on = v.ticketKeys.includes(t.key);
      const i = v.ticketKeys.indexOf(t.key);
      const cb = h("input", { type: "checkbox", checked: on });
      cb.addEventListener("change", () => {
        if (cb.checked) { if (v.ticketKeys.length >= 25) { cb.checked = false; return toast("Discord allows 25 buttons per message.", "warn"); } v.ticketKeys.push(t.key); }
        else v.ticketKeys = v.ticketKeys.filter(k => k !== t.key);
        draw(); update();
      });
      const move = dir => { const j = i + dir; if (j < 0 || j >= v.ticketKeys.length) return; [v.ticketKeys[i], v.ticketKeys[j]] = [v.ticketKeys[j], v.ticketKeys[i]]; draw(); update(); };
      return h("div", { class: "row", style: "gap:6px" }, h("label", { class: "row grow", style: "gap:6px" }, cb, t.name),
        on ? h("button", { class: "btn ghost sm", type: "button", title: "Up", onclick: () => move(-1) }, icon("chevron-up", 14)) : null,
        on ? h("button", { class: "btn ghost sm", type: "button", title: "Down", onclick: () => move(1) }, icon("chevron-down", 14)) : null);
    }) : [h("p", { class: "muted small" }, v.projectKey ? "No sub-projects in this project yet — create one with the button below." : "No ticket types yet.")]));
  };
  // Новый тип тикета прямо отсюда: сразу в проекте панели и сразу отмечен
  const newType = h("button", { class: "btn secondary sm", type: "button" }, icon("plus", 14), "New sub-project");
  newType.addEventListener("click", () => {
    const nd = dialog();
    const nm = h("input", { maxlength: "40", placeholder: "e.g. GvG application", style: "width:100%" });
    const role = roleSelect("Role on Approve — none (e.g. a review)");
    const pj = d.groups.find(g => g.key === v.projectKey);
    const ok = h("button", { class: "btn" }, icon("check", 15), "Create");
    ok.addEventListener("click", async () => {
      if (!nm.value.trim()) return toast("Write a name.", "warn");
      const r = await act(ok, () => gApi("POST", "/tickets/projects", { name: nm.value.trim(), memberRoleId: role.value, parentKey: v.projectKey || null }), "Ticket type created.");
      if (r === undefined) return;
      nd.close();
      try { const fresh = await gApi("GET", "/tickets/panels"); d.types = fresh.types; } catch { /* list stays */ }
      if (r && r.key && v.ticketKeys.length < 25) v.ticketKeys.push(r.key);
      draw(); update();
    });
    nd.replaceChildren(h("h2", {}, pj ? `New sub-project of ${pj.name}` : "New ticket type"),
      h("div", { class: "field" }, h("label", {}, "Name (the button text)"), nm),
      h("div", { class: "field" }, h("label", {}, "Role given on Approve (optional)"), role,
        h("span", { class: "muted small" }, "None — the ticket isn't about a role (e.g. a POV review): it gets ✅ Done instead of Approve / Deny.")),
      h("p", { class: "muted small" }, pj ? `It becomes a tab inside ${pj.name} and gets a button on this panel. ` : "",
        "Who can open it, leadership, questions and texts — set them later in its tab on the Tickets page (by default: members of the main project)."),
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => nd.close() }, "Cancel"), ok));
    nd.showModal();
    nm.focus();
  });
  proj.addEventListener("change", () => { v.projectKey = proj.value; draw(); });
  draw(); update();
  const save = h("button", { class: "btn" }, icon("check", 15), "Save");
  save.addEventListener("click", async () => {
    if (await act(save, () => gApi("PUT", `/tickets/panels/${p.id}`, { title: title.value, text: text.value, color: color.value(), projectKey: v.projectKey || null, ticketKeys: v.ticketKeys }),
      p.jumpUrl ? "Saved — the posted panel is updated." : "Saved.") !== undefined) { dlg.close(); reload(); }
  });
  dlg.replaceChildren(h("h2", {}, "Sub-project panel"),
    h("div", { class: "editor-layout" },
      h("div", {},
        h("div", { class: "field" }, h("label", {}, "Title"), title),
        h("div", { class: "field" }, h("label", {}, "Text"), text),
        h("div", { class: "field" }, h("label", {}, "Color"), color.el),
        h("div", { class: "field" }, h("label", {}, "Main project"), proj),
        h("div", { class: "field" }, h("label", {}, "Sub-projects — one button each, up to 25"), list,
          h("div", { class: "row", style: "gap:8px;margin-top:8px;flex-wrap:wrap" }, newType,
            h("span", { class: "muted small" }, "A sub-project is a kind of application inside the main project (GvG application, POV review…) — also a tab inside the project on the Tickets page.")))),
      h("div", { class: "editor-preview" }, h("h3", {}, "Preview"), preview,
        h("p", { class: "muted small" }, "Access is checked when a member clicks: each ticket type's “Who can open”, or its project's roles."))),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => dlg.close() }, "Cancel"), save));
  dlg.showModal();
}

async function pageTickets(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎫 Tickets"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));

  // A tab per ticket project; ⚙️ General — ticket panel, all open tickets, archive
  let projects = [];
  try { projects = await gApi("GET", "/tickets/projects"); } catch (e) { main.append(errorState(e.message)); return; }
  // Основные проекты — вкладки; подпроекты (заявка на ГВГ, разбор POV…) — вкладки внутри своего основного проекта
  const isMain = p => !p.parentKey || !projects.some(x => x.key === p.parentKey);
  const mains = projects.filter(isMain);
  const childrenOf = k => projects.filter(p => p.parentKey === k && !isMain(p));
  let tab = store.get("gb.ticketTab") || (mains[0] ? mains[0].key : "new");
  let sub = store.get("gb.ticketSub") || "";
  const asChild = projects.find(p => p.key === tab && !isMain(p));
  if (asChild) { sub = asChild.key; tab = asChild.parentKey; }
  if (!["new", "general"].includes(tab) && !mains.some(p => p.key === tab)) tab = mains[0] ? mains[0].key : "new";
  const openOf = p => p.open + childrenOf(p.key).reduce((n, c) => n + c.open, 0);
  main.append(tabsRow([...mains.map(p => [p.key, `📁 ${p.name}${openOf(p) ? ` (${openOf(p)})` : ""}`]), ["new", "➕ New project"], ["general", "⚙️ General"]],
    tab, "gb.ticketTab"));


  if (tab === "new") {
    const nameIn = h("input", { placeholder: "Project name, e.g. Aion 2", maxlength: "100" });
    const roleIn = roleSelect("Role on Approve (optional)…");
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
      h("p", { class: "muted small" }, "A main project: applicants pick it in the main ticket panel (e.g. joining WWM or Aion 2). Applications inside a project (GvG, POV review…) are sub-projects — open the project's tab → ➕ Sub-project."),
      h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn)));
    return;
  }

  const mainProject = mains.find(p => p.key === tab);
  const kids = mainProject ? childrenOf(mainProject.key) : [];
  if (mainProject && sub !== "+sub" && sub !== "#panels" && !kids.some(c => c.key === sub)) sub = "";
  if (mainProject) {
    // Вкладки внутри проекта: основной (по умолчанию) · подпроекты · ➕ подпроект
    main.append(h("div", { class: "tabs sub-tabs" },
      [["", `${mainProject.name} · main`, mainProject.open], ...kids.map(c => [c.key, `↳ ${c.name}`, c.open]), ["+sub", "➕ Sub-project", 0], ["#panels", "🧩 Panels", 0]].map(([k, label, n]) =>
        h("button", { class: `btn secondary sm${k === sub ? " active-tab" : ""}`, onclick: async () => { if (!await leavePage()) return; store.set("gb.ticketSub", k); renderShell(); } },
          label, n ? h("span", { class: "tab-count" }, String(n)) : null))));
  }
  if (mainProject && sub === "#panels") { await ticketPanelsSection(main, mainProject.key); return; }
  if (mainProject && sub === "+sub") {
    const nameIn = h("input", { placeholder: "e.g. GvG application", maxlength: "100" });
    const roleIn = roleSelect("Role on Approve — none (e.g. a review)");
    const addBtn = h("button", { class: "btn" }, "➕ Add sub-project");
    addBtn.addEventListener("click", async () => {
      const r = await act(addBtn, () => gApi("POST", "/tickets/projects", { name: nameIn.value, memberRoleId: roleIn.value, parentKey: mainProject.key }),
        "Sub-project added.");
      if (r === undefined) return;
      store.set("gb.ticketSub", r && r.key ? r.key : "");
      refresh();
    });
    main.append(h("div", { class: "card" }, h("h3", {}, `New sub-project of ${mainProject.name}`),
      h("p", { class: "muted small" }, `A separate kind of application inside ${mainProject.name} — e.g. GvG application or POV review. It isn't shown in the main ticket panel: members open it from a panel (🧩 Panels tab of this project). By default only members of ${mainProject.name} (its Approve roles) and its leadership can open it. Without a role on Approve (e.g. a mentor reviewing your POV) the ticket gets a ✅ Done button instead of Approve / Deny.`),
      h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), roleIn, addBtn)));
    return;
  }
  const project = mainProject ? (kids.find(c => c.key === sub) || mainProject) : null;
  const openBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  if (project) main.append(projectCard(project, mains, kids.length > 0 && project === mainProject));
  else main.append(postPanelCard("Ticket panel", "Posts the message with the button to open a ticket (ticket types without a project — sub-project types are in their own panels).", "/tickets/panel"));
  main.append(h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, project ? `Open tickets · ${project.name}` : "Open tickets")), openBody));
  if (!project) { main.append(ticketArchiveCard(mains)); ticketPanelsSection(main, null); }
  else if (project === mainProject) main.append(ticketArchiveCard(mains, mainProject));

  try {
    let open = await gApi("GET", "/tickets/open");
    if (project) open = open.filter(t => t.project === project.name);
    // Row: Approve / Deny + "⋯" (Open, Force close). Click the row → the ticket card with the form answers.
    openBody.replaceChildren(open.length ? h("div", { class: "table-wrap" }, h("table", { class: "table clickable-rows" },
      h("thead", {}, h("tr", {}, h("th", {}, "Applicant"), h("th", {}, "Project"), h("th", {}, "Status"), h("th", {}, "Opened"), h("th", {}))),
      h("tbody", {}, open.map(t => {
        const a = ticketActions(t);
        const row = h("tr", { title: "Open the ticket card", onclick: e => { if (!e.target.closest("button, a, .menu-pop")) ticketCard(t); } },
          h("td", {}, member(t.applicant), t.answers.length ? h("div", { class: "muted small" }, icon("clipboard-list", 12), ` ${t.answers.length} answers`) : null),
          h("td", {}, t.project),
          h("td", {}, ticketStatus(t)),
          h("td", { title: new Date(t.createdAt).toLocaleString() }, fmtRelative(t.createdAt)),
          h("td", { class: "actions" }, h("div", { class: "row-actions" }, a.approve, a.deny, a.more)));
        return row;
      })))) : emptyState("inbox", "No open tickets", "New applications appear here."));
  } catch (e) { openBody.replaceChildren(errorState(e.message)); }
}

const ticketStatus = t => t.approvedBy ? h("span", { class: "badge ok" }, icon("check", 12), "Approved") : h("span", { class: "badge wait" }, icon("clock", 12), "Pending");

/** Approve / Deny / ⋯ (Open, Force close) for an open ticket — used in the table row and in the ticket card. */
function ticketActions(t, after) {
  const tid = encodeURIComponent(t.id);
  const done = () => { if (after) after(); refresh(); };
  const approve = t.approvedBy ? null : h("button", { class: "btn sm" }, icon("check", 15), "Approve");
  if (approve) approve.addEventListener("click", async () => {
    if (!await confirmDialog(`Approve ${t.applicant.name}?`, `They get the roles of ${t.project}, a DM, and the ticket closes in 15 seconds.`, "Approve", false)) return;
    const res = await act(approve, () => gApi("POST", `/tickets/open/${tid}/approve`));
    if (res === undefined) return;
    toast(res.closed ? "Approved — roles given, the ticket closes in 15 seconds." : res.warning, res.closed ? "ok" : "warn");
    done();
  });
  const deny = t.approvedBy ? null : h("button", { class: "btn secondary sm" }, icon("x", 15), "Deny");
  if (deny) deny.addEventListener("click", () => {
    const d = dialog();
    const reason = h("textarea", { maxlength: "500", placeholder: "Optional — sent to the applicant by DM", style: "min-height:100px" });
    const ok = h("button", { class: "btn danger" }, icon("x", 15), "Deny application");
    ok.addEventListener("click", async () => {
      const res = await act(ok, () => gApi("POST", `/tickets/open/${tid}/deny`, { reason: reason.value }), "Denied — DM sent, the ticket closes in 15 seconds.");
      if (res !== undefined) { d.close(); done(); }
    });
    d.replaceChildren(h("h2", {}, `Deny ${t.applicant.name}?`),
      h("div", { class: "field" }, h("label", {}, "Reason (sent to the applicant)"), reason),
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), ok));
    d.showModal();
    reason.focus();
  });
  const more = menuButton([
    { icon: "external-link", label: "Open in Discord", href: t.jumpUrl },
    { icon: "trash-2", label: "Force close", danger: true, onclick: async () => {
      if (!await confirmDialog("Close this ticket?", `The ticket channels of ${t.applicant.name} will be deleted (no outcome is recorded).`, "Close ticket")) return;
      if (await act(null, () => gApi("POST", `/tickets/open/${tid}/close`), "Ticket closed.") !== undefined) done();
    } }
  ]);
  return { approve, deny, more };
}

/** Ticket card: who, project, status, form answers and the actions. */
function ticketCard(t) {
  const d = dialog();
  const a = ticketActions(t, () => d.close());
  d.classList.add("wide");
  d.replaceChildren(
    h("div", { class: "card-head" }, h("h2", { class: "with-icon" }, toneChip("ticket", 16), `${t.project} — application`),
      h("button", { class: "btn ghost icon-only sm", "aria-label": "Close", onclick: () => d.close() }, icon("x", 16))),
    h("div", { class: "group-meta", style: "margin-bottom:14px" },
      h("div", { class: "meta-item" }, icon("user-round", 14), member(t.applicant)),
      h("div", { class: "meta-item" }, icon("clock", 14), `opened ${fmtRelative(t.createdAt)}`),
      ticketStatus(t),
      t.approvedBy ? h("div", { class: "meta-item" }, "by ", member(t.approvedBy)) : null),
    h("div", { class: "guide-body" }, t.answers.length
      ? h("div", { class: "answers" }, t.answers.map(x => h("div", { class: "answer" }, h("div", { class: "muted small" }, x.question), h("div", {}, x.answer || "—"))))
      : h("p", { class: "muted" }, "No application form answers.")),
    h("div", { class: "actions" }, a.more, h("span", { style: "flex:1" }), a.deny, a.approve));
  d.onclose = () => d.classList.remove("wide");
  d.showModal();
}

/** "⋯" button with a small menu (items: {icon, label, onclick | href, danger}). */
function menuButton(items) {
  const wrap = h("span", { class: "menu-wrap" });
  const btn = h("button", { type: "button", class: "btn ghost icon-only sm", "aria-label": "More actions", title: "More" }, icon("ellipsis", 18));
  let pop = null;
  btn.addEventListener("click", e => {
    e.stopPropagation();
    if (pop) return close();
    pop = h("div", { class: "menu-pop" }, items.map(it => it.href
      ? h("a", { class: "menu-item", href: it.href, target: "_blank", rel: "noopener", onclick: close }, icon(it.icon, 15), it.label)
      : h("button", { type: "button", class: `menu-item${it.danger ? " danger" : ""}`, onclick: () => { close(); it.onclick(); } }, icon(it.icon, 15), it.label)));
    // Mount outside cards: their backdrop-filter would trap a fixed popup and clip it.
    (btn.closest("dialog") || document.body).append(pop);
    const r = btn.getBoundingClientRect(), ph = pop.offsetHeight, pw = pop.offsetWidth;
    const up = r.bottom + 6 + ph > innerHeight - 8 && r.top - 6 - ph > 8;
    pop.style.top = `${up ? r.top - 6 - ph : r.bottom + 6}px`;
    pop.style.left = `${Math.max(8, Math.min(innerWidth - pw - 8, r.right - pw))}px`;
    setTimeout(() => {
      document.addEventListener("mousedown", outside, true);
      addEventListener("scroll", close, true);
      addEventListener("resize", close);
    });
  });
  const outside = e => { if (pop && !pop.contains(e.target) && !btn.contains(e.target)) close(); };
  function close() {
    pop?.remove(); pop = null;
    document.removeEventListener("mousedown", outside, true);
    removeEventListener("scroll", close, true);
    removeEventListener("resize", close);
  }
  wrap.append(btn);
  return wrap;
}

/** Подсказка «?»: короткое объяснение по наведению/фокусу вместо абзаца серого текста под полем. */
function hint(text) {
  return h("span", { class: "hint", tabindex: "0", "data-tip": text, "aria-label": text }, icon("circle-help", 14));
}

// "?" hints: one tooltip on top of everything (pinned heading, glass cards, dialogs), placed above or below — whichever fits
(function hintTip() {
  const tip = h("div", { class: "hint-tip", role: "tooltip" });
  document.body.append(tip);
  let cur = null;
  const show = el => {
    cur = el;
    tip.textContent = el.dataset.tip || "";
    tip.classList.add("on");
    const r = el.getBoundingClientRect(), t = tip.getBoundingClientRect();
    const head = (document.querySelector(".topbar") || { getBoundingClientRect: () => ({ bottom: 0 }) }).getBoundingClientRect().bottom;
    if (el.closest(".nav")) { // sidebar counters: to the right of the menu
      tip.style.left = `${Math.min(el.closest(".nav").getBoundingClientRect().right + 10, innerWidth - t.width - 8)}px`;
      tip.style.top = `${Math.max(head + 6, Math.min(r.top + r.height / 2 - t.height / 2, innerHeight - t.height - 8))}px`;
      return;
    }
    const below = r.top - t.height - 10 < head + 6;
    tip.style.left = `${Math.max(8, Math.min(r.left - 10, innerWidth - t.width - 8))}px`;
    tip.style.top = `${below ? r.bottom + 10 : r.top - t.height - 10}px`;
    tip.classList.toggle("below", below);
  };
  const hide = () => { cur = null; tip.classList.remove("on"); };
  const TIP = ".hint[data-tip], .nav-badge[data-tip], .nav-count[data-tip]";
  document.addEventListener("pointerover", e => { const el = e.target.closest && e.target.closest(TIP); if (el && el !== cur) show(el); else if (!el && cur) hide(); });
  document.addEventListener("focusin", e => { const el = e.target.closest && e.target.closest(".hint[data-tip]"); if (el) show(el); });
  document.addEventListener("focusout", hide);
  addEventListener("scroll", hide, { passive: true, capture: true });
})();

/** Подпись поля с подсказкой. */
const fieldLabel = (text, tip) => h("label", { class: "field-label" }, text, tip ? hint(tip) : null);

/**
 * Разделы длинной формы: сегментированный переключатель + панели (все остаются в DOM — сохранение и «несохранено» работают как раньше).
 * sections: [[key, icon, label, panel, badge?]]; выбранный раздел запоминается по storeKey.
 */
function sectionTabs(sections, storeKey) {
  let cur = store.get(storeKey);
  if (!sections.some(s => s[0] === cur)) cur = sections[0][0];
  const buttons = sections.map(([k, ic, label, , badge]) => h("button", { type: "button", class: "seg-btn", "data-k": k },
    icon(ic, 15), label, badge ? h("span", { class: "seg-badge" }, badge) : null));
  const show = k => {
    cur = k; store.set(storeKey, k);
    buttons.forEach(b => b.classList.toggle("active", b.dataset.k === k));
    sections.forEach(([sk, , , panel]) => {
      panel.style.display = sk === k ? "" : "none";
      if (sk === k) { panel.classList.remove("tab-in"); void panel.offsetWidth; panel.classList.add("tab-in"); } // replay the fade-in
    });
  };
  buttons.forEach(b => b.addEventListener("click", () => show(b.dataset.k)));
  show(cur);
  return { nav: h("div", { class: "seg" }, buttons), panels: sections.map(s => s[3]) };
}

function projectCard(p, mains = [], hasChildren = false) {
  const nameIn = h("input", { maxlength: "100", value: p.name, style: "font-weight:600;min-width:200px" });
  // Основной проект (для подпроекта) и кто может открыть
  const parentSel = h("select", {}, h("option", { value: "" }, "None — a main project (in the main ticket panel)"),
    mains.filter(m => m.key !== p.key).map(m => h("option", { value: m.key }, `Sub-project of ${m.name}`)));
  parentSel.value = p.parentKey || "";
  if (hasChildren) parentSel.disabled = true;
  // «Нужны ВСЕ роли» имеет смысл только при 2+ ролях (напр. разбор POV — только у кого есть и WWM, и GW)
  const openUsers = userListEditor(p.openUsers || [], () => openRoles.redraw && openRoles.redraw());
  const staffUsers = userListEditor(p.staffUsers || [], () => { preview.refresh(); staff.redraw(); });
  const openRoles = roleListEditor(p.openRoles || [], state.roles, () => syncRequireAll(), null, "➕ Add roles…", openUsers);
  const requireAll = toggle(!!p.openRequireAll, "Require ALL of these roles (off — any one of them is enough)");
  const syncRequireAll = () => { requireAll.el.style.display = openRoles.value().length >= 2 ? "" : "none"; };
  syncRequireAll();
  const staff = roleListEditor(p.staffRoles, state.roles, () => preview.refresh(), null, "➕ Add roles…", staffUsers);
  const members = roleListEditor(p.memberRoles, state.roles, () => preview.refresh());
  const cat = channelSelect("categories", "Default category “Tickets”");
  if (p.category) cat.value = p.category.id;
  const text = h("textarea", { maxlength: "4000", placeholder: "Markdown works. {applicant} = applicant mention, {project} = project name" });
  text.value = p.customText || "";
  const counter = h("span", { class: "muted small" }, `${text.value.length}/4000`);
  text.addEventListener("input", () => { counter.textContent = `${text.value.length}/4000`; });

  const form = questionEditor(p);
  const autoClose = h("input", { type: "number", min: "2", max: "90", placeholder: "off", value: p.autoCloseDays ?? "", style: "width:100px" });
  const preview = livePreview(() => gApi("POST", `/tickets/projects/${encodeURIComponent(p.key)}/preview`, {
    staffRoleIds: staff.value(), memberRoleIds: members.value(), customText: text.value, name: nameIn.value,
    formEnabled: form.enabled(), questions: form.value()
  }), "Ticket message preview");

  const save = h("button", { class: "btn" }, icon("check", 15), "Save");
  const body = () => ({
    staffRoleIds: staff.value(), memberRoleIds: members.value(), categoryId: cat.value || null, customText: text.value,
    formEnabled: form.enabled(), questions: form.value(), name: nameIn.value,
    autoCloseDays: autoClose.value === "" ? null : Number(autoClose.value),
    parentKey: parentSel.value, openRoleIds: openRoles.value(), openRequireAll: requireAll.cb.checked,
    openUserIds: openUsers.value(), staffUserIds: staffUsers.value()
  });
  const tracker = track(`Project ${p.name}`, body, async () => {
    const res = await act(save, () => gApi("PUT", `/tickets/projects/${encodeURIComponent(p.key)}`, body()));
    if (res === undefined) return false;
    toast(`Project ${nameIn.value.trim() || p.name} saved.`);
    if ((nameIn.value.trim() && nameIn.value.trim() !== p.name) || (parentSel.value || "") !== (p.parentKey || "")) {
      if (parentSel.value) { store.set("gb.ticketTab", parentSel.value); store.set("gb.ticketSub", p.key); }
      else if (p.parentKey) { store.set("gb.ticketTab", p.key); store.set("gb.ticketSub", ""); }
      refresh();
    }
    if (res.warning) toast(res.warning, "warn");
    if (res.ticketsTotal) toast(`Updated ${res.ticketsUpdated} of ${res.ticketsTotal} open tickets.`, res.ticketsUpdated === res.ticketsTotal ? "ok" : "warn");
    return true;
  });
  save.addEventListener("click", tracker.save);
  // Удаление — в меню «⋯», а не рядом с «Save»
  const more = menuButton([{ icon: "trash-2", label: "Remove project", danger: true, onclick: async () => {
    if (!await confirmDialog(`Remove ${p.name}?`, "New tickets for this project can't be created. Open tickets stay until they are closed.", "Remove project")) return;
    if (await act(null, () => gApi("DELETE", `/tickets/projects/${encodeURIComponent(p.key)}`), "Project removed.") !== undefined) refresh();
  } }]);
  const parentName = (mains.find(m => m.key === p.parentKey) || {}).name;

  // Разделы: Доступ · Сообщение тикета · Анкета · Каналы
  const access = h("div", { class: "form-section" },
    h("div", { class: "grid2" },
      h("div", { class: "field" }, fieldLabel("Who can open", (p.parentKey ? "Empty — members of the main project (its Approve roles) and its leadership." : "Empty — everyone.") +
          " Only people and no roles — only those people. The applicant always sees their own ticket."), openRoles.el, requireAll.el),
      h("div", { class: "field" }, fieldLabel("Leadership", "See the ticket channels, get pinged, can Approve / Done. Roles and/or separate people (e.g. a mentor). Changes apply to new tickets."),
        staff.el)),
    h("div", { class: "grid2" },
      h("div", { class: "field" }, fieldLabel("Roles given on Approve", "Empty — the ticket isn't about a role (e.g. a mentor reviews your POV): it gets ✅ Done instead of Approve / Deny."), members.el),
      h("div", { class: "field" }, fieldLabel("Main project", hasChildren ? "It has sub-projects, so it stays a main project."
          : "A sub-project is a tab inside its main project and isn't in the main ticket panel — post it from the main project's 🧩 Panels tab."), parentSel)));
  const message = h("div", { class: "form-section" },
    h("div", { class: "field" }, fieldLabel("Ticket text", "Shown under the system info, above the buttons. Markdown works; {applicant} — the applicant, {project} — the project name."), text,
      h("div", { class: "row", style: "justify-content:flex-end" }, counter)));
  const formPanel = h("div", { class: "form-section" }, form.el);
  const channels = h("div", { class: "form-section" },
    h("div", { class: "grid2" },
      h("div", { class: "field" }, fieldLabel("Category for ticket channels", "Sub-projects without their own category use the main project's."), cat),
      h("div", { class: "field" }, fieldLabel("Auto-close after (days)", "Empty = never. The bot warns 24 h before closing; any message in the ticket resets the timer."), autoClose)));
  const qCount = (p.questions || []).length;
  const tabs = sectionTabs([
    ["access", "key-round", "Access", access],
    ["message", "message-square-text", "Message", message],
    ["form", "clipboard-list", "Form", formPanel, p.formEnabled && qCount ? String(qCount) : null],
    ["channels", "folder", "Channels", channels]
  ], "gb.ticketSection");

  const card = h("div", { class: "card project-card", style: "margin:0" },
    h("div", { class: "project-head" },
      h("div", { class: "project-title" }, nameIn,
        h("div", { class: "row", style: "gap:6px;flex-wrap:wrap" },
          p.parentKey ? h("span", { class: "chip" }, icon("corner-down-right", 12), `Sub-project of ${parentName || "?"}`) : h("span", { class: "chip" }, icon("folder", 12), "Main project"),
          h("span", { class: `chip ${p.open ? "warn" : ""}` }, `${p.open} open`),
          members.value().length ? null : h("span", { class: "chip" }, "✅ Done tickets"))),
      h("div", { class: "row", style: "gap:6px" }, save, more)),
    tabs.nav, ...tabs.panels);
  // Any edit in the form (name, text, questions) refreshes the preview
  card.addEventListener("input", () => preview.refresh());
  card.addEventListener("change", () => preview.refresh());
  card.addEventListener("click", e => { if (e.target.closest("button")) preview.refresh(); });
  preview.now();
  return tracker.attach(h("div", { class: "editor-layout", style: "margin-bottom:16px" }, card, h("div", { class: "editor-preview" }, preview.el)));
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
      // 🎂 Вопрос о дне рождения: ответ ДД.ММ проверяется и сразу сохраняется в «Дни рождения»
      const bd = h("input", { type: "checkbox", checked: !!q.birthday });
      const syncBd = () => { long.disabled = max.disabled = bd.checked; if (bd.checked) { long.checked = q.long = false; } };
      bd.addEventListener("change", () => {
        q.birthday = bd.checked;
        if (bd.checked && !hint.value) { hint.value = q.placeholder = "DD.MM, e.g. 05.02"; }
        if (bd.checked && !label.value) { label.value = q.label = "Your birthday (DD.MM, optional)"; }
        // ДР по умолчанию необязателен — человек может пропустить (галочка «required» включает обязательность)
        if (bd.checked) { req.checked = false; q.required = false; }
        syncBd();
      });
      syncBd();
      const up = h("button", { class: "btn ghost", type: "button", title: "Up", disabled: i === 0, onclick: () => { [qs[i - 1], qs[i]] = [qs[i], qs[i - 1]]; draw(); } }, "↑");
      const del = h("button", { class: "btn ghost icon-only sm danger-text", type: "button", title: "Remove", "aria-label": "Remove", onclick: () =>
        removeLocal(qs, i, draw, "Question removed (after Save).") }, icon("trash-2", 15));
      return h("div", { class: "question-row" }, h("span", { class: "q-num" }, `${i + 1}`), label, hint,
        h("div", { class: "q-opts" },
          h("label", { class: "row small" }, req, "required"), h("label", { class: "row small" }, long, "long"),
          h("label", { class: "row small", title: "Birthday question: the answer must be a date (DD.MM) and is saved to Birthdays right away" }, bd, "🎂 birthday"),
          h("label", { class: "row small muted", title: "Max length" }, "max", max), h("span", { class: "spacer" }), up, del));
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
        h("span", { class: "muted small" }, "Answers appear in the ticket, on the site and in the archive. Tick 🎂 birthday on a question to save the answer straight to Birthdays — it's optional unless you tick “required”."))),
    enabled: () => enabled.checked,
    value: () => qs.filter(q => (q.label || "").trim()).map(q => ({
      id: q.id || null, label: q.label.trim(), placeholder: q.placeholder || null, long: !!q.long, required: q.required !== false, maxLength: q.maxLength || null,
      birthday: !!q.birthday
    }))
  };
}

/** Archive of closed tickets with search. */
/** Архив закрытых тикетов. mains — основные проекты для фильтра; fixedProject — архив одного проекта (он и его подпроекты). */
function ticketArchiveCard(mains = [], fixedProject = null) {
  const OUT = { approved: ["approved", "ok"], denied: ["denied", "lock"], closed: ["closed", ""], "auto-closed": ["auto-closed", "wait"] };
  const outcome = h("select", {}, h("option", { value: "" }, "All outcomes"), Object.keys(OUT).map(k => h("option", { value: k }, k)));
  const projectSel = fixedProject || !mains.length ? null
    : h("select", {}, h("option", { value: "" }, "All projects"), mains.map(m => h("option", { value: m.key }, m.name)));
  if (projectSel) projectSel.value = store.get("gb.archiveProject") || "";
  const q = h("input", { placeholder: "Search: name, ID, project, reason, answers…", style: "flex:1 1 220px" });
  const tbody = h("tbody", {});
  const more = h("button", { class: "btn secondary", style: "display:none" }, "Load more");
  let skip = 0;
  const load = async reset => {
    if (reset) { skip = 0; tbody.replaceChildren(); }
    const params = new URLSearchParams({ skip: String(skip), take: "50" });
    if (outcome.value) params.set("outcome", outcome.value);
    if (q.value.trim()) params.set("q", q.value.trim());
    const proj = fixedProject ? fixedProject.key : projectSel ? projectSel.value : "";
    if (proj) params.set("project", proj);
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
        h("td", {}, h("b", {}, e.userName),
          h("div", { class: "muted small" }, e.project),
          h("div", { class: "muted small", style: "cursor:pointer;font-family:monospace", title: "Click to copy the user ID",
            onclick: () => navigator.clipboard?.writeText(e.userId).then(() => toast("User ID copied."), () => {}) }, `ID ${e.userId}`)),
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
  if (projectSel) projectSel.addEventListener("change", () => { store.set("gb.archiveProject", projectSel.value); load(true); });
  q.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => load(true), 300); });
  more.addEventListener("click", () => load(false));
  load(true);
  return h("div", { class: "card" }, h("h2", {}, fixedProject ? `🗄️ Archive · ${fixedProject.name} (with sub-projects)` : "🗄️ Archive"),
    h("div", { class: "row", style: "margin-bottom:12px" }, projectSel, outcome, q),
    h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Applicant"), h("th", {}, "Outcome"), h("th", {}, "Reason"), h("th", {}, "Opened → closed"), h("th", {}))), tbody)),
    h("div", { class: "row", style: "margin-top:12px" }, more));
}

// ================= Temp Voice =================

async function pageVoice(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔊 Temp Voice"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));

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

  const createCards = (
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
        h("div", { class: "row" }, eLim, eBtn))));

  const creatorsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  const roomsBody = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  // Разделы: активные комнаты · каналы «зайди и создай» (список + создание) · панель управления комнатой
  const voiceSec = sectionTabs([
    ["rooms", "volume-2", "Active rooms", h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "Active rooms")), roomsBody)],
    ["creators", "headphones", "'Join to create' channels", h("div", {},
      h("div", { class: "card" }, h("div", { class: "card-head" }, h("h2", {}, "'Join to create' channels")), creatorsBody), createCards)],
    ["panel", "pin", "Room panel", postPanelCard("Room control panel", "Posts the panel members use to manage their own room.", "/voice/panel")]
  ], "gb.voiceSection");
  main.append(voiceSec.nav, ...voiceSec.panels);

  try {
    const creators = await gApi("GET", "/voice/creators");
    creatorsBody.replaceChildren(creators.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Channel"), h("th", {}, "Room name"), h("th", {}, "Limit"), h("th", {}, "Rooms appear"), h("th", {}))),
      h("tbody", {}, creators.map(creatorRow)))) : empty("None yet."));
  } catch (e) { creatorsBody.replaceChildren(errorState(e.message)); }

  try {
    const rooms = await gApi("GET", "/voice/rooms");
    roomsBody.replaceChildren(rooms.length ? h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, "Room"), h("th", {}, "Owner"), h("th", {}, "Inside"), h("th", {}))),
      h("tbody", {}, rooms.map(roomRow)))) : empty("No active rooms."));
  } catch (e) { roomsBody.replaceChildren(errorState(e.message)); }
}

const ROOM_PLACEMENTS = [
  ["below", "⬇ Under the creator", "Right below the 'join to create' channel (older rooms higher)"],
  ["above", "⬆ Above the creator", "Right above the 'join to create' channel"],
  ["top", "⏫ Top of the category", "First in the category"],
  ["bottom", "⏬ Bottom of the category", "Last in the category"]
];
const DELETE_DELAYS = [[0, "Right away"], [15, "15 seconds"], [30, "30 seconds"], [60, "1 minute"], [120, "2 minutes"], [300, "5 minutes"], [600, "10 minutes"]];

/** ⚙ Settings of a 'join to create' channel: where rooms appear, their category, bitrate, delete delay, … */
function creatorSettingsDialog(c) {
  const d = dialog();
  const place = h("div", { class: "seg", style: "flex-wrap:wrap" });
  let placement = c.placement || "below";
  const drawPlace = () => place.replaceChildren(...ROOM_PLACEMENTS.map(([v, label, hint]) =>
    h("button", { type: "button", class: `seg-btn${placement === v ? " active" : ""}`, title: hint, onclick: () => { placement = v; drawPlace(); } }, label)));
  drawPlace();
  const cat = channelSelect("categories", "Same category as the creator");
  cat.value = c.roomCategoryId || "";
  const bitrate = h("input", { type: "number", min: "8", max: String(c.maxBitrateKbps || 96), value: c.bitrateKbps ?? "",
    placeholder: c.creatorBitrateKbps ? `${c.creatorBitrateKbps} (as the creator)` : "as the creator", style: "width:170px" });
  const delay = h("select", {}, DELETE_DELAYS.map(([v, l]) => h("option", { value: String(v) }, l)));
  delay.value = String(DELETE_DELAYS.some(([v]) => v === c.deleteDelay) ? c.deleteDelay : 0);
  const maxRooms = h("input", { type: "number", min: "0", max: "100", value: c.maxRooms || "", placeholder: "no limit", style: "width:120px" });
  const locked = toggle(!!c.startLocked, "🔒 Rooms start locked — only invited members can join");
  const memberPrefs = toggle(c.allowMemberPrefs !== false, "👤 Members' own settings win (name, limit, lock)");
  const panel = toggle(c.postPanel !== false, "🎛️ Post the control panel in the room's chat");
  const save = h("button", { class: "btn" }, "💾 Save");
  save.addEventListener("click", async () => {
    const body = { placement, roomCategoryId: cat.value || null, bitrateKbps: bitrate.value === "" ? null : Number(bitrate.value),
      deleteDelay: Number(delay.value), startLocked: locked.cb.checked, allowMemberPrefs: memberPrefs.cb.checked, postPanel: panel.cb.checked,
      maxRooms: maxRooms.value === "" ? 0 : Number(maxRooms.value) };
    if (await act(save, () => gApi("PUT", `/voice/creators/${c.channelId}/settings`, body), "Settings saved. Open rooms are moved right away.") !== undefined) { d.close(); refresh(); }
  });
  d.replaceChildren(h("h2", {}, `⚙ ${c.name || "Join to create"}`),
    h("div", { class: "field" }, h("label", {}, "Where new rooms appear"), place,
      h("span", { class: "muted small" }, "Your own channels stay where you put them in Discord; only the temporary rooms are placed.")),
    h("div", { class: "field" }, h("label", {}, "Category for the rooms"), cat,
      h("span", { class: "muted small" }, "Another category: rooms go to its top (⬆ / ⏫) or bottom (⬇ / ⏬). Permissions still come from the creator channel.")),
    h("div", { class: "grid2" },
      h("div", { class: "field" }, h("label", {}, `Bitrate, kbps (up to ${c.maxBitrateKbps || 96} on this server)`), bitrate),
      h("div", { class: "field" }, h("label", {}, "Delete an empty room"), delay,
        h("span", { class: "muted small" }, "Someone comes back in time — the room stays."))),
    h("div", { class: "field" }, h("label", {}, "Rooms at the same time (from this channel)"), maxRooms,
      h("span", { class: "muted small" }, "When the limit is reached, joining the channel doesn't create a new room.")),
    locked.el, memberPrefs.el, panel.el,
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  d.showModal();
}

function creatorRow(c) {
  const tpl = h("input", { placeholder: "auto", maxlength: "100", value: c.template || "" });
  const lim = h("input", { type: "number", min: "0", max: "99", style: "width:80px", value: c.limit ?? "" });
  const save = h("button", { class: "btn secondary" }, "Save");
  const tracker = track(`🔊 ${c.name}`, () => [tpl.value, lim.value], async () => await act(save, () => gApi("PUT", `/voice/creators/${c.channelId}`, {
    template: tpl.value, limit: lim.value === "" ? null : Number(lim.value)
  }), "Saved.") !== undefined);
  save.addEventListener("click", tracker.save);
  const rm = h("button", { class: "btn ghost" }, "Remove");
  rm.addEventListener("click", async () => {
    if (!await confirmDialog("Stop using this channel?", "The channel itself is not deleted. Existing rooms keep working until they are empty.", "Remove")) return;
    if (await act(rm, () => gApi("DELETE", `/voice/creators/${c.channelId}`), "Removed.") !== undefined) refresh();
  });
  const settings = h("button", { class: "btn secondary", title: "Where rooms appear, their category, bitrate, delete delay…", onclick: () => creatorSettingsDialog(c) }, icon("settings", 15), "Settings");
  const where = (ROOM_PLACEMENTS.find(([v]) => v === (c.placement || "below")) || ROOM_PLACEMENTS[0])[1];
  const extras = [c.roomCategory ? `in ${c.roomCategory}` : null, c.bitrateKbps ? `${c.bitrateKbps} kbps` : null,
    c.deleteDelay ? `deleted after ${DELETE_DELAYS.find(([v]) => v === c.deleteDelay)?.[1] || c.deleteDelay + " s"}` : null,
    c.startLocked ? "🔒 locked" : null, c.maxRooms ? `max ${c.maxRooms}` : null].filter(Boolean);
  return tracker.attach(h("tr", {},
    h("td", {}, c.exists ? `🔊 ${c.name}` : h("span", { class: "chip warn" }, "deleted channel"), c.category ? h("div", { class: "muted small" }, c.category) : null,
      c.rooms ? h("div", { class: "muted small" }, `${c.rooms} open room(s)`) : null),
    h("td", {}, tpl), h("td", {}, lim),
    h("td", { class: "small" }, where, extras.length ? h("div", { class: "muted small" }, extras.join(" · ")) : null),
    h("td", { class: "actions" }, save, " ", settings, " ", rm)));
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


// ================= Discord-like preview (Welcome & Role panels) =================

/** Small Discord-markdown → DOM for previews: **bold**, *italic*, __underline__, ~~strike~~, `code`, # headings, -# small, > quotes,
 *  [text](url), links, mentions (<@id> <@&id> <#id>, by name), <t:unix:F> times, custom emoji. mentions: optional {code: "@name"}. */
function mdPreview(text, mentions = {}) {
  const wrap = h("div", { class: "dc-md" });
  const tsFmt = { t: { hour: "2-digit", minute: "2-digit" }, T: { hour: "2-digit", minute: "2-digit", second: "2-digit" }, d: { day: "2-digit", month: "2-digit", year: "numeric" },
    D: { day: "numeric", month: "long", year: "numeric" }, f: { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit" },
    F: { weekday: "long", day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit" } };
  const inline = (line, el) => {
    const re = /(\*\*[^*]+\*\*|__[^_]+__|~~[^~]+~~|\*[^*\s][^*]*\*|`[^`]+`|\[[^\]]+\]\(https?:\/\/[^)]+\)|<a?:\w+:\d+>|<t:-?\d+(?::[tTdDfFR])?>|<@&?!?\d+>|<#\d+>|@everyone|@here|@NewMember|https?:\/\/\S+)/g;
    let last = 0, m;
    while ((m = re.exec(line))) {
      if (m.index > last) el.append(line.slice(last, m.index));
      const t = m[0];
      if (t.startsWith("**")) inline(t.slice(2, -2), el.appendChild(h("b")));
      else if (t.startsWith("__")) inline(t.slice(2, -2), el.appendChild(h("u")));
      else if (t.startsWith("~~")) inline(t.slice(2, -2), el.appendChild(h("s")));
      else if (t.startsWith("*")) inline(t.slice(1, -1), el.appendChild(h("i")));
      else if (t.startsWith("`")) el.append(h("code", {}, t.slice(1, -1)));
      else if (t.startsWith("[")) el.append(h("span", { class: "dc-link" }, t.slice(1, t.indexOf("]"))));
      else if (/^<a?:/.test(t)) el.append(emojiEl(t, 20));
      else if (t.startsWith("<t:")) {
        const [, unix, style = "f"] = /<t:(-?\d+):?(\w)?>/.exec(t);
        const d = new Date(Number(unix) * 1000);
        let txt;
        if (style === "R") { const min = Math.round((d - Date.now()) / 60000), a = Math.abs(min);
          const v = a < 60 ? `${a} minutes` : a < 1440 ? `${Math.round(a / 60)} hours` : `${Math.round(a / 1440)} days`; txt = min >= 0 ? `in ${v}` : `${v} ago`; }
        else txt = d.toLocaleString(undefined, tsFmt[style] || tsFmt.f);
        el.append(h("span", { class: "dc-time-chip" }, txt));
      }
      else if (t.startsWith("http")) el.append(h("span", { class: "dc-link" }, t));
      else if (t.startsWith("<") || t.startsWith("@")) {
        let name = mentions[t];
        if (!name) { const mm = /<@&(\d+)>|<@!?(\d+)>|<#(\d+)>/.exec(t); name = mm ? mentionName(t, mm[1], mm[2], mm[3]).text : t; }
        el.append(h("span", { class: "dc-mention" }, name));
      }
      last = m.index + t.length;
    }
    if (last < line.length) el.append(line.slice(last));
  };
  let inCode = false, code = null;
  for (const raw of String(text || "").split("\n")) {
    if (raw.trim().startsWith("```")) { if (inCode) { inCode = false; } else { inCode = true; code = h("pre", { class: "dc-code" }); wrap.append(code); } continue; }
    if (inCode) { code.append(raw + "\n"); continue; }
    let line = raw, cls = "";
    if (line.startsWith("-# ")) { cls = "dc-small"; line = line.slice(3); }
    else if (line.startsWith("### ")) { cls = "dc-h3"; line = line.slice(4); }
    else if (line.startsWith("## ")) { cls = "dc-h2"; line = line.slice(3); }
    else if (line.startsWith("# ")) { cls = "dc-h1"; line = line.slice(2); }
    else if (line.startsWith("> ")) { cls = "dc-quote"; line = line.slice(2); }
    else if (/^\s*[-*] /.test(line)) { cls = "dc-li"; line = "• " + line.replace(/^\s*[-*] /, ""); }
    const el = h("div", { class: cls });
    inline(line, el);
    if (!line.length) el.append(" ");
    wrap.append(el);
  }
  return wrap;
}

/** Replace :name: shortcodes with the server/bot emoji code <:name:id>, leaving existing codes and times (21:00) alone. */
function resolveEmojiShortcodes(text, map) {
  if (!text || !map || !map.size || !text.includes(":")) return text;
  return text.replace(/(?<![<\w]):([A-Za-z0-9_]{2,32}):(?!\d)/g, (m, name) => map.get(name.toLowerCase()) || m);
}

/** name → code of server/bot emojis (from the /…/emojis lists), for previews of :name:. */
function emojiMapOf(list) {
  const m = new Map();
  for (const e of list || []) m.set(e.name.toLowerCase(), e.code);
  return m;
}

/** "😀 Insert emoji…" list with images; inserts :name: at the cursor of the field. null when there are no emojis. */
function emojiInsertPicker(field, emojis, onInsert) {
  if (!emojis || !emojis.length) return null;
  const pick = h("select", { style: "width:200px", title: "Insert a server / bot emoji" }, h("option", { value: "" }, "😀 Insert emoji…"),
    emojis.map(e => h("option", { value: e.name, "data-icon": e.code }, `:${e.name}:${e.app ? " (bot)" : ""}`)));
  pick.addEventListener("change", () => {
    if (!pick.value) return;
    const ins = `:${pick.value}:`, st = field.selectionStart ?? field.value.length, en = field.selectionEnd ?? field.value.length;
    field.value = field.value.slice(0, st) + ins + field.value.slice(en);
    field.focus(); field.selectionStart = field.selectionEnd = st + ins.length;
    pick.value = "";
    field.dispatchEvent(new Event("input", { bubbles: true }));
    if (onInsert) onInsert();
  });
  return pick;
}

function emojiEl(code, size = 18) {
  if (!code) return null;
  const m = /^<(a?):([^:]+):(\d+)>$/.exec(code);
  return m ? h("img", { class: "gm-emo", src: `https://cdn.discordapp.com/emojis/${m[3]}.${m[1] ? "gif" : "webp"}?size=48`, alt: "", title: `:${m[2]}:`, style: `width:${size}px;height:${size}px` })
    : h("span", {}, code);
}

function dcEmbed(e, mentions = {}) {
  const fields = (e.fields || []);
  return h("div", { class: "dc-embed", style: `border-left-color:${e.color || "#1e1f22"}` },
    h("div", { class: "dc-embed-main" },
      // Заголовок embed в Discord — без разметки: **, #, -# и т.п. показываются как есть; работают только эмодзи
      e.title ? h("div", { class: "dc-embed-title", style: e.url ? "color:#00a8fc" : null }, plainWithEmoji(e.title)) : null,
      e.description ? mdPreview(e.description, mentions) : null,
      fields.length ? h("div", { class: "dc-fields" }, fields.map(f => h("div", { class: `dc-field${f.inline ? " inline" : ""}` },
        h("div", { class: "dc-field-name" }, mdPreview(f.name, mentions)), mdPreview(f.value, mentions)))) : null,
      mediaUrl(e.image) ? h("img", { class: "dc-embed-image", src: mediaUrl(e.image), alt: "" }) : null,
      e.footer || e.timestamp ? h("div", { class: "dc-embed-footer" }, [e.footer, e.timestamp ? new Date(e.timestamp).toLocaleString() : null].filter(Boolean).join(" • ")) : null),
    mediaUrl(e.thumbnail) ? h("img", { class: "dc-embed-thumb", src: mediaUrl(e.thumbnail), alt: "" }) : e.avatar ? h("span", { class: "dc-embed-thumb dc-avatar-ph" }) : null);
}

/** A Discord message mock: bot header, text, embeds, component rows, reactions. */
/** Текст без разметки (заголовки embed): только коды эмодзи <:name:id> становятся картинками. */
function plainWithEmoji(text) {
  const out = [];
  let last = 0;
  for (const m of String(text).matchAll(/<a?:\w+:\d+>/g)) {
    if (m.index > last) out.push(text.slice(last, m.index));
    out.push(emojiEl(m[0], 20));
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function dcMessage({ content, embed, embeds, rows = [], reactions = [], mentions = {}, images = [] }) {
  const bot = state.bot || { name: "Greatness Bot" };
  // [img:имя] в тексте → вложения под текстом (как бот их отправит)
  if (content && MEDIA_TOKEN.test(content)) {
    images = [...images, ...[...content.matchAll(new RegExp(MEDIA_TOKEN.source, "g"))].map(m => mediaUrl(m[0]) || null)];
    content = content.replace(new RegExp(MEDIA_TOKEN.source + "[ \\t]*\\n?", "g"), "").trim();
  }
  const list = embeds || (embed ? [embed] : []);
  return h("div", { class: "dc-msg" },
    h("div", { class: "dc-avatar" }, bot.avatarUrl ? h("img", { src: bot.avatarUrl, alt: "" }) : null),
    h("div", { class: "dc-body" },
      h("div", { class: "dc-head" }, h("b", {}, bot.name), h("span", { class: "dc-bot" }, "APP"), h("span", { class: "dc-time" }, "Today at 21:00")),
      content ? mdPreview(content, mentions) : null,
      list.map(e => dcEmbed(e, mentions)),
      images.length ? h("div", { class: "dc-attachments" }, images.map(u => u ? h("img", { class: "dc-attach", src: u, alt: "" })
        : h("div", { class: "dc-attach missing" }, "image not found in the media library"))) : null,
      rows.map(r => h("div", { class: "dc-row" }, r)),
      reactions.length ? h("div", { class: "dc-reactions" }, reactions.map(e => h("span", { class: "dc-reaction" }, emojiEl(e, 16), " 1"))) : null));
}

/** Message built by the bot (/preview endpoints) → mock. */
function dcFromPreview(p, opts = {}) {
  const styleColor = { Primary: "Blurple", Secondary: "Grey", Success: "Green", Danger: "Red", Link: "Grey", Premium: "Blurple" };
  const select = c => {
    const options = c.options || [];
    const picked = options.filter(o => o.isDefault);
    const box = h("span", { class: "dc-select", title: options.length ? "Click to see the options" : "" },
      h("span", { class: picked.length ? "dc-select-value" : "" }, picked.length ? picked.map(o => o.label).join(", ") : c.label || "Make a selection"),
      icon("chevron-down", 14));
    const list = options.length ? h("div", { class: "dc-options", style: opts.expand ? "" : "display:none" }, options.slice(0, 8).map(o =>
      h("div", { class: `dc-option${o.isDefault ? " picked" : ""}` }, h("b", {}, o.emoji ? emojiEl(o.emoji, 16) : null, o.emoji ? " " : "", o.label),
        o.description ? h("span", {}, o.description) : null)),
      options.length > 8 ? h("div", { class: "dc-option muted small" }, `+ ${options.length - 8} more`) : null) : null;
    if (list) box.addEventListener("click", () => { list.style.display = list.style.display === "none" ? "" : "none"; });
    return h("div", { class: "dc-dropdown" }, box, list);
  };
  const rows = (p.rows || []).map(row => row.map(c => c.type === "select" ? select(c)
    : h("span", { class: c.disabled ? "dc-disabled" : "" }, dcButton(c.label, c.emoji, styleColor[c.style] || "Grey", !!c.url))));
  return dcMessage({ content: p.content, embeds: p.embeds || [], rows, mentions: p.mentions || {}, reactions: opts.reactions || [], images: opts.images || [] });
}

/** Live preview box: load() → preview DTO; refresh() is debounced. */
function livePreview(load, title = "Preview") {
  const frame = h("div", { class: "dc-frame" }, h("div", { class: "muted small" }, "Loading preview…"));
  const note = h("p", { class: "muted small" });
  let timer = 0, seq = 0;
  const run = async () => {
    const my = ++seq;
    try {
      const p = await load();
      if (my !== seq || !p) return;
      frame.replaceChildren(dcFromPreview(p));
      note.textContent = p.note ? `${p.note} · how the bot will post it.` : "How the bot will post it.";
    } catch (e) { if (my === seq) note.textContent = `Preview: ${e.message}`; }
  };
  const refresh = () => { clearTimeout(timer); timer = setTimeout(run, 350); };
  return { el: h("div", { class: "card preview-card" }, h("h3", {}, title), frame, note), refresh, now: run };
}

function dcButton(label, emoji, color = "Grey", link = false) {
  return h("span", { class: `dc-btn dc-btn-${link ? "link" : color.toLowerCase()}` }, emoji ? emojiEl(emoji, 16) : null, label || "", link ? icon("external-link", 12) : null);
}

/** Text field + clickable placeholder chips that insert at the cursor. */
function withPlaceholders(field, names, onInsert) {
  return h("div", { class: "ph-row" }, h("span", { class: "muted small" }, "Insert:"), names.map(n => h("button", {
    type: "button", class: "chip ph-chip", title: PLACEHOLDER_HELP[n] || "",
    onclick: () => {
      const start = field.selectionStart ?? field.value.length, end = field.selectionEnd ?? field.value.length;
      field.value = field.value.slice(0, start) + n + field.value.slice(end);
      field.focus(); field.selectionStart = field.selectionEnd = start + n.length;
      field.dispatchEvent(new Event("input")); if (onInsert) onInsert();
    }
  }, n)));
}
const PLACEHOLDER_HELP = { "{user}": "Mention of the new member (pings them)", "{name}": "Display name", "{username}": "Discord username",
  "{server}": "Server name", "{members}": "Member count", "{tickets}": "Link to the ticket panel" };
const WELCOME_PH = Object.keys(PLACEHOLDER_HELP);

/** Color: native picker + hex text. */
function embedColorField(value, onChange) {
  const hex = h("input", { maxlength: "7", placeholder: "default", style: "width:100px", value: value || "" });
  const pick = h("input", { type: "color", value: /^#[0-9a-f]{6}$/i.test(value || "") ? value : "#5865f2", style: "width:42px;padding:2px" });
  pick.addEventListener("input", () => { hex.value = pick.value; onChange(); });
  hex.addEventListener("input", () => { if (/^#[0-9a-f]{6}$/i.test(hex.value)) pick.value = hex.value; onChange(); });
  const clear = h("button", { class: "btn ghost sm", type: "button", title: "Default color", onclick: () => { hex.value = ""; onChange(); } }, "↩");
  return { el: h("div", { class: "row", style: "gap:6px" }, pick, hex, clear), value: () => hex.value.trim() || null };
}

/** Emoji: text input + server/bot emoji picker + preview. */
function emojiField(value, emojis, onChange) {
  const input = h("input", { placeholder: "😀 or <:name:id>", style: "width:120px", value: value || "" });
  const prev = h("span", { class: "emoji-prev" }, emojiEl(value));
  const set = v => { input.value = v; prev.replaceChildren(emojiEl(v) || ""); onChange(); };
  input.addEventListener("input", () => { prev.replaceChildren(emojiEl(input.value) || ""); onChange(); });
  const pick = emojis.length ? h("select", { style: "width:150px", title: "Server / bot emoji" }, h("option", { value: "" }, "server emoji…"),
    emojis.map(e => h("option", { value: e.code, "data-icon": e.code }, `:${e.name}:${e.app ? " (bot)" : ""}`))) : null;
  if (pick) pick.addEventListener("change", () => { if (pick.value) { set(pick.value); pick.value = ""; } });
  return { el: h("div", { class: "row", style: "gap:6px;flex-wrap:nowrap" }, prev, input, pick), value: () => input.value.trim() || null };
}

function sectionCard(ic, title, hint, ...body) {
  return h("div", { class: "card editor-block" },
    cardHead(ic, title),
    hint ? h("p", { class: "muted small" }, hint) : null, ...body);
}

function toggle(checked, label, onChange) {
  const cb = h("input", { type: "checkbox", checked });
  if (onChange) cb.addEventListener("change", onChange);
  return { cb, el: h("label", { class: "row", style: "margin:4px 0 10px" }, cb, label) };
}

// ================= Welcome =================

async function pageWelcome(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "👋 Welcome"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(5)));
  main.append(holder);
  let w, emojis = [];
  try {
    w = await gApi("GET", "/welcome");
    try { emojis = await gApi("GET", "/welcome/emojis"); } catch { /* unicode only */ }
  } catch (e) { holder.replaceChildren(errorState(e.message)); return; }

  const sample = {
    "{user}": "@NewMember", "{name}": "NewMember", "{username}": "newmember", "{server}": w.server.name,
    "{members}": String(w.server.members + 1), "{tickets}": w.ticketPanelUrl || "(ticket panel not posted)"
  };
  const fill = t => String(t || "").replace(/\{(user|name|username|server|members|tickets)\}/g, m => sample[m]);

  const previewBox = h("div", { class: "preview-box" });
  let previewTab = "channel";
  const update = () => drawPreview();

  // ----- One message editor (channel or DM) -----
  const messageEditor = (m, defaults) => {
    const en = toggle(m.enabled, "Send this message", update);
    const content = h("textarea", { maxlength: "2000", style: "min-height:70px", placeholder: "Text above the card (here {user} pings the member)", value: m.content || "" });
    const embedOn = toggle(m.useEmbed, "Show a card (embed)", () => { cardBox.style.display = embedOn.cb.checked ? "" : "none"; update(); });
    const title = h("input", { maxlength: "256", placeholder: "Card title", value: m.title || "" });
    const descr = h("textarea", { maxlength: "4000", style: "min-height:110px", placeholder: "Card text (Markdown works)", value: m.description || "" });
    const color = embedColorField(m.color, update);
    const avatar = toggle(m.showAvatar, "Member's avatar in the corner", update);
    const image = h("input", { placeholder: "https://… or 🖼 from the media library (optional)", value: m.imageUrl || "" });
    const footer = h("input", { maxlength: "2048", placeholder: "Small text at the bottom (optional)", value: m.footer || "" });
    for (const f of [content, title, descr, image, footer]) f.addEventListener("input", update);
    const cardBox = h("div", { class: "subcard" },
      h("div", { class: "field" }, h("label", {}, "Title"), title, withPlaceholders(title, WELCOME_PH)),
      h("div", { class: "field" }, h("label", {}, "Text"), descr, withPlaceholders(descr, WELCOME_PH)),
      h("div", { class: "grid2" }, h("div", { class: "field" }, h("label", {}, "Stripe color"), color.el), h("div", { class: "field" }, h("label", {}, " "), avatar.el)),
      h("div", { class: "field" }, h("label", {}, "Image"), withMediaButton(image)),
      h("div", { class: "field" }, h("label", {}, "Footer"), footer, withPlaceholders(footer, WELCOME_PH)));
    cardBox.style.display = m.useEmbed ? "" : "none";
    const reset = h("button", { class: "btn ghost sm", type: "button", title: "Put back the default texts" }, "↩ Default texts");
    reset.addEventListener("click", () => {
      content.value = defaults.content || ""; title.value = defaults.title || ""; descr.value = defaults.description || "";
      footer.value = defaults.footer || ""; embedOn.cb.checked = defaults.useEmbed; cardBox.style.display = defaults.useEmbed ? "" : "none"; update();
    });
    return {
      el: h("div", {}, h("div", { class: "row" }, en.el, h("span", { class: "spacer" }), reset),
        h("div", { class: "field" }, h("label", {}, "Text"), content, withPlaceholders(content, WELCOME_PH)), embedOn.el, cardBox),
      value: () => ({ enabled: en.cb.checked, content: content.value, useEmbed: embedOn.cb.checked, title: title.value, description: descr.value,
        color: color.value(), showAvatar: avatar.cb.checked, imageUrl: image.value, footer: footer.value })
    };
  };

  const channel = channelSelect("text", "Choose a channel…");
  channel.value = w.channelId || "";
  const chMsg = messageEditor(w.channel, w.defaults.channel);
  const dmMsg = messageEditor(w.dm, w.defaults.dm);

  // ----- Buttons -----
  const apply = toggle(w.applyButton, "📝 Apply button — opens the ticket panel", update);
  const applyLabel = h("input", { maxlength: "80", placeholder: "Apply", style: "max-width:220px", value: w.applyLabel || "" });
  applyLabel.addEventListener("input", update);
  let links = w.links.map(l => ({ ...l }));
  const linksBox = h("div", {});
  const drawLinks = () => {
    linksBox.replaceChildren(...links.map((l, i) => {
      const label = h("input", { maxlength: "80", placeholder: "Label, e.g. Rules", value: l.label || "", class: "binding-label" });
      const url = h("input", { placeholder: "https://…", value: l.url || "", class: "binding-url" });
      const em = emojiField(l.emoji, emojis, () => { l.emoji = em.value(); update(); });
      label.addEventListener("input", () => { l.label = label.value; update(); });
      url.addEventListener("input", () => { l.url = url.value; update(); });
      return h("div", { class: "row binding-row" }, em.el, label, url,
        h("button", { class: "btn ghost icon-only sm danger-text", type: "button", title: "Remove", "aria-label": "Remove", onclick: () => { links.splice(i, 1); drawLinks(); update(); } }, icon("trash-2", 15)));
    }), links.length < 4 ? h("button", { class: "btn secondary sm", type: "button", onclick: () => { links.push({ label: "", url: "", emoji: "" }); drawLinks(); } },
      icon("plus", 14), "Add a link button") : null);
  };
  drawLinks();

  // ----- Auto role -----
  const mode = h("select", {}, h("option", { value: "off" }, "Off — don't give any role (default)"), h("option", { value: "join" }, "Give when a member joins"));
  mode.value = w.autoRoleMode || "off";
  const roles = roleListEditor(w.autoRoles, state.roles);
  const delay = h("input", { type: "number", min: "0", max: "600", style: "width:100px", value: w.autoRoleDelay || 0 });
  const skipBots = toggle(w.skipBots, "Don't give auto roles to bots");
  const roleBox = h("div", {}, h("div", { class: "field" }, h("label", {}, "Roles"), roles.el),
    h("div", { class: "row", style: "margin-bottom:8px" }, h("span", { class: "muted" }, "Delay"), delay, h("span", { class: "muted" }, "seconds after joining (0 = at once)")), skipBots.el);
  const syncMode = () => { roleBox.style.display = mode.value === "join" ? "" : "none"; };
  mode.addEventListener("change", syncMode); syncMode();

  const body = () => ({
    channelId: channel.value || null, channel: chMsg.value(), dm: dmMsg.value(),
    applyButton: apply.cb.checked, applyLabel: applyLabel.value, links: links.filter(l => l.label || l.url),
    autoRoleMode: mode.value, autoRoleIds: roles.value(), autoRoleDelay: Number(delay.value || 0), skipBots: skipBots.cb.checked
  });

  function drawPreview() {
    const b = body();
    const m = previewTab === "channel" ? b.channel : b.dm;
    const btns = [];
    if (b.applyButton) btns.push(dcButton(b.applyLabel || "Apply", "📝", "Grey", true));
    for (const l of b.links.filter(l => l.label)) btns.push(dcButton(l.label, l.emoji, "Grey", true));
    const tabs = h("div", { class: "seg" }, [["channel", "# Channel"], ["dm", "✉ DM"]].map(([k, label]) =>
      h("button", { type: "button", class: `seg-btn${previewTab === k ? " active" : ""}`, onclick: () => { previewTab = k; drawPreview(); } }, label)));
    previewBox.replaceChildren(tabs,
      !m.enabled ? h("p", { class: "muted small", style: "margin:12px 0" }, "This message is turned off.") :
        h("div", { class: "dc-frame" }, dcMessage({
          content: fill(m.content),
          embed: m.useEmbed ? { title: fill(m.title), description: fill(m.description), color: m.color || "#9d7bff",
            image: m.imageUrl, footer: fill(m.footer), avatar: m.showAvatar } : null,
          rows: btns.length ? [btns] : []
        })),
      h("p", { class: "muted small" }, "Preview with a sample member. ", previewTab === "channel" ? (b.channelId ? "" : "Choose a channel on the left.") : ""));
  }

  const save = h("button", { class: "btn" }, icon("save", 15), "Save");
  const tracker = track("Welcome", body, async () => await act(save, () => gApi("PUT", "/welcome", body()), "Welcome saved.") !== undefined);
  save.addEventListener("click", tracker.save);
  const test = h("button", { class: "btn secondary", title: "Sends the messages from this form to your DMs (even before saving)" }, icon("send", 15), "Send me a test");
  test.addEventListener("click", () => act(test, () => gApi("POST", "/welcome/test", body()), "Sent to your DMs."));

  holder.replaceChildren(h("div", { class: "editor-layout" },
    (() => {
      // Разделы вместо четырёх карточек подряд; превью справа остаётся
      const sec = sectionTabs([
        ["channel", "hash", "Channel message", sectionCard("hash", "Message in a channel", "Posted when someone joins the server.",
          h("div", { class: "field" }, h("label", {}, "Channel"), channel), chMsg.el)],
        ["dm", "mail-plus", "Direct message", sectionCard("mail-plus", "Direct message", "Sent privately to the new member (if their DMs are open).", dmMsg.el)],
        ["buttons", "link", "Buttons", sectionCard("link", "Buttons under the welcome", w.ticketPanelUrl ? null : "The Apply button appears once the ticket panel is posted.",
          apply.el, h("div", { class: "field" }, h("label", {}, "Apply button label"), applyLabel),
          h("label", { class: "field-label" }, "Link buttons (rules, website…)"), linksBox)],
        ["role", "user-plus", "Auto role", sectionCard("user-plus", "Auto role", "Off by default. For a role given after accepting the rules, make a Role panel in the “Give only” mode.",
          w.canManageRoles ? null : h("p", { class: "error-text small" }, "The bot has no Manage Roles permission."),
          h("div", { class: "field" }, h("label", {}, "Mode"), mode), roleBox)]
      ], "gb.welcomeSection");
      return h("div", { class: "editor-form" }, sec.nav, ...sec.panels, h("div", { class: "row sticky-actions" }, save, test));
    })(),
    h("div", { class: "editor-preview" }, h("div", { class: "card preview-card" }, h("h3", {}, "Preview"), previewBox))));
  tracker.attach(holder.firstElementChild);
  drawPreview();
}

// ================= Role panels =================

const ROLE_MODES = [
  ["Normal", "Get / remove", "Click to get the roles, click again to remove them."],
  ["Unique", "Only one", "Picking another item replaces the previous one (e.g. one game or one region)."],
  ["Verify", "Give only", "Roles are only given and stay (e.g. ✅ I accept the rules)."],
  ["Remove", "Remove only", "Clicking removes the roles (e.g. turn off notifications)."]
];
const TRIGGERS = [["Button", "🔘", "Button"], ["Reaction", "😀", "Reaction"], ["Option", "📋", "List item"]];
const BTN_COLORS = [["Grey", "Grey"], ["Blurple", "Blurple"], ["Green", "Green"], ["Red", "Red"]];

async function pageRoles(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎭 Role panels"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  main.append(holder);
  let data, emojis = [];
  try {
    data = await gApi("GET", "/roles/panels");
    try { emojis = await gApi("GET", "/roles/emojis"); } catch { /* unicode only */ }
  } catch (e) { holder.replaceChildren(errorState(e.message)); return; }

  // Вкладки по проектам (как у страниц каналов): All · General · <проект>…; новая панель — в открытом проекте
  const groups = data.groups || [];
  const isGeneral = p => !p.projectKey || !groups.some(g => g.key === p.projectKey);
  const tabs = [["all", "All", data.panels.length], ["", "General", data.panels.filter(isGeneral).length],
    ...groups.map(g => [g.key, g.name, data.panels.filter(p => p.projectKey === g.key).length])];
  let tab = store.get("gb.rolesTab") ?? "all";
  if (!tabs.some(t => t[0] === tab)) tab = "all";
  const shown = tab === "all" ? data.panels : tab === "" ? data.panels.filter(isGeneral) : data.panels.filter(p => p.projectKey === tab);
  const newPanel = () => rolePanelEditor(null, emojis, groups, tab === "all" ? "" : tab);
  const add = h("button", { class: "btn", onclick: newPanel }, icon("plus", 15), "New role panel");
  const tabBar = groups.length ? h("div", { class: "tabs page-tabs" }, tabs.map(([k, label, n]) =>
    h("button", { class: `btn secondary sm${k === tab ? " active-tab" : ""}`, onclick: () => { store.set("gb.rolesTab", k); renderShell(); } },
      label, h("span", { class: "tab-count" }, String(n))))) : null;
  holder.replaceChildren(
    h("div", { class: "card" },
      h("div", { class: "card-head" }, h("h2", {}, "How it works"), add),
      h("p", { class: "muted small" },
        "A role panel is a message where members take roles themselves. Each item is a 🔘 button, a 😀 reaction or a 📋 list item, and gives one or more roles. ",
        "Mix them freely on one panel."),
      data.canManageRoles ? h("p", { class: "muted small" }, `The bot's role (${data.botRole || "?"}) must be above every role it gives — Server Settings → Roles.`)
        : h("p", { class: "error-text small" }, "The bot has no Manage Roles permission — it can't give roles yet.")),
    tabBar,
    shown.length ? h("div", { class: "panel-grid" }, shown.map(p => rolePanelCard(p, emojis, groups)))
      : h("div", { class: "card" }, data.panels.length
        ? emptyState("user-plus", "No role panels in this project", "“New role panel” creates one here.")
        : emptyState("user-plus", "No role panels yet", "Create one — e.g. game roles with buttons or a “I accept the rules” panel.", add.cloneNode(true))));
  const emptyAdd = holder.querySelector(".empty-state .btn");
  if (emptyAdd) emptyAdd.addEventListener("click", newPanel);
}

function panelPreview(p, emojis) {
  const emap = emojiMapOf(emojis);
  const options = p.bindings.filter(b => b.trigger === "Option");
  const buttons = p.bindings.filter(b => b.trigger === "Button");
  const reactions = p.bindings.filter(b => b.trigger === "Reaction");
  const list = p.showList ? p.bindings.map(b => `${b.emoji || "•"} ${b.roles.map(r => `<@&${r.id}>`).join(" ")}${b.description ? ` — ${b.description}` : ""}`).join("\n") : "";
  const hint = p.showHint ? `-# ${(ROLE_MODES.find(m => m[0] === p.mode) || ROLE_MODES[0])[2]}` : "";
  const rows = [];
  if (options.length) rows.push([h("span", { class: "dc-select" }, h("span", {}, p.placeholder || "Choose your roles"), icon("chevron-down", 14))]);
  for (let i = 0; i < buttons.length; i += 5) rows.push(buttons.slice(i, i + 5).map(b => dcButton(b.shownLabel || b.label, b.emoji, b.color)));
  return dcMessage({
    embed: { title: resolveEmojiShortcodes(p.title, emap), description: [resolveEmojiShortcodes(p.text, emap), list, hint].filter(Boolean).join("\n\n"), color: p.color || "#5865f2", image: p.imageUrl, thumbnail: p.thumbnailUrl, footer: p.footer },
    rows, reactions: reactions.map(r => r.emoji)
  });
}

function rolePanelCard(p, emojis, groups = []) {
  const post = channelSelect("text", "Channel…");
  if (p.channelId) post.value = p.channelId;
  const hasReactions = p.bindings.some(b => b.trigger === "Reaction");
  const name = p.title || "role panel";
  // Ещё не опубликована — Post на виду; опубликована — главное Edit (сохранение правит сообщение на месте), Repost — в «⋯»
  const postBtn = p.jumpUrl ? null : h("button", { class: "btn" }, icon("pin", 15), "Post");
  if (postBtn) postBtn.addEventListener("click", async () => {
    if (!post.value) return toast("Choose a channel first.", "warn");
    if (await act(postBtn, () => gApi("POST", `/roles/panels/${p.id}/post`, { channelId: post.value }), "Panel posted.") !== undefined) refresh();
  });
  const repost = () => act(null, async () => {
    if (!post.value) return toast("Choose a channel first.", "warn");
    const text = "The old message is deleted and the panel is posted again in the chosen channel." +
      (hasReactions ? " ⚠️ All reactions under it disappear — the bot can't put them back." + REPOST_NOTE : "") +
      " To change the panel, just Edit and Save — the posted message is updated in place.";
    if (!await (hasReactions ? confirmTyped("Re-post this panel?", text, name, "Re-post") : confirmDialog("Re-post this panel?", text, "Re-post"))) return;
    await gApi("POST", `/roles/panels/${p.id}/post`, { channelId: post.value });
    toast("Panel re-posted."); refresh();
  });
  const sync = () => act(null, async () => {
    const r = await gApi("POST", `/roles/panels/${p.id}/sync`);
    if (r) toast(r.given ? `Roles given: ${r.given}.` : "Everyone with a reaction already has the role.");
  });
  const more = menuButton([
    { icon: "copy", label: "Copy", onclick: () => act(null, async () => { await gApi("POST", `/roles/panels/${p.id}/copy`); toast("Copied."); refresh(); }) },
    p.jumpUrl && hasReactions ? { icon: "refresh-cw", label: "Sync reactions (give missed roles)", onclick: sync } : null,
    p.jumpUrl ? { icon: "repeat", label: hasReactions ? "Re-post (reactions reset)" : "Re-post", danger: true, onclick: repost } : null,
    { icon: "trash-2", label: "Delete panel", danger: true, onclick: () => act(null, async () => {
      const text = "The posted message is deleted too. Members keep the roles they already have.";
      if (!await (p.jumpUrl && hasReactions ? confirmTyped(`Delete “${name}”?`, text + " Reactions are lost.", name, "Delete") : confirmDialog(`Delete “${name}”?`, text, "Delete"))) return;
      await gApi("DELETE", `/roles/panels/${p.id}`); toast("Panel deleted."); refresh();
    }) }
  ].filter(Boolean));
  const mode = ROLE_MODES.find(m => m[0] === p.mode) || ROLE_MODES[0];
  const problems = p.bindings.flatMap(b => b.roles).filter(r => !r.manageable);
  return h("div", { class: "card panel-card" },
    h("div", { class: "card-head" }, h("h3", {}, p.title || "(no title)"),
      h("span", { class: "chip" }, mode[1]),
      p.jumpUrl ? h("a", { class: "chip ok", href: p.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 12), `#${p.channel || "posted"}`)
        : h("span", { class: "chip warn" }, "not posted")),
    h("div", { class: "dc-frame compact" }, panelPreview(p, emojis)),
    h("details", { class: "binding-summary" }, h("summary", {}, `${p.bindings.length} item${p.bindings.length === 1 ? "" : "s"} → roles`),
      p.bindings.map(b => h("div", { class: "binding-line" },
        h("span", { class: "binding-trigger", title: b.trigger }, (TRIGGERS.find(t => t[0] === b.trigger) || TRIGGERS[0])[1].split(" ")[0]),
        b.emoji ? emojiEl(b.emoji) : null, h("b", {}, b.shownLabel), icon("arrow-right", 13),
        h("span", { class: "binding-roles" }, b.roles.map(r => roleChip(r)))))),
    problems.length ? h("p", { class: "error-text small" }, `The bot can't give: ${[...new Set(problems.map(r => "@" + r.name))].join(", ")}`) : null,
    (p.requiredRoles || []).some(r => !r.exists) ? h("p", { class: "error-text small" }, "“Only for” has a deleted role — it is ignored; remove it in the editor.") : null,
    p.jumpUrl ? (() => {
      const sticky = h("input", { type: "checkbox", class: "switch", checked: !!p.sticky, "aria-label": "Keep at the bottom" });
      sticky.addEventListener("change", () => act(null, () => gApi("PUT", `/roles/panels/${p.id}/sticky`, { sticky: sticky.checked }),
        sticky.checked ? "📌 Sticky: the panel stays at the bottom of the channel." : "Sticky off."));
      return h("label", { class: "row", style: "gap:6px;margin-top:8px", title: "After new messages in the channel the bot posts the panel again at the bottom (15 s of silence, at most a minute)" },
        sticky, "📌 Keep at the bottom of the channel");
    })() : null,
    h("div", { class: "row", style: "margin-top:10px;gap:8px" }, post, postBtn, h("span", { class: "spacer" }),
      h("button", { class: p.jumpUrl ? "btn" : "btn secondary", onclick: () => rolePanelEditor(p, emojis, groups) }, icon("pencil", 15), "Edit"), more));
}

function rolePanelEditor(p, emojis, groups = [], defaultProject = "") {
  const d = dialog();
  d.classList.add("xwide");
  d.onclose = () => d.classList.remove("xwide");
  const v = p || { title: "Pick your roles", text: "", mode: "Normal", showList: true, showHint: true, maxPick: 0, requiredRoles: [],
    bindings: [{ trigger: "Button", label: "", emoji: "", description: "", color: "Grey", roles: [] }] };

  const preview = h("div", { class: "dc-frame" });
  const projSel = h("select", {}, h("option", { value: "" }, "General (no project)"), groups.map(g => h("option", { value: g.key }, g.name)));
  projSel.value = p ? (p.projectKey || "") : defaultProject;
  const state_ = () => ({
    projectKey: projSel.value,
    title: title.value, text: text.value, color: color.value(), imageUrl: image.value, thumbnailUrl: thumb.value, footer: footer.value,
    showList: showList.cb.checked, showHint: showHint.cb.checked, mode: modeValue, placeholder: placeholder.value, maxPick: Number(maxPick.value || 0),
    requiredRoleIds: required.value(),
    bindings: rows.map(r => ({ id: r.id, trigger: r.trigger, label: r.label, emoji: r.emoji, description: r.description, color: r.color, roleIds: r.roles.value() }))
  });
  const update = () => {
    const s = state_();
    preview.replaceChildren(panelPreview({ ...s,
      bindings: rows.map(r => ({ trigger: r.trigger, emoji: r.emoji, description: r.description, color: r.color, label: r.label,
        shownLabel: r.label || (r.roles.roles()[0] || { name: "Role" }).name, roles: r.roles.roles() })) }, emojis));
    listOnly.forEach(el => { el.style.display = rows.some(r => r.trigger === "Option") ? "" : "none"; });
  };

  // ----- Card -----
  const title = h("input", { maxlength: "256", placeholder: "Title", value: v.title || "" });
  const text = h("textarea", { maxlength: "3000", style: "min-height:100px", placeholder: "Text above the roles — Markdown: **bold**, *italic*, # heading, -# small", value: v.text || "" });
  const color = embedColorField(v.color, () => update());
  const image = h("input", { placeholder: "https://… or 🖼 media library (optional)", value: v.imageUrl || "" });
  const thumb = h("input", { placeholder: "small, in the corner (optional)", value: v.thumbnailUrl || "" });
  const footer = h("input", { maxlength: "2048", placeholder: "Small text at the bottom (optional)", value: v.footer || "" });
  const showList = toggle(v.showList, "Add the list “emoji — roles — description” to the text", () => update());
  const showHint = toggle(v.showHint, "Small hint about how the panel works", () => update());
  for (const f of [title, text, image, thumb, footer]) f.addEventListener("input", () => update());

  // ----- Behaviour -----
  let modeValue = v.mode || "Normal";
  const modeBox = h("div", { class: "mode-grid" });
  const drawModes = () => modeBox.replaceChildren(...ROLE_MODES.map(([k, label, hint]) => h("button", {
    type: "button", class: `mode-card${modeValue === k ? " active" : ""}`, onclick: () => { modeValue = k; drawModes(); update(); }
  }, h("b", {}, label), h("span", { class: "muted small" }, hint))));
  drawModes();
  const required = roleListEditor(v.requiredRoles || [], state.roles);
  const placeholder = h("input", { maxlength: "150", placeholder: "Choose your roles", value: v.placeholder || "" });
  const maxPick = h("input", { type: "number", min: "0", max: "25", style: "width:90px", value: v.maxPick || 0 });
  placeholder.addEventListener("input", () => update());
  const listOnly = [
    h("div", { class: "field" }, h("label", {}, "List hint"), placeholder),
    h("div", { class: "field" }, h("label", {}, "Max items picked at once (0 = any)"), maxPick)
  ];

  // ----- Items: button / reaction / list item → roles -----
  const rows = [];
  const itemsBox = h("div", {});
  const makeRow = b => {
    const r = { id: b.id || null, trigger: b.trigger || "Button", label: b.label || "", emoji: b.emoji || "", description: b.description || "", color: b.color || "Grey" };
    r.roles = roleListEditor(b.roles || [], state.roles, () => update());
    return r;
  };
  for (const b of v.bindings) rows.push(makeRow(b));
  const drawRows = () => {
    itemsBox.replaceChildren(...rows.map((r, i) => {
      const seg = h("div", { class: "seg" }, TRIGGERS.map(([k, ic, label]) => h("button", {
        type: "button", class: `seg-btn${r.trigger === k ? " active" : ""}`, onclick: () => { r.trigger = k; drawRows(); update(); }
      }, `${ic} ${label}`)));
      const em = emojiField(r.emoji, emojis, () => { r.emoji = em.value() || ""; update(); });
      const label = h("input", { maxlength: "80", placeholder: r.trigger === "Reaction" ? "Name in the list (optional)" : "Label (empty = role name)", value: r.label });
      label.addEventListener("input", () => { r.label = label.value; update(); });
      const descr = h("input", { maxlength: "100", placeholder: r.trigger === "Option" ? "Description under the item (optional)" : "Description in the list (optional)", value: r.description });
      descr.addEventListener("input", () => { r.description = descr.value; update(); });
      const colorSel = r.trigger === "Button" ? h("div", { class: "seg small" }, BTN_COLORS.map(([k, l]) => h("button", {
        type: "button", class: `seg-btn swatch-${k.toLowerCase()}${r.color === k ? " active" : ""}`, title: l, onclick: () => { r.color = k; drawRows(); update(); }
      }, l))) : null;
      const move = (dir) => { const j = i + dir; if (j < 0 || j >= rows.length) return; [rows[i], rows[j]] = [rows[j], rows[i]]; drawRows(); update(); };
      return h("div", { class: "binding-card" },
        h("div", { class: "row binding-head" }, h("span", { class: "binding-num" }, `#${i + 1}`), seg, h("span", { class: "spacer" }),
          h("button", { class: "btn ghost sm", type: "button", title: "Up", onclick: () => move(-1) }, icon("chevron-up", 14)),
          h("button", { class: "btn ghost sm", type: "button", title: "Down", onclick: () => move(1) }, icon("chevron-down", 14)),
          h("button", { class: "btn ghost sm", type: "button", title: "Remove", onclick: () => { rows.splice(i, 1); drawRows(); update(); } }, icon("trash-2", 14))),
        h("div", { class: "binding-grid" },
          h("div", { class: "field" }, h("label", {}, r.trigger === "Reaction" ? "Emoji (required)" : "Emoji"), em.el),
          h("div", { class: "field" }, h("label", {}, r.trigger === "Button" ? "Button label" : r.trigger === "Option" ? "Item label" : "Name"), label),
          colorSel ? h("div", { class: "field" }, h("label", {}, "Button color"), colorSel) : null,
          h("div", { class: "field wide" }, h("label", {}, "Description"), descr)),
        h("div", { class: "field" }, h("label", {}, "Gives the roles"), r.roles.el));
    }));
  };
  drawRows();
  const addItem = trigger => { rows.push(makeRow({ trigger })); drawRows(); update(); itemsBox.lastElementChild?.scrollIntoView({ block: "nearest" }); };
  const addBar = h("div", { class: "row", style: "gap:8px;margin-top:8px" }, TRIGGERS.map(([k, ic, label]) =>
    h("button", { class: "btn secondary sm", type: "button", onclick: () => addItem(k) }, icon("plus", 14), `${ic} ${label}`)));

  const channel = p ? null : channelSelect("text", "Don't post yet");
  const save = h("button", { class: "btn" }, icon("save", 15), p ? "Save" : "Create");
  save.addEventListener("click", async () => {
    const body = { ...state_(), channelId: channel ? channel.value || null : null };
    const res = await act(save, () => p ? gApi("PUT", `/roles/panels/${p.id}`, body) : gApi("POST", "/roles/panels", body),
      p ? (p.jumpUrl ? "Saved — the posted panel is updated." : "Saved.") : (body.channelId ? "Panel created and posted." : "Panel created — post it from the list."));
    if (res !== undefined) { d.close(); refresh(); }
  });

  d.replaceChildren(
    h("div", { class: "card-head" }, h("h2", {}, p ? "Edit role panel" : "New role panel"), h("button", { class: "btn ghost", onclick: () => d.close() }, "✕")),
    h("div", { class: "editor-layout in-dialog" },
      h("div", { class: "editor-form" },
        sectionCard("file-text", "1. Message", "The card members see.",
          h("div", { class: "field" }, h("label", {}, "Project (groups the panels on this page)"), projSel),
          h("div", { class: "field" }, h("label", {}, "Title"), title),
          h("div", { class: "field" }, h("label", {}, "Text"), text, emojiInsertPicker(text, emojis, () => update())),
          h("div", { class: "grid2" }, h("div", { class: "field" }, h("label", {}, "Stripe color"), color.el),
            h("div", { class: "field" }, h("label", {}, "Footer"), footer)),
          h("div", { class: "grid2" }, h("div", { class: "field" }, h("label", {}, "Big image"), withMediaButton(image)),
            h("div", { class: "field" }, h("label", {}, "Thumbnail"), withMediaButton(thumb))),
          showList.el, showHint.el),
        sectionCard("list-checks", "2. Buttons, reactions & list items", "Each item gives one or more roles. Mix types freely: up to 25 buttons (20 with a list), 25 list items, 20 reactions.",
          itemsBox, addBar),
        sectionCard("sliders-horizontal", "3. How it works", null, modeBox,
          h("div", { class: "grid2", style: "margin-top:10px" }, ...listOnly),
          h("div", { class: "field" }, h("label", {}, "Only for members with (optional)"), required.el,
            h("span", { class: "muted small" }, "Others get “This panel is only for …”."))),
        channel ? sectionCard("hash", "4. Post", null, h("div", { class: "field" }, h("label", {}, "Post in channel"), channel)) : null),
      h("div", { class: "editor-preview" }, h("div", { class: "card preview-card" }, h("h3", {}, "Preview"), preview,
        h("p", { class: "muted small" }, "Roughly how it looks in Discord.")))),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  update();
  d.showModal();
}

// ================= Panel texts =================

async function pageTexts(main, keys = null) {
  if (!keys) {
    main.append(h("div", { class: "card-head" }, h("h1", {}, "📝 Panel texts"),
      h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
    main.append(h("p", { class: "muted" },
      "Edit the text of the panels the bot posts and the button labels of group posts and tickets. Saving updates what is already posted. ",
      "Panels posted before this feature are unknown to the bot — click “Find posted panels” once to pick them up."));
  }

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
    const panels = (await gApi("GET", "/texts")).filter(p => !keys || keys.includes(p.key));
    holder.replaceChildren(...panels.map(textCard));
  } catch (e) { holder.replaceChildren(errorState(e.message)); }
}

function textCard(p) {
  // Empty title = the panel has no title (headings go into the text)
  const title = h("input", { maxlength: String(p.maxTitle), placeholder: "No title", value: p.title === "-" ? "" : (p.title || p.defaults.title) });
  const desc = h("textarea", { maxlength: String(p.maxDescription), style: "min-height:260px" });
  desc.value = p.description || p.defaults.description;
  const counter = h("span", { class: "muted small" }, `${desc.value.length}/${p.maxDescription}`);
  desc.addEventListener("input", () => { counter.textContent = `${desc.value.length}/${p.maxDescription}`; });

  const color = colorField(p.color, p.defaultColor, () => preview.refresh());

  const labelInputs = p.labels.map(l => {
    const input = h("input", { maxlength: String(l.max), placeholder: l.default, value: l.value || l.default });
    return { l, input, el: h("div", { class: "field" }, h("label", {}, l.name), input) };
  });

  const labelsOnly = p.kind === "labels";
  const draft = () => {
    const labels = {};
    for (const { l, input } of labelInputs) labels[l.id] = input.value;
    return { title: title.value, description: desc.value, labels, color: color.value() };
  };
  const preview = livePreview(() => gApi("POST", `/texts/${p.key}/preview`, draft()));
  for (const f of [title, desc, ...labelInputs.map(x => x.input)]) f.addEventListener("input", () => preview.refresh());
  const what = p.key === "lfgpost" ? "active group post(s)" : p.key === "ticketmsg" ? "open ticket(s)" : "posted panel(s)";
  const resultToast = (res, done) => {
    if (!res) return;
    toast(res.panelsTotal
      ? `${done} Updated ${res.panelsUpdated} of ${res.panelsTotal} ${what}.`
      : labelsOnly ? `${done} New posts will use it.` : `${done} No posted panels to update yet — post the panel from its module tab.`,
      res.panelsUpdated === res.panelsTotal ? "ok" : "warn");
  };

  const save = h("button", { class: "btn" }, labelsOnly ? "💾 Save & update posts" : "💾 Save & update panels");
  const tracker = track(p.name, draft, async () => {
    const res = await act(save, () => gApi("PUT", `/texts/${p.key}`, draft()));
    resultToast(res, "Saved.");
    return res !== undefined;
  });
  save.addEventListener("click", tracker.save);
  const reset = h("button", { class: "btn secondary" }, "↩️ Reset to default");
  reset.addEventListener("click", async () => {
    if (!await confirmDialog(`Reset ${p.name}?`, `The default text is restored and ${what} are updated.`, "Reset", false)) return;
    const res = await act(reset, () => gApi("DELETE", `/texts/${p.key}`));
    if (res === undefined) return;
    resultToast(res, "Reset.");
    // Defaults back in the form; the rest of the page keeps its unsaved edits
    title.value = p.defaults.title === "-" ? "" : p.defaults.title || "";
    desc.value = p.defaults.description || "";
    for (const { l, input } of labelInputs) input.value = l.default;
    color.set("", null);
    tracker.rebase();
    preview.refresh();
    refresh();
  });

  const custom = p.title || p.description || p.color || p.labels.some(l => l.value);
  preview.now();
  return tracker.attach(h("div", { class: "editor-layout", style: "margin-bottom:16px" }, h("div", { class: "card", style: "margin:0" },
    h("div", { class: "card-head" }, h("h2", {}, p.name),
      custom ? h("span", { class: "badge wait" }, "custom") : h("span", { class: "badge ok" }, "default"),
      labelsOnly ? null : h("span", { class: "muted small" }, `${p.posted} posted`)),
    labelsOnly ? h("p", { class: "muted small" }, p.key === "lfgpost"
      ? "Buttons under every LFG group post. Saving updates the active groups." : "Buttons in every ticket message. Saving updates the open tickets.") : null,
    labelsOnly ? null : h("div", { class: "field" }, h("label", {}, "Title"), title,
      h("span", { class: "muted small" }, "Plain text — Discord doesn't apply markdown in titles. Leave it empty for no title and put headings (# , ## , -# ) at the start of the text. Reset to default brings the standard title back.")),
    labelsOnly ? null : h("div", { class: "field" }, h("label", {}, "Text (Discord markdown)"), desc,
      h("div", { class: "row" }, counter, p.placeholders ? h("span", { class: "muted small" }, `· ${p.placeholders}`) : null)),
    labelsOnly ? null : h("div", { class: "field" }, h("label", {}, "Stripe color"), color.el,
      h("span", { class: "muted small" }, "The colored line on the left of the panel in Discord.")),
    labelInputs.length ? h("div", { class: "grid2" }, labelInputs.map(x => x.el)) : null,
    h("div", { class: "row" }, save, reset)),
    h("div", { class: "editor-preview" }, preview.el)));
}

/** Color picker: swatch + #hex field + reset. value() → "#rrggbb" or "" (= default). */
function colorField(value, def, onChange) {
  const norm = v => /^#?[0-9a-f]{6}$/i.test((v || "").trim()) ? "#" + v.trim().replace("#", "").toLowerCase() : null;
  let current = norm(value) || "";
  const swatch = h("input", { type: "color", value: current || def, title: "Pick a color" });
  const hex = h("input", { class: "color-hex", maxlength: "7", placeholder: def, value: current, spellcheck: "false" });
  const preview = h("span", { class: "embed-preview" }, h("span", { class: "embed-stripe" }), h("span", { class: "embed-lines" }, h("i"), h("i"), h("i")));
  const paint = () => preview.style.setProperty("--stripe", current || def);
  const set = (v, from) => {
    current = v ? norm(v) || current : "";
    if (from !== swatch) swatch.value = current || def;
    if (from !== hex) hex.value = current;
    hex.classList.toggle("bad", !!hex.value && !norm(hex.value));
    paint();
    onChange?.(current);
  };
  swatch.addEventListener("input", () => set(swatch.value, swatch));
  hex.addEventListener("input", () => { if (!hex.value || norm(hex.value)) set(hex.value, hex); else hex.classList.add("bad"); });
  const reset = h("button", { type: "button", class: "btn ghost icon-only sm", title: `Default (${def})`, onclick: () => set("", null) }, icon("undo-2", 15));
  paint();
  return { el: h("div", { class: "color-field" }, swatch, hex, preview, reset), value: () => current === def ? "" : current, set };
}

// ================= Bot messages: every reply, DM, window and color =================

const BOT_TEXT_ICONS = [["LFG — group post", "swords"], ["LFG — creating", "circle-plus"], ["LFG — signing", "user-plus"],
  ["LFG — class", "drama"], ["LFG — organizer", "crown"], ["LFG — DMs", "mail-plus"], ["Tickets — ticket", "ticket"],
  ["Tickets — creating", "clipboard-list"], ["Tickets — approve", "shield-check"], ["Temp voice", "headphones"],
  ["Characters", "users"]];

async function pageBotTexts(main, groupFilter = null) {
  if (!groupFilter) {
    main.append(h("div", { class: "card-head" }, h("h1", {}, "💬 Bot messages"),
      h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
    main.append(h("p", { class: "muted" },
      "Every text the bot writes to members — replies, DMs, group posts, ticket messages, menus, buttons and windows — and the stripe colors of its messages. ",
      "Words in {braces} are filled in by the bot. Empty field = default text; in fields marked “- = hide” a single - removes the line."));
  }

  const holder = h("div", {}, skeletonLines(4));
  main.append(holder);
  let items;
  try { items = await gApi("GET", "/texts/bot"); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }
  if (groupFilter) items = items.filter(i => groupFilter(i.group));

  const dirty = new Map(); // key → new value ("" = back to default)
  const rows = [];
  const search = h("input", { type: "search", placeholder: "Search texts… (e.g. reserve, DM, Approve)", style: "flex:1;min-width:200px" });
  const onlyCustom = h("input", { type: "checkbox" });
  const resetAll = h("button", { class: "btn ghost danger-text" }, icon("undo-2", 15), " Reset all to default");

  // Unsaved texts go to the page's sticky bar (Save / Discard) — one save for all of them
  const tracker = track("Bot messages", () => [...dirty].sort((a, b) => a[0].localeCompare(b[0])), async () => {
    const res = await act(null, () => gApi("PUT", "/texts/bot", { values: Object.fromEntries(dirty) }));
    if (res === undefined) return false;
    toast(res.postsTotal ? `Saved ${dirty.size} text(s). Updated ${res.postsUpdated} of ${res.postsTotal} posted message(s).` : `Saved ${dirty.size} text(s). New messages use it.`,
      res.postsUpdated === res.postsTotal ? "ok" : "warn");
    dirty.clear();
    refresh();
    return true;
  });
  const updateBar = () => tracker.check && schedulePendingCheck();

  // Groups → cards (collapsed; search opens the matching ones)
  const groups = [];
  for (const it of items) {
    let g = groups.find(x => x.name === it.group);
    if (!g) groups.push(g = { name: it.group, items: [] });
    g.items.push(it);
  }

  const cards = groups.map(g => {
    const ic = (BOT_TEXT_ICONS.find(([p]) => g.name.startsWith(p)) || [, "message-square-text"])[1];
    const body = h("div", { class: "bt-list" });
    const badge = h("span", { class: "badge wait" });
    // Внутри модуля название группы без префикса модуля («LFG — signing up» → «Signing up»)
    const shown = groupFilter && groupFilter !== ALL_TEXT_GROUPS && /\s—\s/.test(g.name) ? g.name.replace(/^[^—]+—\s*/, "").replace(/^./, c => c.toUpperCase()) : g.name;
    const head = h("button", { type: "button", class: "bt-head" }, toneChip(ic, 16), h("b", {}, shown),
      h("span", { class: "muted small" }, `${g.items.length} texts`), badge, h("span", { class: "bt-chevron" }, icon("chevron-down", 18)));
    const card = h("div", { class: "card bt-card" }, head, body);
    // Group post / ticket message texts: the bot builds a sample message with the unsaved texts
    const sample = (g.items.find(i => i.refresh === "lfgpost" || i.refresh === "ticketmsg") || {}).refresh;
    const preview = sample ? livePreview(() => gApi("POST", "/texts/bot/preview", { sample, values: Object.fromEntries(dirty) }),
      sample === "lfgpost" ? "Preview — group post" : "Preview — ticket message") : null;
    // Other groups: the text you are editing, drawn as Discord shows it (button, menu, window, card or message)
    const local = sample ? null : botTextPreview(g.items, it => {
      const v = dirty.has(it.key) ? dirty.get(it.key) : it.value || "";
      return v || it.default;
    }, () => gApi("POST", "/texts/bot/screens", { sample: g.name, values: Object.fromEntries(dirty) }));
    let previewLoaded = false;
    const groupRows = g.items.map(it => {
      const row = botTextRow(it, v => {
        const original = it.value || "";
        if ((v || "") === original) dirty.delete(it.key); else dirty.set(it.key, v || "");
        row.el.classList.toggle("changed", dirty.has(it.key));
        updateBar();
        if (preview) preview.refresh();
        if (local) local.changed(it);
      });
      if (local) {
        row.el.addEventListener("focusin", () => local.show(it));
      }
      rows.push({ it, row, card });
      return row;
    });
    if (preview) {
      preview.el.classList.add("bt-preview");
      body.append(h("div", { class: "editor-layout" }, h("div", {}, ...groupRows.map(r => r.el)), h("div", { class: "editor-preview" }, preview.el)));
    } else {
      local.show(g.items[0]);
      body.append(h("div", { class: "editor-layout" }, h("div", {}, ...groupRows.map(r => r.el)), h("div", { class: "editor-preview" }, local.el)));
    }
    const countCustom = () => {
      const n = g.items.filter(x => x.value).length;
      badge.textContent = `${n} changed`;
      badge.style.display = n ? "" : "none";
    };
    countCustom();
    head.addEventListener("click", () => {
      card.classList.toggle("open");
      if (!previewLoaded && card.classList.contains("open")) {
        previewLoaded = true;
        if (preview) preview.now();
        if (local) local.load();
      }
    });
    return { card, g, countCustom };
  });

  const filter = () => {
    const q = search.value.trim().toLowerCase();
    for (const c of cards) {
      let visible = 0;
      for (const r of rows.filter(r => r.card === c.card)) {
        const hay = `${r.it.name} ${r.it.default} ${r.it.value || ""} ${r.it.key}`.toLowerCase();
        const show = (!q || hay.includes(q)) && (!onlyCustom.checked || r.it.value || dirty.has(r.it.key));
        r.row.el.style.display = show ? "" : "none";
        if (show) visible++;
      }
      c.card.style.display = visible ? "" : "none";
      if (q || onlyCustom.checked) c.card.classList.toggle("open", visible > 0);
    }
  };
  search.addEventListener("input", filter);
  onlyCustom.addEventListener("change", filter);

  resetAll.addEventListener("click", async () => {
    const n = items.filter(x => x.value).length;
    if (!n) return toast("Everything is already the default.");
    if (!await confirmDialog("Reset all bot messages?", `${n} changed text(s) and color(s) go back to the defaults. Active group posts and open tickets are updated.`, "Reset all")) return;
    const res = await act(resetAll, () => gApi("POST", "/texts/bot/reset", { keys: [] }), "All bot messages are back to default.");
    if (res !== undefined) renderShell();
  });

  holder.replaceChildren(
    h("div", { class: "card bt-tools" }, h("div", { class: "row" }, icon("search", 18), search,
      h("label", { class: "toggle-line" }, onlyCustom, h("span", {}, "Only changed")), resetAll)),
    ...cards.map(c => c.card));
  tracker.attach(holder);
}

/** Sample values for {placeholders} in previews of bot texts. */
function txSamples() {
  const t = Math.floor(Date.now() / 1000 / 60) * 60 + 7200;
  const guild = (state.guilds.find(x => x.id === state.guildId) || {}).name || "Greatness";
  return {
    user: "<@1>", applicant: "<@1>", organizer: "<@1>", member: "<@1>", owner: "<@1>", mention: "<@1>", by: "<@1>", who: "<@1>",
    role: "<@&2>", roles: "Tank, DPS, Healer", channel: "<#3>", voice: "<#3>", category: "Tickets",
    activity: "Ludra Hard", dungeon: "Ludra Hard", time: `<t:${t}:F> (<t:${t}:R>)`, when: ` — <t:${t}:F>`, date: `<t:${t}:D>`, until: `<t:${t}:R>`,
    filled: "4", total: "6", count: "2", n: "6", size: "6", slots: "2", left: "2", free: "2", minutes: "15", hours: "5", days: "7", seconds: "15", number: "1",
    list: "<@&2> · GS 3000+", note: "Bring buff food", comment: "Bring buff food", id: "a1b2c3", status: "📝 Recruiting", server: guild,
    project: "Aion 2", name: "Apostall", character: "Apostall", characters: "Apostall, Lemoshka", gs: "3100", min: "3000",
    class: "Templar", reason: "Not enough GS", link: "https://discord.com", url: "https://discord.com", text: "Hello!", limit: "5",
    old: "Ludra Hard", new: "Ludra Hell", answer: "Answer…", question: "Character name", tickets: "#tickets", members: "531"
  };
}

/** What a bot text is in Discord, by its name and length limit (see BotTextCatalog). */
function txKind(it) {
  const n = it.name.toLowerCase();
  if (it.color) return "color";
  if (n.includes("window:")) return "modal";
  if (n.includes("button") || it.max === 80) return "button";
  if (n.includes("placeholder") || it.max === 150) return "select";
  if (n.includes("option") || n.includes("description")) return "option";
  if (/:\s*title\b/.test(n) || it.max === 256) return "title";
  if (n.includes("footer") || it.max === 2048) return "footer";
  return "text";
}

/** "New group DM: title (- = hide)" → "new group dm" (texts of one message share it). */
const txPrefix = it => {
  const n = it.name.replace(/\s*\([^)]*\)/g, "").trim();
  const i = n.indexOf(":");
  return i > 0 ? n.slice(0, i).trim().toLowerCase() : null;
};

/**
 * Preview box for one group of bot texts. loadScreens() → [{ title, keys, preview }]: real screens the bot builds
 * (wizard steps, menus, windows) with the unsaved texts — shown as tabs; editing a text opens the screen that uses it.
 * Texts in no screen (replies, errors) are drawn on their own, in their Discord form.
 */
function botTextPreview(items, valueOf, loadScreens) {
  const frame = h("div", { class: "dc-frame" });
  const note = h("p", { class: "muted small" });
  const tabs = h("div", { class: "seg screen-tabs" });
  let current = items[0];
  let screens = [], tab = -1, timer = 0, seq = 0;
  const S = txSamples();
  const mentions = { "<@1>": `@${state.me?.name || "member"}`, "<@&2>": "@Aion 2", "<#3>": "#lfg" };
  const fill = v => String(v ?? "").replace(/\{(\w+)\}/g, (m, k) => k in S ? S[k] : m);
  const val = it => { const v = valueOf(it); return v.trim() === "-" ? "" : fill(v); };
  const mark = (it, el) => { if (it === current) el.classList.add("tx-focus"); return el; };

  const draw = () => {
    const it = current;
    const kind = txKind(it);
    const prefix = txPrefix(it);
    const siblings = prefix && prefix !== "stripe color" ? items.filter(x => txPrefix(x) === prefix) : [it];
    let out;
    if (kind === "modal") {
      // Discord window: title, then fields (label + input with placeholder)
      const parts = siblings.filter(x => txKind(x) === "modal");
      const box = h("div", { class: "dc-modal" });
      let lastInput = null;
      for (const x of parts) {
        const n = x.name.toLowerCase();
        if (n.includes("title")) box.prepend(mark(x, h("div", { class: "dc-modal-title" }, val(x) || "Window")));
        else if (n.includes("placeholder") || n.includes("hint")) {
          if (!lastInput) box.append(lastInput = h("div", { class: "dc-input" }));
          lastInput.replaceChildren(val(x));
          mark(x, lastInput);
          lastInput = null;
        } else {
          box.append(mark(x, h("div", { class: "dc-modal-label" }, val(x), h("span", { class: "dc-req" }, " *"))), lastInput = h("div", { class: "dc-input" }));
        }
      }
      box.append(h("div", { class: "dc-modal-actions" }, h("span", { class: "dc-btn dc-btn-link-plain" }, "Cancel"), h("span", { class: "dc-btn dc-btn-blurple" }, "Submit")));
      out = box;
    } else if (kind === "button") {
      const btns = items.filter(x => txKind(x) === "button").slice(0, 10);
      const row = [];
      for (let i = 0; i < btns.length; i += 5) row.push(btns.slice(i, i + 5).map(x => mark(x, dcButton(val(x), null, x === it ? "Blurple" : "Grey"))));
      out = dcMessage({ content: "-# Buttons of this group (the edited one is blue)", rows: row, mentions });
    } else if (kind === "select" || kind === "option") {
      const ph = kind === "select" ? it : items.find(x => txKind(x) === "select");
      const opts = items.filter(x => txKind(x) === "option").slice(0, 6);
      out = dcMessage({ mentions, rows: [h("div", { class: "dc-dropdown" },
        mark(ph || it, h("span", { class: "dc-select" }, h("span", {}, ph ? val(ph) || "Make a selection" : "Make a selection"), icon("chevron-down", 14))),
        opts.length ? h("div", { class: "dc-options" }, (() => {
          // A "… description" goes under the option before it
          const els = [];
          for (const x of opts) {
            const desc = /description/i.test(x.name);
            if (desc && els.length) { els[els.length - 1].append(mark(x, h("span", {}, val(x)))); continue; }
            els.push(mark(x, h("div", { class: "dc-option" }, desc ? h("b", {}, "Option") : h("b", {}, val(x)), desc ? h("span", {}, val(x)) : null)));
          }
          return els;
        })()) : null)] });
    } else {
      // A card (title / lines / footer / stripe) or a plain message
      const colorName = x => x.name.toLowerCase().replace(/^stripe color:\s*/, "");
      const own = kind === "color" ? items.filter(x => !x.color && txPrefix(x) === colorName(it)) : siblings;
      const parts = own.filter(x => ["title", "text", "footer"].includes(txKind(x)));
      const colorIt = kind === "color" ? it : items.find(x => x.color && colorName(x) === prefix);
      const asCard = kind === "color" || kind === "footer" || parts.some(x => txKind(x) === "title" || txKind(x) === "footer") || !!colorIt;
      if (!asCard) out = dcMessage({ content: val(it) || "(hidden)", mentions });
      else {
        const title = parts.find(x => txKind(x) === "title");
        const footer = parts.find(x => txKind(x) === "footer");
        const lines = parts.filter(x => txKind(x) === "text" && val(x));
        const desc = h("div", {}, lines.map(x => mark(x, mdPreview(val(x), mentions))));
        const card = dcEmbed({ title: title ? val(title) : kind === "color" ? "Sample card" : null, footer: footer ? val(footer) : null,
          color: colorIt ? valueOf(colorIt) : "#5865f2", description: lines.length ? null : "Text of the message…" }, mentions);
        const main = card.querySelector(".dc-embed-main");
        if (lines.length) main.insertBefore(desc, main.querySelector(".dc-embed-footer"));
        if (title && main.firstElementChild) mark(title, main.firstElementChild);
        if (footer) { const f = main.querySelector(".dc-embed-footer"); if (f) mark(footer, f); }
        if (kind === "color") mark(it, card);
        out = dcMessage({ embeds: [], mentions });
        out.querySelector(".dc-body").append(card);
      }
    }
    frame.replaceChildren(out);
    note.textContent = `${it.name} · {placeholders} filled with sample values`;
  };

  const drawTabs = () => {
    tabs.style.display = screens.length ? "" : "none";
    tabs.replaceChildren(...screens.map((sc, i) => h("button", { type: "button", class: `seg-btn${tab === i ? " active" : ""}`,
      title: "How the bot shows this screen", onclick: () => { tab = i; render(); } }, sc.title)),
      screens.length ? h("button", { type: "button", class: `seg-btn${tab === -1 ? " active" : ""}`, title: "Only the text you are editing",
        onclick: () => { tab = -1; render(); } }, "This text") : null);
  };
  const render = () => {
    drawTabs();
    const sc = screens[tab];
    if (!sc) { draw(); return; }
    const kind = txKind(current);
    frame.replaceChildren(sc.preview.modal ? dcModal(sc.preview.modal) : dcFromPreview(sc.preview, { expand: kind === "option" || kind === "select" }));
    note.textContent = sc.keys.includes(current.key)
      ? `${sc.title} · uses “${current.name}” · built by the bot with your unsaved texts`
      : `${sc.title} · built by the bot with your unsaved texts`;
  };
  const pick = it => {
    const i = screens.findIndex(sc => sc.keys.includes(it.key));
    tab = i;
  };
  const fetchScreens = async () => {
    if (!loadScreens) return;
    const my = ++seq;
    try {
      const res = await loadScreens();
      if (my !== seq) return;
      const keep = screens[tab]?.title;
      screens = res.screens || [];
      const again = screens.findIndex(sc => sc.title === keep);
      tab = again >= 0 && screens[again].keys.includes(current.key) ? again : (pick(current), tab);
      render();
    } catch { /* keep the text preview */ }
  };
  return {
    el: h("div", { class: "card preview-card" }, h("h3", {}, "Preview"), tabs, frame, note),
    show(it) { current = it; pick(it); render(); },
    changed(it) { current = it; render(); clearTimeout(timer); timer = setTimeout(fetchScreens, 350); },
    load: fetchScreens
  };
}

/** A Discord window (modal) mock: title, fields, Cancel / Submit. */
function dcModal(m) {
  return h("div", { class: "dc-modal" }, h("div", { class: "dc-modal-title" }, m.title || "Window"),
    (m.fields || []).flatMap(f => [
      h("div", { class: "dc-modal-label" }, f.label || "", f.required ? h("span", { class: "dc-req" }, " *") : null),
      h("div", { class: `dc-input${f.long ? " long" : ""}` }, f.value ? h("span", { style: "color:#dbdee1" }, f.value) : f.placeholder || "")]),
    h("div", { class: "dc-modal-actions" }, h("span", { class: "dc-btn dc-btn-link-plain" }, "Cancel"), h("span", { class: "dc-btn dc-btn-blurple" }, "Submit")));
}

/** One editable bot text (textarea) or color (swatch). onChange(value) — "" = default. */
function botTextRow(it, onChange) {
  const custom = h("span", { class: "badge wait" }, "custom");
  custom.style.display = it.value ? "" : "none";
  let control, value;
  if (it.color) {
    const c = colorField(it.value, it.default, v => onChange(v === it.default ? "" : v));
    control = c.el;
  } else {
    const area = h("textarea", { class: "bt-text", maxlength: String(it.max), rows: "1", spellcheck: "false", placeholder: it.default });
    area.value = it.value || it.default;
    const fit = () => { area.style.height = "auto"; area.style.height = Math.min(area.scrollHeight + 2, 320) + "px"; };
    const counter = h("span", { class: "muted small" });
    const upd = () => { counter.textContent = `${area.value.length}/${it.max}`; };
    area.addEventListener("input", () => { fit(); upd(); onChange(area.value === it.default ? "" : area.value); });
    requestAnimationFrame(fit); upd();
    const reset = h("button", { type: "button", class: "btn ghost icon-only sm", title: "Back to default",
      onclick: () => { area.value = it.default; fit(); upd(); onChange(""); } }, icon("undo-2", 15));
    control = h("div", { class: "bt-control" }, area, h("div", { class: "bt-meta" },
      it.placeholders ? h("span", { class: "bt-ph" }, ...it.placeholders.split(/,\s*/).map(p => h("code", {}, p))) : null,
      it.optional ? h("span", { class: "muted small" }, "- = hide") : null, h("span", { style: "flex:1" }), counter, reset));
  }
  const el = h("div", { class: "bt-row" }, h("div", { class: "bt-name" }, h("span", {}, it.name), custom), control);
  return { el };
}

// ================= Posts: panels and bot messages =================

const KIND_LABELS = {
  "panel:lfg": ["LFG panel", "ok"], "panel:tickets": ["Ticket panel", "ok"], "panel:rooms": ["Room panel", "ok"],
  "lfg-group": ["LFG group", "wait"], "ticket": ["Ticket", "wait"], "other": ["Message", ""]
};

async function pagePosts(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📌 Posts"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));

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
    h("p", { class: "muted small" }, "Panel messages the bot knows about. Deleting removes the message in Discord. 📌 Keep at the bottom — the panel is posted again under new messages, so it never scrolls away."),
    panelsBody));

  // --- Bot messages: only channels where the bot has posted; All or a chosen channel
  const msgCard = h("div", { class: "card" });
  main.append(msgCard);
  renderBotMessages(msgCard);

  try {
    const panels = await gApi("GET", "/posts/panels");
    panelsBody.replaceChildren(...panels.map(panelGroup));
  } catch (e) { panelsBody.replaceChildren(errorState(e.message)); }
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
        // Sticky: the bot reposts the panel at the bottom after new messages in the channel
        const sticky = h("input", { type: "checkbox", class: "switch", checked: !!m.sticky, "aria-label": "Keep at the bottom" });
        sticky.addEventListener("change", () => act(null, () => gApi("PUT", `/posts/panels/${p.key}/${m.messageId}/sticky`, { sticky: sticky.checked }),
          sticky.checked ? "📌 Sticky: the panel stays at the bottom of the channel." : "Sticky off."));
        return h("tr", {},
          h("td", {}, m.channel ? `#${m.channel}` : h("span", { class: "chip warn" }, "deleted channel")),
          h("td", { class: "muted small" }, fmtDate(m.createdAt)),
          h("td", {}, h("label", { class: "row", style: "gap:6px;white-space:nowrap", title: "After new messages in the channel the bot posts the panel again at the bottom (the old one is deleted). Waits for 15 s of silence, at most a minute." },
            sticky, "📌 Keep at the bottom")),
          h("td", { class: "actions" }, h("a", { class: "btn ghost", href: m.jumpUrl, target: "_blank", rel: "noopener" }, "Open"), " ", del));
      })))) : h("div", { class: "empty small" }, "None known. Post it from the module tab or use “Find posted panels”."));
}

/** Bot messages card: channel chips (only channels with the bot's messages), All = every one of them. */
async function renderBotMessages(card, refreshScan = false) {
  const head = cardHead("message-square-text", "Bot messages");
  const rescan = h("button", { class: "btn secondary" }, icon("refresh-cw", 15), "Rescan");
  rescan.addEventListener("click", () => renderBotMessages(card, true));
  head.append(rescan);
  card.replaceChildren(head,
    h("p", { class: "muted small" }, "Channels where the bot has messages (the last 50 messages of each channel are checked). Deleting an LFG group post closes the group."),
    h("div", { class: "muted" }, "Looking for the bot's messages…"));
  let channels;
  try { channels = await gApi("GET", `/posts/channels${refreshScan ? "?refresh=true" : ""}`); }
  catch (e) { card.replaceChildren(head, errorState(e.message)); return; }
  if (!channels.length) { card.replaceChildren(head, emptyState("message-square-text", "No bot messages found", "The bot hasn't posted in the channels it can read.")); return; }

  let current = store.get("gb.msgChannel") || "all";
  if (current !== "all" && !channels.some(c => c.id === current)) current = "all";
  const total = channels.reduce((a, c) => a + c.count, 0);
  const body = h("div", {});
  const chips = h("div", { class: "filter-chips" });
  const drawChips = () => chips.replaceChildren(...
    [{ id: "all", name: "All channels", count: total }, ...channels].map(c => h("button", {
      class: `filter-chip${c.id === current ? " active" : ""}`, title: c.category || "",
      onclick: () => { current = c.id; store.set("gb.msgChannel", c.id); drawChips(); load(); }
    }, c.id === "all" ? icon("layers", 14) : icon(c.voice ? "volume-2" : "hash", 14), c.name, h("span", { class: "count" }, c.count))));
  const load = () => loadMessages(current === "all" ? channels : channels.filter(c => c.id === current), body, load);
  drawChips();
  card.replaceChildren(head,
    h("p", { class: "muted small" }, "Channels where the bot has messages (the last 50 messages of each channel are checked). Deleting an LFG group post closes the group."),
    chips, body);
  load();
}

async function loadMessages(channels, body, reload) {
  body.replaceChildren(skeletonLines(3));
  const lists = await Promise.all(channels.map(c => gApi("GET", `/posts/channels/${c.id}/messages?limit=100`)
    .then(list => list.map(m => ({ ...m, channelId: c.id, channel: c })))
    .catch(() => [])));
  const list = lists.flat().sort((a, b) => b.createdAt - a.createdAt);
  if (!list.length) { body.replaceChildren(empty("No bot messages here any more.")); return; }
  const many = channels.length > 1;

  const checks = [];
  const delSel = h("button", { class: "btn danger" }, icon("trash-2", 15), "Delete selected");
  const all = h("input", { type: "checkbox", title: "Select all", "aria-label": "Select all" });
  all.addEventListener("change", () => checks.forEach(c => { c.cb.checked = all.checked; }));
  delSel.addEventListener("click", async () => {
    const picked = checks.filter(c => c.cb.checked);
    if (!picked.length) return toast("Nothing selected.", "warn");
    if (!await confirmDialog(`Delete ${picked.length} message(s)?`, "This can't be undone. LFG group posts close their groups.", "Delete")) return;
    const byChannel = {};
    for (const c of picked) (byChannel[c.channelId] = byChannel[c.channelId] || []).push(c.id);
    let deleted = 0, failed = 0;
    for (const [ch, ids] of Object.entries(byChannel)) {
      const res = await act(delSel, () => gApi("POST", `/posts/channels/${ch}/delete`, { messageIds: ids }));
      if (res) { deleted += res.deleted; failed += res.failed; }
    }
    toast(`Deleted ${deleted}${failed ? `, failed ${failed}` : ""}.`, failed ? "warn" : "ok");
    reload();
  });

  const rows = list.map(m => {
    const cb = h("input", { type: "checkbox", "aria-label": "Select" });
    checks.push({ cb, id: m.messageId, channelId: m.channelId });
    const [label, cls] = KIND_LABELS[m.kind] || [m.kind, ""];
    const del = h("button", { class: "btn ghost" }, "Delete");
    del.addEventListener("click", async () => {
      if (!await confirmDialog("Delete this message?", m.kind === "lfg-group" ? "The LFG group will be closed." : "This can't be undone.", "Delete")) return;
      if (await act(del, () => gApi("POST", `/posts/channels/${m.channelId}/delete`, { messageIds: [m.messageId] }), "Deleted.") !== undefined) reload();
    });
    return h("tr", {},
      h("td", {}, h("label", { class: "row-check" }, cb)),
      h("td", { class: "muted small", style: "white-space:nowrap" }, fmtDate(m.createdAt)),
      many ? h("td", { class: "small" }, `${m.channel.voice ? "🔊" : "#"} ${m.channel.name}`) : null,
      h("td", {}, h("span", { class: `badge ${cls}` }, label)),
      h("td", {}, m.title ? h("div", {}, m.title) : null, h("div", { class: "muted small" }, m.preview || "—")),
      h("td", { class: "actions" }, h("a", { class: "btn ghost", href: m.jumpUrl, target: "_blank", rel: "noopener" }, "Open"), " ", del));
  });

  body.replaceChildren(
    h("div", { class: "row", style: "margin:12px 0 8px" }, h("span", { class: "muted small" }, `${list.length} bot message(s)`), h("span", { style: "flex:1" }), delSel),
    h("div", { class: "table-wrap" }, h("table", { class: "table" },
      h("thead", {}, h("tr", {}, h("th", {}, h("label", { class: "row-check" }, all)), h("th", {}, "Posted"), many ? h("th", {}, "Channel") : null,
        h("th", {}, "Type"), h("th", {}, "Content"), h("th", {}))),
      h("tbody", {}, rows))));
}

// ================= Log =================

const MODULE_LABELS = { lfg: "📣 LFG", tickets: "🎫 Tickets", voice: "🔊 Voice", texts: "📝 Texts", posts: "📌 Posts",
  access: "🔑 Access", settings: "⚙️ Settings" };
const SOURCE_LABELS = { web: "🌐 site", discord: "💬 Discord", system: "🤖 bot" };

async function pageLog(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📜 Log"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
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
  main.append(h("div", { class: "card-head" }, h("h1", {}, "⚙️ Settings")),
    h("p", { class: "muted" }, "Time zone, auto-delete and reminder of LFG groups are in LFG → LFG settings."));
  const holder = h("div", {});
  main.append(holder);

  // Quick search (Ctrl+K): which members it finds
  if (can("settings")) {
    const card = h("div", { class: "card" }, h("h2", {}, "🔍 Quick search"),
      h("p", { class: "muted small" }, "Ctrl+K on the site finds people only with these roles (for example the guild member roles). Empty — everyone on the server."),
      h("div", { class: "muted" }, "Loading…"));
    holder.append(card);
    gApi("GET", "/search-roles").then(d => {
      const roles = roleListEditor(d.roles || []);
      const save = h("button", { class: "btn" }, "Save");
      const body = () => ({ roleIds: roles.value() });
      const tracker = track("Quick search roles", () => JSON.stringify(body()), async () =>
        await act(save, () => gApi("PUT", "/search-roles", body()), "Quick search roles saved.") !== undefined);
      save.addEventListener("click", tracker.save);
      card.lastChild.replaceWith(h("div", {}, h("div", { class: "field" }, fieldLabel("Search people with roles"), roles.el), save));
      tracker.attach(card);
    }).catch(e => card.lastChild.replaceWith(errorState(e.message)));
  }

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
    // Automatic copies made by the bot: after every start (last 5) and once a day (last 14)
    const autoList = h("div", { class: "muted small" }, "Loading…");
    api("GET", "/api/backup/auto").then(list => {
      const kind = n => n.startsWith("auto-start") ? "after a start" : n.startsWith("auto-day") ? "daily" : n.startsWith("pre-restore") ? "before a restore" : "";
      autoList.replaceChildren(...(list.length ? list.slice(0, 20).map(b => h("div", { class: "row", style: "margin-top:4px;gap:10px" },
        h("a", { class: "btn ghost sm", href: `/api/backup/auto/${encodeURIComponent(b.name)}`, download: "" }, "⬇️"),
        h("span", {}, new Date(b.at).toLocaleString()), h("span", { class: "chip" }, kind(b.name)), h("span", { class: "muted" }, `${Math.max(1, Math.round(b.size / 1024))} KB`)))
        : [h("span", {}, "No automatic copies yet — the first one is made a few seconds after the bot starts.")]));
    }).catch(e => { autoList.textContent = e.message; });
    holder.append(h("div", { class: "card" }, h("h2", {}, "💾 Backup"),
      h("p", { class: "muted small" }, "All bot data (data/*.json): activities, groups, characters, tickets, archive, settings, access… Site login keys are not included."),
      h("div", { class: "row", style: "margin-bottom:10px" }, download),
      h("div", { class: "row" }, file, restore),
      h("h3", { style: "margin-top:16px" }, "Automatic copies"),
      h("p", { class: "muted small" }, "The bot saves a copy after every start (last 5 kept) and once a day (last 14). Download one and restore it above if something got lost."),
      autoList));

    // Characters: export (JSON for another instance, CSV for spreadsheets) and import of such JSON
    const base = `/api/guilds/${state.guildId}/characters-transfer`;
    const jsonBtn = h("a", { class: "btn", href: `${base}?format=json`, download: "" }, "⬇️ Export JSON");
    const csvBtn = h("a", { class: "btn", href: `${base}?format=csv`, download: "" }, "⬇️ Export CSV");
    const cfile = h("input", { type: "file", accept: ".json,application/json" });
    const over = toggle(false, "Overwrite existing profiles (class, GS, server, armory link)");
    const imp = h("button", { class: "btn" }, "⬆️ Import");
    imp.addEventListener("click", async () => {
      const f = cfile.files && cfile.files[0];
      if (!f) return toast("Choose a characters .json first.", "warn");
      if (!await confirmDialog("Import characters from this file?",
        over.cb.checked ? "Missing characters are added; existing profiles are replaced with the file's data."
          : "Missing characters are added; existing ones keep their data.", "Import")) return;
      imp.disabled = true;
      try {
        const res = await fetch(`${base}/import?overwrite=${over.cb.checked}`, { method: "POST", headers: { "X-Panel": "1", "Content-Type": "application/json" }, body: f, credentials: "same-origin" });
        const d = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(d.error || `Import failed (${res.status})`);
        toast(`Characters: ${d.added} added, ${d.updated} updated, ${d.skipped} unchanged` +
          (d.full ? `, ${d.full} skipped (member has 24 already)` : "") + (d.bad ? `, ${d.bad} invalid` : "") + ".");
      } catch (e) { toast(e.message, "error"); }
      finally { imp.disabled = false; }
    });
    holder.append(h("div", { class: "card" }, h("h2", {}, "👥 Characters export"),
      h("p", { class: "muted small" }, "All characters of this server: owner, project, class, GS, server, comment, armory link. ",
        "JSON — to move them to another bot instance (Import below on that instance); CSV — for spreadsheets."),
      h("div", { class: "row", style: "margin-bottom:10px" }, jsonBtn, csvBtn),
      h("div", { class: "row", style: "margin-bottom:6px" }, cfile, imp), over.el));
  }

  // Bot DMs: all off by default
  if (can("settings")) {
    const dmCard = h("div", { class: "card" }, h("h2", {}, "✉️ Direct messages"), skeletonLines(3));
    holder.append(dmCard);
    gApi("GET", "/dm-prefs").then(p => {
      const items = [
        ["lfgNewGroup", "📣 New LFG group — to members with the activity role (they can still turn it off for themselves)"],
        ["lfgPromoted", "⬆️ Moved from the reserve into the group"],
        ["lfgTimeChanged", "🕒 The group's start time changed"],
        ["ticketResult", "🎫 Ticket result — approved / denied"],
        ["gsReminder", "📈 GS reminder (LFG → Group options)"]
      ].map(([k, label]) => ({ k, t: toggle(!!p[k], label) }));
      const save = h("button", { class: "btn" }, "💾 Save");
      const body = () => Object.fromEntries(items.map(x => [x.k, x.t.cb.checked]));
      const tracker = track("Direct messages", body, async () => await act(save, () => gApi("PUT", "/dm-prefs", body()), "DM settings saved.") !== undefined);
      save.addEventListener("click", tracker.save);
      dmCard.replaceChildren(h("h2", {}, "✉️ Direct messages"),
        h("p", { class: "muted small" }, "Which DMs the bot may send on this server. All are off until you turn them on. The welcome DM is set in Welcome."),
        ...items.map(x => x.t.el), save);
      tracker.attach(dmCard);
    }).catch(e => dmCard.replaceChildren(h("h2", {}, "✉️ Direct messages"), errorState(e.message)));
  }

  // Member roles of the selected server
  const card = h("div", { class: "card" }, h("h2", {}, "👥 Member roles"),
    h("p", { class: "muted small" }, "Everyone with one of these roles on this server can log in and use the Member section (Groups, My characters). Admin sections still need rights from Access."));
  holder.append(card);
  try {
    const current = await gApi("GET", "/member-roles");
    const editor = roleListEditor(current);
    const saveRoles = h("button", { class: "btn" }, "💾 Save member roles");
    const tracker = track("Member roles", () => editor.value(), async () =>
      await act(saveRoles, () => gApi("PUT", "/member-roles", { roleIds: editor.value() }), "Member roles saved.") !== undefined);
    saveRoles.addEventListener("click", tracker.save);
    card.append(editor.el, h("div", { class: "row", style: "margin-top:12px" }, saveRoles));
    tracker.attach(card);
  } catch (e) { card.append(errorState(e.message)); }
}

// ================= Member: home =================

async function pageHome(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Home"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
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
      statCard("users", "Members", sum.server.members, sum.server.name, null, sum.trends.members),
      statCard("swords", "Active groups", sum.activeGroups, sum.recruiting ? `${sum.recruiting} recruiting` : "none recruiting", go("play")),
      // Ticket staff see what's waiting for them; everyone else — how active applications were this week
      can("tickets") || !sum.ticketWeek
        ? statCard("ticket", "Open tickets", sum.openTickets, "applications in progress", can("tickets") ? go("tickets") : null, sum.trends.tickets)
        : statCard("ticket", "Tickets · 7 days", sum.ticketWeek.opened, `opened · ${sum.ticketWeek.processed} processed`, null, { history: sum.ticketWeek.history }),
      statCard("headphones", "Voice rooms", sum.voiceRooms, "temporary rooms now", can("voice") ? go("voice") : null)));
  }).catch(e => server.replaceChildren(sectionLabel("Server"), errorState(e.message)));

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
  try { d = await gApi("GET", "/me/home"); } catch (e) { groupsCard.replaceChildren(errorState(e.message)); return; }
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
    const tracker = track("Room defaults", () => [pn.value, pl.value, pk.checked], async () => {
      if (pl.value !== "" && (Number(pl.value) < 0 || Number(pl.value) > 99)) { toast("The limit is 0–99.", "warn"); return false; }
      return await act(ps, () => gApi("PUT", "/me/room-prefs", { name: pn.value || null, limit: pl.value === "" ? null : Number(pl.value), locked: pk.checked }),
        "Room defaults saved — used for your next room.") !== undefined;
    });
    ps.addEventListener("click", tracker.save);
    tracker.attach(prefsCard);
    prefsCard.append(
      h("div", { class: "field" }, h("label", {}, "Room name"), pn, h("span", { class: "muted small" }, "{user} is replaced with your name.")),
      h("div", { class: "row", style: "margin-bottom:12px" },
        h("div", { class: "field", style: "margin:0" }, h("label", {}, "User limit"), pl),
        h("label", { class: "row", style: "margin-top:20px" }, pk, "Lock the room right away")),
      ps);
  }).catch(e => prefsCard.append(errorState(e.message)));

  // My stats
  gApi("GET", "/me/stats").then(st => {
    statsCard.replaceChildren(statsCard.firstChild,
      h("div", { class: "stats compact" },
        statCard("users-round", "Groups", st.groups, "played"),
        statCard("crown", "Organized", st.organized, "as organizer"),
        statCard("circle-check", "Reliability", st.reliability === null ? "—" : `${st.reliability}%`, `came ${st.attended} · no-show ${st.noShows}`)),
      st.last30 ? h("div", { class: "stats compact", style: "margin-top:8px" },
        statCard("calendar-clock", "Last 30 days", st.last30.groups, "groups"),
        statCard("circle-check", "Came", st.last30.came, `no-show ${st.last30.noShow}`),
        statCard("swords", "Favorite role", st.last30.role || "—", "most played")) : null,
      st.recent.length ? h("div", { class: "list" }, st.recent.slice(0, 5).map(r => h("div", { class: "list-row" },
        h("div", { class: "grow" }, h("b", {}, r.activity), h("div", { class: "muted small" }, fmtDate(r.at))),
        r.organizer ? h("span", { class: "badge" }, icon("crown", 12), "Organizer")
          : r.attended === true ? h("span", { class: "badge ok" }, icon("check", 12), "Came")
          : r.attended === false ? h("span", { class: "badge lock" }, icon("x", 12), "No-show") : null)))
        : h("p", { class: "muted small" }, "Your finished groups will appear here."));
  }).catch(e => statsCard.replaceChildren(statsCard.firstChild, errorState(e.message)));

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
  }).catch(e => charsCard.replaceChildren(charsCard.firstChild, errorState(e.message)));
}

/** Apply for a project from the site (with the project's form, if enabled). */
async function applyDialog() {
  let projects;
  try { projects = await gApi("GET", "/me/tickets/projects"); } catch (e) { return toast(e.message, "error"); }
  if (!projects.length) return toast("There are no ticket types available to you.", "warn");

  const d = dialog();
  // Сгруппировано по проектам бота: optgroup «WWM» → типы тикетов
  const label = p => p.openTicketUrl ? `${p.name} (already open)` : p.hasRoles ? `${p.name} (you already have the roles)` : p.name;
  const groupsOrder = [...new Set(projects.map(p => p.group || ""))].sort((a, b) => (a === "") - (b === "") || a.localeCompare(b));
  const sel = h("select", {}, groupsOrder.length > 1
    ? groupsOrder.map(g => h("optgroup", { label: g || "General" }, projects.filter(p => (p.group || "") === g).map(p => h("option", { value: p.key }, label(p)))))
    : projects.map(p => h("option", { value: p.key }, label(p))));
  const fields = h("div", {});
  let inputs = [];
  const draw = () => {
    const p = projects.find(x => x.key === sel.value);
    inputs = [];
    send.disabled = !!p.hasRoles;
    if (p.hasRoles) {
      fields.replaceChildren(h("p", {}, "You already have all the roles this ticket gives — no need to apply."));
      return;
    }
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
  const send = h("button", { class: "btn" }, "📨 Send application");
  sel.addEventListener("change", draw);
  draw();

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

/** Тексты по модулям: в каждом — тексты панелей модуля и все его сообщения бота. «All» — всё сразу (поиск по всему). */
const TEXT_MODULES = [
  ["lfg", "LFG", "swords", ["lfg", "lfgpost"], g => g.startsWith("LFG")],
  ["tickets", "Tickets", "ticket", ["tickets", "ticketmsg"], g => g.startsWith("Tickets")],
  ["voice", "Temp Voice", "headphones", ["rooms"], g => g.startsWith("Temp voice")],
  ["chars", "Characters", "users", [], g => g.startsWith("Characters")],
  ["community", "Welcome & role panels", "door-open", [], g => g.startsWith("Welcome")],
  ["birthdays", "Birthdays", "cake", [], g => g.startsWith("Birthdays")],
];
const TEXT_MODULE_OTHER = g => !TEXT_MODULES.some(m => m[4](g));
const ALL_TEXT_GROUPS = () => true;

async function pageContent(main) {
  const saved = store.get("gb.content");
  const sub = saved === "posts" && can("posts") ? "posts" : can("texts") ? "texts" : "posts";
  const top = h("div", { class: "tabs" },
    can("texts") ? h("button", { class: `btn secondary${sub === "texts" ? " active-tab" : ""}`, onclick: () => { store.set("gb.content", "texts"); refresh(); } }, "📝 Texts by module") : null,
    can("posts") ? h("button", { class: `btn secondary${sub === "posts" ? " active-tab" : ""}`, onclick: () => { store.set("gb.content", "posts"); refresh(); } }, "📌 Posts & messages") : null);
  main.append(h("div", { class: "card-head" }, h("h1", {}, "📌 Panels & posts"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")), top);
  const inner = h("div", {});
  main.append(inner);
  if (sub === "posts") {
    await pagePosts(inner);
    const h1 = inner.querySelector("h1");
    if (h1) h1.replaceWith(h("h2", { class: "sub-title" }, h1.textContent.replace(/^[^\p{L}\p{N}]+/u, "")));
    return;
  }

  // Модуль: вкладки второго уровня (как подпроекты в тикетах)
  const mods = [...TEXT_MODULES, ["general", "General", "settings", [], TEXT_MODULE_OTHER], ["all", "All texts", "search", null, null]];
  let mod = store.get("gb.textsModule") || "lfg";
  if (!mods.some(m => m[0] === mod)) mod = "lfg";
  inner.append(h("div", { class: "tabs sub-tabs" }, mods.map(([k, label, ic]) =>
    h("button", { class: `btn secondary sm${k === mod ? " active-tab" : ""}`, onclick: async () => { if (!await leavePage()) return; store.set("gb.textsModule", k); renderShell(); } },
      icon(ic, 14), label))));
  inner.append(h("p", { class: "muted small" },
    "Each module has its panels (the posted messages with buttons) and its bot messages — replies, DMs, posts, menus, buttons, windows and stripe colors. ",
    "Words in {braces} are filled in by the bot. Empty field = default text; “- = hide” fields: a single - removes the line. Saving updates what is already posted."));

  const m = mods.find(x => x[0] === mod);
  if (mod === "all") {
    inner.append(sectionLabel("Panels"));
    await pageTexts(inner, ["lfg", "lfgpost", "tickets", "ticketmsg", "rooms"]);
    inner.append(sectionLabel("Bot messages"));
    await pageBotTexts(inner, ALL_TEXT_GROUPS);
    return;
  }
  if (m[3].length) {
    inner.append(sectionLabel(`${m[1]} — panels`));
    await pageTexts(inner, m[3]);
  }
  inner.append(sectionLabel(`${m[1]} — bot messages`));
  await pageBotTexts(inner, m[4]);
}

// ================= Member: groups =================

// Roles players sign up with («All» is only a slot type — anyone can take such a slot with their role)
const ROLE_CHOICES = [["Dps", "⚔️ DD"], ["Tank", "🛡️ Tank"], ["Healer", "💚 Sup"]];

async function pagePlay(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎮 Groups"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
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
    h("p", { class: "muted small" }, "Posted like /lfg in Discord. You are the organizer and take slot 1 with the character you choose."),
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
        templates: opt.templates, roles: opt.roles, relatedRoles: opt.relatedRoles || {}, projects: opt.projects, mirrorChannels: opt.mirrorChannels,
        characters: opt.characters || {},
        initial: tab !== "all" ? { projectKey: tab } : {} });
      const publish = h("button", { class: "btn" }, "📣 Publish");
      publish.addEventListener("click", async () => {
        const res = await act(publish, () => gApi("POST", "/me/create", form.value()));
        if (res === undefined) return;
        toast(res.dmsStarted ? "Group published. Sending DMs to the activity role…" : "Group published.");
        refresh();
      });
      createBody.replaceChildren(form.el, h("div", { class: "row" }, publish));
    } catch (e) { createBody.replaceChildren(errorState(e.message)); }
  });
  main.insertBefore(createCard, holder);
  // Home → «Create a group» opens the form right away
  if (store.get("gb.openCreate") === "1") { store.set("gb.openCreate", ""); toggle.click(); }

  let data;
  try { data = await gApi("GET", "/me/groups"); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }
  if (tab !== "all") data.groups = data.groups.filter(g => g.projectKey === tab);
  const focus = store.get("gb.focusGroup");
  if (focus) store.set("gb.focusGroup", "");
  holder.replaceChildren(sectionLabel(`Active groups · ${data.groups.length}`), data.groups.length
    ? h("div", { class: "grid2" }, data.groups.map(g => {
      const card = playCard(g);
      if (g.id === focus) requestAnimationFrame(() => { card.classList.add("focus-flash"); card.scrollIntoView({ behavior: "smooth", block: "center" }); });
      return card;
    }))
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
    ? h("div", { class: `slot taken${s.member.id === meId ? " me" : ""}${attClass(s)}` },
        h("span", { class: "slot-num" }, i + 1), rolePill(s.role || s.slotRole),
        slotInfo(s, "Ready", false), slotTools(
        org && g.started ? attendanceButton(s, `/me/groups/${gid}/attendance`) : null,
        org && (!g.owner || s.member.id !== g.owner.id) ? tool("arrow-right", "Move to reserve", () => roster$("toReserve", s.member, "Moved to the reserve.")) : null))
    : h("div", { class: "slot open" },
        h("span", { class: "slot-num" }, i + 1), rolePill(s.slotRole), slotReqMark(s),
        h("span", { class: "slot-open-text" }, icon("circle-plus", 14), "Open slot"))));
  const reserve = g.reserve.length ? h("details", { class: "reserve", open: org },
    h("summary", {}, icon("clock", 14), `Reserve · ${g.reserve.length}`),
    h("div", { class: "roster" }, g.reserve.map(r => h("div", { class: "slot taken reserve-row" },
      rolePill(r.role || "All"), slotInfo(r, null, false), slotTools(
      org ? tool("arrow-left", "Move into the group (sends a DM)", () => roster$("promote", r.member, "Moved into the group, DM sent.")) : null,
      org ? tool("x", "Remove from the reserve", e => removeWithUndo(e.currentTarget.closest(".slot"), `${r.member.name} removed from the reserve.`,
        () => gApi("POST", `/me/groups/${gid}/roster`, { action: "removeReserve", userId: r.member.id }), refresh)) : null))))) : null;

  const closeBtn = org ? h("button", { class: "btn danger" }, icon("trash-2", 15), "Close group") : null;
  if (closeBtn) closeBtn.addEventListener("click", async () => {
    if (!await confirmDialog(`Close ${g.activity}?`, "The post is deleted in Discord. Copy the invites first if you need them.", "Close & delete")) return;
    if (await act(closeBtn, () => gApi("POST", `/me/groups/${gid}/close`), "Group closed.") !== undefined) refresh();
  });

  // Sign-up form: role + character (saved or new)
  const free = v => g.canJoin[v] || (g.me && g.me.status === "group" && g.me.role === v);
  const role = h("select", { "aria-label": "Role", "data-render": "role" }, ROLE_CHOICES.map(([v, l]) => h("option", { value: v }, free(v) ? l : `${l} (reserve)`)));
  // Characters whose class plays the chosen role (LFG → Classes), like in Discord; "All" — any
  const charRoles = g.characterRoles || {};
  const fits = (c, r) => r === "Any" || !charRoles[c] || charRoles[c].includes(r);
  const charSel = h("select", { "aria-label": "Character" });
  const newChar = h("input", { placeholder: "Character name", maxlength: "32", style: "width:160px", "aria-label": "New character name" });
  const sync = () => { newChar.style.display = charSel.value ? "none" : ""; };
  const fillChars = () => {
    const keep = charSel.value;
    const ok = characters.filter(c => fits(c, role.value));
    const info = g.characterInfo || {};
    charSel.replaceChildren(...ok.map(c => {
      const x = info[c] || {};
      return h("option", { value: c, "data-icon": x.icon || null },
        [c, x.gs != null ? `GS ${x.gs}` : null, x.cp != null ? `CP ${fmtCp(x.cp)}` : null].filter(Boolean).join(" · "));
    }), h("option", { value: "" }, "+ New character…"));
    charSel.title = ok.length < characters.length ? `${characters.length - ok.length} character(s) hidden — their class doesn't play this role` : "";
    if (ok.includes(keep)) charSel.value = keep;
    sync();
  };
  charSel.addEventListener("change", sync);
  role.addEventListener("change", fillChars);
  if (g.me && ROLE_CHOICES.some(([v]) => v === g.me.role)) role.value = g.me.role;
  fillChars();
  if (g.me && g.me.character && fits(g.me.character, role.value) && characters.includes(g.me.character)) { charSel.value = g.me.character; sync(); }

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
      h("div", { class: "meta-item" }, icon("crown", 14), g.owner ? member(g.owner) : h("span", { class: "muted" }, "no organizer")),
      h("div", { class: "meta-item" }, icon("calendar-clock", 14), g.startsAt ? fmtDate(g.startsAt) : "No time set"),
      g.channel ? h("div", { class: "meta-item" }, icon("hash", 14), g.channel) : null,
      mine, req),
    groupNoteLine(g),
    roster, reserve,
    g.readyCheck && g.me && g.me.status === "group" ? h("div", { class: "group-note ready-line" },
      g.myReady ? h("span", { class: "badge ok" }, "🟢 You're marked as ready")
        : h("button", { class: "btn sm", onclick: async e => {
          if (await act(e.currentTarget, () => gApi("POST", `/me/groups/${gid}/ready`), "Marked as ready.") !== undefined) refresh();
        } }, "✅ I'm ready"),
      h("span", { class: "muted small" }, `${g.slots.filter(s => s.ready).length}/${g.slots.filter(s => s.member).length} ready`)) : null,
    g.closed ? h("div", { class: "muted small group-note" }, icon("lock", 14), "Signups are closed.") :
      g.blocked && !g.me ? h("div", { class: "muted small group-note" }, icon("lock", 14), g.blocked) :
      h("div", { class: "group-join" }, role, charSel, newChar, join, g.me && !org ? leave : null),
    h("div", { class: "group-actions" },
      h("a", { class: "btn secondary", href: g.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 15), "Open in Discord"),
      g.voiceUrl ? h("a", { class: "btn secondary tone-teal voice-btn", href: g.voiceUrl, target: "_blank", rel: "noopener", title: "Open the group's voice room in Discord" }, icon("volume-2", 15), "Voice room") : null,
      org ? h("button", { class: "btn secondary", onclick: () => rescheduleDialog(g, `/me/groups/${gid}/time`) }, icon("clock", 15), "Time") : null,
      org ? h("button", { class: "btn secondary", onclick: () => groupNoteDialog(g, `/me/groups/${gid}/note`) }, icon("file-pen-line", 15), "Note") : null,
      closeBtn ? h("span", { class: "spacer" }) : null, closeBtn));
}

// ================= Member: characters =================

/** My characters: the official armory profile (linked — data and buttons; not linked — the Link button). */
function armoryBox(c, pq, gsSource, servers, realm) {
  const path = `/me/characters/${encodeURIComponent(c.name)}/armory`;
  const linked = res => (res.gs != null ? `Linked to the official profile. GS ${res.gs.toLocaleString()} taken from the game.` : "Linked to the official profile.") +
    (res.renamed ? ` Name corrected to ${res.renamed}.` : "");
  const link = async e => {
    const btn = e.currentTarget;
    const r = realm.value();
    const name = c.armory && c.armory.name || c.name;
    let hitsForDialog = null;
    // Exactly one character with this exact name on the chosen faction/server — link it right away, no list
    if (name.length >= 2) {
      btn.disabled = true;
      let hits = null;
      try { hits = await gApi("GET", `/me/armory/search?name=${encodeURIComponent(name)}${r.serverId ? `&server=${r.serverId}` : ""}`); } catch { /* fall back to the dialog */ }
      btn.disabled = false;
      hitsForDialog = hits;
      const exact = (hits || []).filter(x => x.name.toLowerCase() === name.toLowerCase() && Math.floor(x.serverId / 1000) === r.race);
      if (exact.length === 1) {
        if (await act(btn, () => gApi("POST", `${path}${pq}`, { characterId: exact[0].characterId, serverId: exact[0].serverId }), linked) !== undefined) refresh();
        return;
      }
    }
    const hit = await armoryPickDialog(name, servers, r.race, r.serverId, hitsForDialog);
    if (!hit) return;
    if (await act(btn, () => gApi("POST", `${path}${pq}`, { characterId: hit.characterId, serverId: hit.serverId }), linked) !== undefined) refresh();
  };
  const a = c.armory;
  if (!a) return h("div", { class: "armory-box" },
    h("span", { class: "muted small grow" }, icon("link", 13), gsSource === "none"
      ? " Link the official profile — combat power, item level and legion come from there."
      : ` Link the official profile — GS (${gsSource === "combatPower" ? "combat power" : "item level"}) then comes from there.`),
    h("button", { class: "btn secondary sm", onclick: link }, icon("link", 14), "Link profile"));
  const stat = (label, v) => v == null || v === "" ? null : h("span", { class: "chip" }, h("span", { class: "muted" }, label + " "), typeof v === "number" ? v.toLocaleString() : v);
  return h("div", { class: `armory-box linked${a.error ? " stale" : ""}` },
    h("div", { class: "grow" },
      h("div", { class: "armory-stats" }, stat("Lv", a.level || null), stat("Class", a.className), a.server ? serverChip(a.server, Math.floor(a.serverId / 1000)) : null,
        stat("CP", a.combatPower), stat("Item lvl", a.itemLevel), stat("Legion", a.legion)),
      h("div", { class: "muted small", style: "margin-top:4px" }, a.error
        ? "⚠️ Not found in the official armory anymore (renamed or deleted?) — link it again."
        : `Official profile · updated ${a.synced ? fmtRelative(a.synced) : "—"} · press Update after you level up or change gear`)),
    h("div", { class: "row", style: "gap:4px" },
      a.error ? h("button", { class: "btn secondary sm", onclick: link }, icon("link", 14), "Link again")
        : h("button", { class: "btn ghost sm", title: "Update now (once per 15 minutes)", onclick: async e => {
          if (await act(e.currentTarget, () => gApi("POST", `${path}/refresh${pq}`), r => r && r.renamed ? `Updated. The character was renamed in the game — now ${r.renamed}.` : "Updated from the official profile.") !== undefined) refresh();
        } }, icon("refresh-cw", 14), "Update"),
      armoryLink(a, "Open profile"),
      h("button", { class: "btn ghost sm", title: "Unlink", "aria-label": "Unlink the official profile", onclick: async e => {
        if (!await confirmDialog(`Unlink ${c.name}?`, "The official data is removed; GS can be entered by hand again.", "Unlink")) return;
        if (await act(e.currentTarget, () => gApi("DELETE", `${path}${pq}`), "Unlinked.") !== undefined) refresh();
      } }, icon("unlink", 14))));
}

/** "GS updated 20 days ago" — highlighted when older than the server's limit (or not set). */
function gsAge(c, staleDays = 7, pq = "") {
  if (c.gs == null) return h("div", { class: "gs-age stale" }, icon("triangle-alert", 13), "GS not set — groups with a GS requirement put you in the reserve");
  if (!c.gsUpdated) return h("div", { class: "gs-age stale" }, icon("clock", 13), "GS update date unknown",
    h("button", { class: "btn ghost sm", onclick: async e => { if (await act(e.currentTarget, () => gApi("POST", `/me/characters/${encodeURIComponent(c.name)}/gs-confirm${pq}`), "GS confirmed.") !== undefined) refresh(); } },
      icon("check", 13), "Still current"));
  const days = Math.floor((Date.now() - c.gsUpdated) / 86400000);
  const stale = days >= staleDays;
  const confirm = stale ? h("button", { class: "btn ghost sm", title: "The GS hasn't changed — mark it as current",
    onclick: async e => { if (await act(e.currentTarget, () => gApi("POST", `/me/characters/${encodeURIComponent(c.name)}/gs-confirm${pq}`), "GS confirmed.") !== undefined) refresh(); } },
    icon("check", 13), "Still current") : null;
  return h("div", { class: `gs-age${stale ? " stale" : ""}`, title: new Date(c.gsUpdated).toLocaleString() },
    icon(stale ? "triangle-alert" : "clock", 13), days === 0 ? "GS updated today" : `GS updated ${days} day${days === 1 ? "" : "s"} ago`, stale ? " — time to update" : "", confirm);
}

async function pageChars(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "My characters"), h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(3)));
  main.append(holder);

  let data;
  const wanted = store.get("gb.charProject") || "";
  try { data = await gApi("GET", `/me/characters?project=${encodeURIComponent(wanted)}`); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }
  const pq = data.project ? `?project=${encodeURIComponent(data.project)}` : "";
  const project = data.projects.find(p => p.key === data.project);

  // Projects (games): each has its own characters; locked without the project role
  const projTabs = data.projects.length ? h("div", { class: "tabs" },
    data.projects.map(p => h("button", {
      class: `btn secondary${p.key === data.project ? " active-tab" : ""}`, disabled: !p.allowed,
      title: p.allowed ? "" : `Needs ${p.roles.map(r => "@" + r.name).join(" or ")}`,
      onclick: () => { store.set("gb.charProject", p.key); refresh(); }
    }, icon(p.allowed ? "gamepad-2" : "lock", 15), p.name))) : null;

  // My notifications: only the DMs an admin turned on for this server
  const NOTIFY_LABELS = {
    lfgNewGroup: ["📣 New groups", "A group is posted for an activity whose role you have. Same as 🔔 / 🔕 under the LFG panel or /lfg-dm."],
    lfgPromoted: ["⬆️ Moved into the group", "You were moved from the reserve into the group."],
    lfgTimeChanged: ["🕒 Start time changed", "The organizer changed the time of a group you're in."],
    ticketResult: ["🎫 Ticket result", "Your application was approved or denied."],
    gsReminder: ["📈 GS reminder", "Weekly: time to update the GS of your characters."]
  };
  const prefs = h("div", { class: "card" }, cardHead("bell", "My notifications"), skeletonLines(2));
  gApi("GET", "/me/notify").then(list => {
    prefs.replaceChildren(cardHead("bell", "My notifications"), list.length
      ? h("div", {}, list.map(n => {
        const [title, hint] = NOTIFY_LABELS[n.key] || [n.key, ""];
        const sw = h("input", { type: "checkbox", class: "switch", checked: n.enabled, "aria-label": title });
        sw.addEventListener("change", () => act(null, () => gApi("PUT", "/me/notify", { key: n.key, enabled: sw.checked }),
          `${title.replace(/^\S+\s/, "")}: ${sw.checked ? "on" : "off"}.`));
        return h("div", { class: "pref-row" }, h("div", { class: "grow" }, h("b", {}, title), h("div", { class: "muted small" }, hint)), sw);
      }))
      : h("p", { class: "muted small" }, "The bot sends no DMs on this server right now — admins turn them on in Settings."));
  }).catch(e => prefs.replaceChildren(cardHead("bell", "My notifications"), errorState(e.message)));

  const isCustom = ic => /^<a?:[^:]+:\d+>$/.test(ic || "");
  const ap = data.armoryProject && data.armoryProject.enabled ? data.armoryProject : null;
  let servers = [];
  if (ap) try { servers = await armoryServers(); } catch { /* the selects stay empty */ }
  const cards = data.characters.map(c => {
    const realm = realmPicker(servers, c.race || ap?.race, c.race || c.serverId ? c.serverId : ap?.serverId, !!c.armory);
    // <option> can't show images: unicode icons go in the text, server/bot emojis are shown in the card header
    const linkedOk = !!(c.armory && !c.armory.error);
    const cls = h("select", { "aria-label": "Class", disabled: linkedOk, title: linkedOk ? "Comes from the official armory — press Update to refresh" : "" }, h("option", { value: "" }, "No class"),
      data.classRoles.map(r => h("option", { value: r.id, "data-icon": r.icon || null }, r.name)));
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
    const gs = h("input", { type: "number", min: "0", max: "99999", placeholder: "—", value: c.gs ?? "", disabled: c.gsLocked,
      title: c.gsLocked ? "Comes from the official armory — press Update to refresh" : "" });
    const save = h("button", { class: "btn" }, icon("save", 15), "Save");
    const tracker = track(c.name, () => [cls.value, note.value, gs.value, realm.race.value, realm.server.value], async () => {
      if (gs.value !== "" && (Number(gs.value) < 0 || Number(gs.value) > 99999)) { toast("GS: 0–99999.", "warn"); return false; }
      return await act(save, () => gApi("PUT", `/me/characters/${encodeURIComponent(c.name)}${pq}`, { classRoleId: cls.value || null, comment: note.value, gs: gs.value === "" ? null : Number(gs.value),
        ...(ap ? realm.value() : { race: c.race ?? null, serverId: c.serverId ?? null }) }),
        r => r.groupsUpdated ? `Saved. Updated ${r.groupsUpdated} active group(s).` : "Saved.") !== undefined;
    });
    save.addEventListener("click", tracker.save);
    const rm = menuButton([{ icon: "trash-2", label: `Remove ${c.name}`, danger: true, onclick: () => removeWithUndo(rm.closest(".card"), `${c.name} removed.`,
      () => gApi("DELETE", `/me/characters/${encodeURIComponent(c.name)}${pq}`), refresh) }]);
    // Rename: class, note, GS and active groups follow the new name
    const rename = h("button", { class: "btn ghost icon-only sm", title: "Rename", "aria-label": `Rename ${c.name}` }, icon("pencil", 15));
    rename.addEventListener("click", async () => {
      const name = await promptDialog(`Rename ${c.name}`, c.name);
      if (!name || name === c.name) return;
      if (await act(rename, () => gApi("POST", `/me/characters/${encodeURIComponent(c.name)}/rename${pq}`, { name }),
        r => r.groupsUpdated ? `Renamed. Updated ${r.groupsUpdated} active group(s).` : "Renamed.") !== undefined) refresh();
    });
    return tracker.attach(h("div", { class: "card char-edit" },
      h("div", { class: "char-edit-head" }, badge,
        h("div", { class: "grow" }, h("div", { class: "row", style: "gap:4px;flex-wrap:nowrap" }, h("b", { class: "char-name" }, c.name), rename), clsName),
        h("div", { class: "row", style: "gap:4px;flex-direction:column;align-items:flex-end" },
          c.gs ? h("span", { class: "gs big" }, `GS ${c.gs}`) : null,
          c.armory && c.armory.combatPower != null ? h("span", { class: "gs big cp" }, `CP ${fmtCp(c.armory.combatPower)}`) : null)),
      c.gsLocked ? null : gsAge(c, data.gsStaleDays, pq),
      ap ? armoryBox(c, pq, data.armoryGs, servers, realm) : null,
      h("div", { class: "char-fields" },
        h("div", { class: "field" }, h("label", {}, linkedOk ? "Class · auto" : "Class"), cls),
        h("div", { class: "field gs-field" }, h("label", {}, c.gsLocked ? "GS · auto" : "GS"), gs)),
      ap ? h("div", { class: "char-fields" },
        h("div", { class: "field" }, h("label", {}, "Faction"), realm.race),
        h("div", { class: "field" }, h("label", {}, "Server"), realm.server)) : null,
      h("div", { class: "field" }, h("label", {}, "Personal note — only you see it"), note),
      h("div", { class: "char-actions" }, save, h("span", { class: "spacer" }), rm)));
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
  // Other characters from the official armory: names → found on the server → created and linked
  const linkedOne = data.characters.find(c => c.armory && !c.armory.error);
  const altsBtn = ap ? h("button", { class: "btn secondary", title: "Enter the names of your other characters — they are found on the official site and added with GS, class and server" },
    icon("users", 15), "Add other characters…") : null;
  if (altsBtn) altsBtn.addEventListener("click", () => {
    const d = dialog();
    const names = h("textarea", { rows: "4", placeholder: "One name per line (or separated by commas)", style: "width:100%" });
    const realm = realmPicker(servers, linkedOne ? Math.floor(linkedOne.armory.serverId / 1000) : ap.race,
      linkedOne ? linkedOne.armory.serverId : ap.serverId);
    const go = h("button", { class: "btn" }, icon("search", 15), "Find & add");
    const result = h("div", { class: "muted small" });
    go.addEventListener("click", async () => {
      const list = names.value.split(/[\n,;]+/).map(x => x.trim()).filter(Boolean);
      const r0 = realm.value();
      if (!list.length) return toast("Enter at least one name.", "warn");
      if (!r0.serverId) return toast("Pick the server.", "warn");
      result.replaceChildren(skeletonLines(1));
      const r = await act(go, () => gApi("POST", `/me/characters/armory-alts${pq}`, { names: list, serverId: r0.serverId }));
      if (!r) { result.replaceChildren(); return; }
      result.replaceChildren(...[
        r.added.length ? h("div", {}, `✅ Added and linked: ${r.added.join(", ")}`) : null,
        r.existing.length ? h("div", {}, `Already in your list: ${r.existing.join(", ")}`) : null,
        r.notFound.length ? h("div", { class: "warn-text" }, `Not found on this server: ${r.notFound.join(", ")} — check the spelling or the server`) : null,
        r.unavailable ? h("div", { class: "warn-text" }, "The official armory stopped answering — try the rest later.") : null].filter(Boolean));
      if (r.added.length) d.addEventListener("close", () => refresh(), { once: true });
    });
    d.replaceChildren(h("h2", {}, "Add other characters"),
      h("p", { class: "muted small" }, "Exact names from the game. Each one is found on the chosen server of the official site and added already linked — GS, class and server come from there."),
      h("div", { class: "row", style: "flex-wrap:wrap;margin-bottom:8px" }, realm.race, realm.server), names, result,
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Close"), go));
    d.showModal();
    names.focus();
  });
  const addCard = h("div", { class: "card char-add" }, h("span", { class: "es-icon tone-cyan" }, icon("user-plus", 20)),
    h("div", { class: "grow" }, h("b", {}, "New character"), h("div", { class: "muted small" }, project ? `In ${project.name}` : "Saved for sign-ups")),
    h("div", { class: "row" }, h("div", { class: "grow" }, nameIn), add),
    altsBtn ? h("div", { class: "row", style: "margin-top:8px" }, altsBtn) : null);

  // Update all linked characters of the project at once
  const linkedCount = data.characters.filter(c => c.armory).length;
  const updAll = ap && linkedCount ? h("button", { class: "btn secondary", title: "Refresh GS, CP, class, level and legion of all your linked characters (skips the ones updated in the last 15 minutes)" },
    icon("refresh-cw", 15), `Update all (${linkedCount})`) : null;
  if (updAll) updAll.addEventListener("click", async () => {
    const r = await act(updAll, () => gApi("POST", `/me/characters/armory-refresh-all${pq}`));
    if (!r) return;
    const parts = [`Updated ${r.updated}`];
    if (r.skipped) parts.push(`${r.skipped} were updated less than 15 min ago`);
    if (r.renamed.length) parts.push(`renamed: ${r.renamed.join(", ")}`);
    if (r.notFound.length) parts.push(`not found: ${r.notFound.join(", ")}`);
    if (r.unavailable) parts.push("the official site stopped answering — try the rest later");
    toast(parts.join(" · ") + ".", r.unavailable || r.notFound.length ? "warn" : undefined);
    refresh();
  });
  holder.replaceChildren(prefs, projTabs,
    h("div", { class: "row", style: "margin:-6px 0 14px;align-items:center" },
      h("p", { class: "muted small grow", style: "margin:0" }, "Each game has its own characters. Class — any class of the project (its icon is shown after your name in group posts). GS is checked by group requirements."),
      updAll),
    h("div", { class: "char-edit-grid" }, cards, addCard));
}

// ================= Access (owner only) =================

// ================= Voice now (bot owner only) =================

/** Who is in which voice channel right now — every channel the bot knows, hidden ones too. Refreshes itself. */
// ================= Owner tools: Mascot (only the bot owner) =================

async function pageMascot(main) {
  const g = currentGuild() || {};
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🤖 Mascot")),
    h("p", { class: "muted small" }, "Only you (the bot owner) see this page. The little Greatness robot flies across the site for everyone on this server, runs away from the cursor, gets tired and can be caught. When it escapes it sometimes casts a spell (4%, never legendary); when it's caught it strikes back much more often (60%; 1.5% — a legendary cast of two spells at once). The poop barrage is 5× rarer than the other spells. After 10 catches in a row a GOLDEN robot may come (10%, doubling with each further catch): it always casts, legendary 6%. A RAINBOW robot comes with +1% for every catch in a row (+2% more per catch once you're in the golden zone, 10+ in a row): legendary ×10 (60% if it's golden too). Members collect what they saw in Member → Achievements."));
  // How often
  const secs = h("input", { type: "number", min: "0", max: "604800", step: "1", value: String(g.mascotSeconds ?? 900), style: "width:140px", "aria-label": "Seconds" });
  const human = h("span", { class: "muted small" });
  const say = () => { const v = Number(secs.value) || 0; human.textContent = !v ? "off" : v < 120 ? `≈ every ${v} s` : v < 7200 ? `≈ every ${Math.round(v / 60)} min` : `≈ every ${(v / 3600).toFixed(1)} h`; };
  secs.addEventListener("input", say); say();
  const save = h("button", { class: "btn" }, "Save");
  const tracker = track("Mascot", () => secs.value, async () => {
    const v = Math.max(0, Math.round(Number(secs.value) || 0));
    const ok = await act(save, () => gApi("PUT", "/mascot", { seconds: v }), "Saved.") !== undefined;
    if (ok) { g.mascotSeconds = v; window.__neonCatReschedule && window.__neonCatReschedule(); }
    return ok;
  });
  save.addEventListener("click", tracker.save);
  main.append(tracker.attach(h("div", { class: "card" }, h("h2", {}, "⏱️ How often"),
    h("p", { class: "muted small" }, "While it flies, buttons right around it (55 px — the same zone where it reacts to the cursor) don't react. People with 'reduce motion' turned on never see it."),
    h("div", { class: "row", style: "align-items:flex-end" }, h("div", { class: "field", style: "margin:0" }, fieldLabel("Every … seconds on average (0 = off)"), h("div", { class: "row" }, secs, human)), save))));
  // Summon
  const preview = h("button", { class: "btn secondary" }, icon("sparkles", 15), "Let it fly (normal)");
  preview.addEventListener("click", () => window.__neonCat && window.__neonCat());
  main.append(h("div", { class: "card" }, h("h2", {}, "✨ Summon now"),
    h("p", { class: "muted small" }, "Only on your screen. With a spell button the robot surely casts that spell — when it escapes or when you catch it. Works even when the mascot is off."),
    h("div", { class: "row", style: "margin-bottom:10px" }, preview),
    summonButtons(), h("div", { class: "muted small", style: "margin-top:8px" }, SPELLS.map(s => `${s[1]} ${s[2]}`).join(" · ") + " · 🌟 Legendary · 🎲 Random")));
}

// ================= Member: Achievements (the mascot game) =================

// Achievements: a click opens the place/top-3 popup at once (hovering 3 s does it too); click again or elsewhere to close
document.addEventListener("click", e => {
  const t = e.target.closest && e.target.closest(".ach.done");
  document.querySelectorAll(".ach.done.open").forEach(x => { if (x !== t) x.classList.remove("open"); });
  if (t) t.classList.toggle("open");
});

/** Graded achievements: rarity colour by grade, progress to the next one; locked ones are just "?". */
function achievementsCard(d) {
  const grades = d.grades || ["Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Mythic"];
  const tile = (a, small) => a.unlocked
    ? h("div", { class: `ach done g${a.grade} cls${a.tierClass || 1}${small ? " sm" : ""}`, tabindex: "0" },
      a.tierClass >= 3 ? h("i", { class: "ach-shine" }) : null,
      h("span", { class: "ach-icon" }, h("span", { class: "ico" }, a.icon)),
      h("div", { class: "ach-text" },
        h("div", { class: "ach-top" }, h("b", { title: a.title }, a.title),
          h("span", { class: "ach-cls", title: ["", "Tier I achievement (common) — ×1 grade points", "Tier II achievement (rare) — ×2 grade points", "Tier III achievement (legendary) — ×10 grade points", "Tier IV achievement (ultra rare) — ×50 grade points"][a.tierClass || 1] }, "✦".repeat(a.tierClass || 1)),
          h("span", { class: "ach-grade" }, a.gradeName)),
        // what was done for the current grade, then how far the next one is
        small ? null : h("span", { class: "muted small" }, a.description),
        h("span", { class: "ach-next" }, a.next ? `${a.value} / ${a.next} → ${a.nextGrade || "next grade"}` : `${a.value} · max grade!`),
        h("div", { class: "ach-bar", title: a.next ? `${a.value} / ${a.next}` : `${a.value}` },
          h("i", { style: `width:${a.next ? Math.min(100, Math.round((a.value - a.prev) / (a.next - a.prev) * 100)) : 100}%` })),
        h("div", { class: "ach-foot" }, h("span", { class: "ach-pips" }, Array.from({ length: a.maxGrade }, (_, i) => h("i", { class: i < a.grade ? `on g${i + 1}` : "" }))),
          a.points != null ? h("span", { class: "ach-pts", title: `${a.grade} grade${a.grade === 1 ? "" : "s"} × ${[0, 1, 2, 10, 50][a.tierClass || 1]} (Tier ${["", "I", "II", "III", "IV"][a.tierClass || 1]})` }, `${a.points} pts`) : null)),
      a.top ? h("div", { class: "rank-pop" }, h("div", { class: "rank-pop-title" }, `Your place: #${a.rank} of ${a.of}`),
        a.top.map((t, i) => h("div", { class: `rank-row${t.me ? " me" : ""}` }, h("span", { class: "rank-pos" }, ["🥇", "🥈", "🥉"][i]),
          t.member.avatarUrl ? h("img", { class: "avatar", src: t.member.avatarUrl, alt: "" }) : h("span", { class: "avatar" }),
          h("span", { class: "rank-name" }, t.member.name), h("b", {}, String(t.value))))) : null)
    : a.visible ? h("div", { class: `ach todo${small ? " sm" : ""}` }, h("span", { class: "ach-icon" }, h("span", { class: "ico" }, a.icon)),
      h("div", { class: "ach-text" }, h("div", { class: "ach-top" }, h("b", {}, a.title), h("span", { class: "ach-grade" }, "Not yet")),
        h("span", { class: "muted small" }, a.description),
        h("span", { class: "ach-next" }, `${a.value} / ${a.next}`),
        h("div", { class: "ach-bar" }, h("i", { style: `width:${Math.min(100, Math.round(a.value / a.next * 100))}%` })),
        h("span", { class: "ach-pips" }, Array.from({ length: a.maxGrade }, () => h("i")))))
    : h("div", { class: `ach locked${small ? " sm" : ""}` }, h("span", { class: "ach-icon" }, "?"),
      h("div", { class: "ach-text" }, h("b", {}, "???"), small ? null : h("span", { class: "muted small" }, "Hidden achievement")));
  // Filters: click grade chips and/or tier chips (several at once); nothing selected — everything
  const selG = new Set(), selT = new Set();
  const pass = a => (!selG.size || (a.unlocked && selG.has(a.grade))) && (!selT.size || selT.has(a.tierClass || 0));
  const group = (key, title, small) => {
    const all = d.achievements.filter(a => (a.group || "general") === key);
    const list = all.filter(pass);
    if (!list.length) return null;
    const open = list.filter(a => a.unlocked).length, rankOf = a => a.unlocked ? 2 : a.visible ? 1 : 0;
    const sorted = [...list].sort((x, y) => rankOf(y) - rankOf(x));
    const grid = h("div", { class: `ach-grid${small ? " sm" : ""}` });
    const hidden = list.length - open;
    const draw = all => grid.replaceChildren(...(all || !small ? sorted : sorted.filter(a => a.unlocked)).map(a => tile(a, small)),
      ...(!all && small && hidden ? [h("button", { type: "button", class: "ach locked sm ach-more", onclick: () => draw(true) },
        h("span", { class: "ach-icon" }, "?"), h("div", { class: "ach-text" }, h("b", {}, `+${hidden} hidden`), h("span", { class: "muted small" }, "show")))] : []));
    draw(false);   // the big combination list starts folded: found ones + "+N hidden"
    // each block folds (remembered per browser)
    const fold = `gb.achFold.${key}`, folded = store.get(fold) === "1";
    const box = h("div", { class: `ach-group${folded ? " folded" : ""}` });
    const head = h("button", { type: "button", class: "ach-group-head", "aria-expanded": folded ? "false" : "true", onclick: () => {
      const f = box.classList.toggle("folded"); store.set(fold, f ? "1" : ""); head.setAttribute("aria-expanded", f ? "false" : "true");
    } }, h("span", { class: "chev" }, "▾"), h("h3", {}, `${title} · ${all.filter(a => a.unlocked).length}/${all.length}`));
    box.append(head, grid);
    return box;
  };
  const body = h("div", {});
  const draw = () => {
    const groups = [group("general", "General"), group("guild", "Guild"), group("spells", "Spells"), group("legend", "Legendary casts"), group("combos", "Legendary combinations", true)].filter(Boolean);
    body.replaceChildren(...(groups.length ? groups : [h("div", { class: "muted", style: "margin-top:12px" }, "No achievements match the filter.")]));
  };
  const chip = (cls, label, set, v) => {
    const b = h("button", { type: "button", class: cls, title: "Filter", onclick: () => { set.has(v) ? set.delete(v) : set.add(v); b.classList.toggle("on", set.has(v)); legend.classList.toggle("filtering", selG.size + selT.size > 0); draw(); } }, label);
    return b;
  };
  const reset = h("button", { type: "button", class: "ach-reset", onclick: () => { selG.clear(); selT.clear(); legend.querySelectorAll(".on").forEach(x => x.classList.remove("on")); legend.classList.remove("filtering"); draw(); } }, "✕ All");
  const legend = h("div", { class: "ach-legend" }, grades.map((n, i) => chip(`ach-chip g${i + 1}`, n, selG, i + 1)),
    h("span", { class: "ach-legend-sep" }), chip("ach-cls-chip c1", "✦ Tier I ×1", selT, 1), chip("ach-cls-chip c2", "✦✦ Tier II ×2", selT, 2), chip("ach-cls-chip c3", "✦✦✦ Tier III ×10", selT, 3), chip("ach-cls-chip c4", "✦✦✦✦ Tier IV ×50", selT, 4), reset);
  draw();
  const m = d.multipliers || [1, 2, 10, 50];
  return h("div", { class: "card" }, h("h2", {}, "🏆 Achievements"),
    h("div", { class: "muted small ach-formula" }, `Grade points = grade (1–7) × tier multiplier: ✦ Tier I ×${m[0]} · ✦✦ Tier II ×${m[1]} · ✦✦✦ Tier III ×${m[2]} · ✦✦✦✦ Tier IV ×${m[3]}. You have ${d.points ?? 0} of ${d.maxPoints ?? 0}.`),
    legend, body);
}

/** Ranking: my place per category among everyone on the server; hover a tile for the top 3. */
function rankingCard(rows) {
  const medal = r => r === 1 ? "🥇" : r === 2 ? "🥈" : r === 3 ? "🥉" : null;
  return h("div", { class: "card" }, h("h2", {}, "📊 Ranking"),
    h("p", { class: "muted small" }, "Your place among everyone on the server who has played. Hover a tile to see the top 3."),
    h("div", { class: "rank-grid" }, rows.map(r => h("div", { class: `rank-tile${r.rank && r.rank <= 3 ? ` top${r.rank}` : ""}`, tabindex: "0" },
      h("div", { class: "rank-head" }, h("span", { class: "rank-icon" }, r.icon), h("span", { class: "rank-label" }, r.label)),
      h("div", { class: "rank-place" }, r.rank ? [medal(r.rank) ? h("span", { class: "rank-medal" }, medal(r.rank)) : null, h("b", {}, `#${r.rank}`), h("span", { class: "muted small" }, ` of ${r.of}`)]
        : h("span", { class: "muted small" }, r.of ? `not ranked yet · ${r.of} players` : "nobody yet")),
      h("div", { class: "muted small" }, `you: ${r.value}`),
      h("div", { class: "rank-pop" }, h("div", { class: "rank-pop-title" }, `Top 3 · ${r.label}`),
        r.top.length ? r.top.map((t, i) => h("div", { class: `rank-row${t.me ? " me" : ""}` },
          h("span", { class: "rank-pos" }, ["🥇", "🥈", "🥉"][i]),
          t.member.avatarUrl ? h("img", { class: "avatar", src: t.member.avatarUrl, alt: "" }) : h("span", { class: "avatar" }),
          h("span", { class: "rank-name" }, t.member.name), h("b", {}, String(t.value))))
          : h("div", { class: "muted small" }, "Nobody yet — be the first!"))))));
}

async function pageAchievements(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🏆 Achievements"),
    h("button", { class: "btn btn-refresh", title: "Reload", onclick: hardRefresh }, "🔄 Refresh")),
    h("p", { class: "muted small" }, "Now and then a little robot flies across the site. Catch it, or watch it escape — either way it may cast a spell. Hidden achievements show what to do only once you've earned them."));
  const body = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  main.append(body);
  let d;
  try { d = await gApi("GET", "/me/achievements"); } catch (e) { body.replaceChildren(errorState(e.message)); return; }
  const got = d.achievements.filter(a => a.unlocked).length;
  const seen = d.spells.filter(s => s.count > 0).length;
  body.replaceChildren(
    h("div", { class: "stats" },
      statCard("bot", "Robots caught", d.caught, d.quick ? `${d.quick} before it got tired` : "catch it when it flies by"),
      statCard("sparkles", "Spells seen", d.spells.reduce((a, s) => a + s.count, 0), `${seen} of ${d.spells.length} different`),
      statCard("star", "Legendary casts", d.legendary, "two spells at once"),
      statCard("trophy", "Achievements", `${got}/${d.achievements.length}`, `${d.points ?? 0} of ${d.maxPoints ?? 0} grade points`)),
    rankingCard(d.ranking || []),
    h("div", { class: "card" }, h("h2", {}, "📖 Spellbook"),
      h("div", { class: "spellbook" }, d.spells.map(s => h("div", { class: `spell-tile${s.count ? "" : " locked"}`, title: s.count ? `${s.name}: seen ${s.count}×` : "Not seen yet" },
        h("span", { class: "spell-icon" }, s.count ? s.icon : "?"), h("span", { class: "spell-name" }, s.count ? s.name : "???"),
        s.count ? h("span", { class: "spell-count" }, `×${s.count}`) : null)))),
    achievementsCard(d));
}

async function pageVoiceNow(main) {
  const body = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  const stamp = h("span", { class: "muted small" });
  const auto = h("input", { type: "checkbox", class: "switch", checked: store.get("gb.voiceNowAuto") !== "0", "aria-label": "Auto-refresh" });
  auto.addEventListener("change", () => store.set("gb.voiceNowAuto", auto.checked ? "1" : "0"));
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎧 Voice now"),
    h("div", { class: "row", style: "gap:8px" }, stamp, h("label", { class: "row", style: "gap:6px" }, auto, "every 10 s"),
      h("button", { class: "btn btn-refresh", title: "Reload now", onclick: () => load() }, "🔄 Refresh"))),
    h("p", { class: "muted small" }, "Only you (the bot owner) see this page. All voice channels of the server, including the ones hidden from members — as the bot sees them."),
    body);
  const flags = u => [
    u.serverMuted ? h("span", { class: "vn-flag warn", title: "Muted by the server" }, icon("mic-off", 13)) : u.muted ? h("span", { class: "vn-flag", title: "Muted" }, icon("mic-off", 13)) : null,
    u.deafened ? h("span", { class: "vn-flag", title: "Deafened" }, icon("headphone-off", 13)) : null,
    u.streaming ? h("span", { class: "vn-flag live", title: "Streaming" }, icon("monitor", 13), "LIVE") : null,
    u.video ? h("span", { class: "vn-flag", title: "Camera on" }, icon("video", 13)) : null,
    u.bot ? h("span", { class: "chip" }, "bot") : null
  ].filter(Boolean);
  const load = async () => {
    if (state.tab !== "voicenow" || !document.body.contains(body)) return false;
    let d;
    try { d = await gApi("GET", "/voice-now"); } catch (e) { body.replaceChildren(errorState(e.message)); return true; }
    stamp.textContent = `${d.total} in voice · ${new Date().toLocaleTimeString()}`;
    if (!d.channels.length) { body.replaceChildren(emptyState("headphones", "Nobody in voice", "The page updates by itself.")); return true; }
    const byCat = new Map();
    for (const c of d.channels) { const k = c.category || "No category"; if (!byCat.has(k)) byCat.set(k, []); byCat.get(k).push(c); }
    body.replaceChildren(...[...byCat].map(([cat, list]) => h("div", { style: "margin-bottom:14px" },
      sectionLabel(cat),
      h("div", { class: "grid2" }, list.map(c => h("div", { class: "card vn-card", style: "margin:0" },
        h("div", { class: "row", style: "gap:6px;margin-bottom:8px" },
          icon(c.stage ? "mic" : "volume-2", 16), h("b", { class: "grow" }, c.name),
          c.hidden ? h("span", { class: "badge lock", title: "Hidden from @everyone" }, icon("eye-off", 12), "hidden") : null,
          c.tempRoom ? h("span", { class: "chip" }, "temp room") : null,
          h("span", { class: "chip" }, c.limit ? `${c.users.length}/${c.limit}` : String(c.users.length)),
          h("a", { class: "btn ghost icon-only sm", href: c.jumpUrl, target: "_blank", rel: "noopener", title: "Open in Discord" }, icon("external-link", 14))),
        h("div", { class: "list" }, c.users.map(u => h("div", { class: "list-row", style: "padding:4px 0" },
          h("div", { class: "grow" }, member(u.member)), ...flags(u))))))))));
    return true;
  };
  await load();
  const tick = async () => { if (await load() !== false) setTimeout(() => { if (auto.checked) tick(); else setTimeout(tick, 10000); }, 10000); };
  setTimeout(tick, 10000);
}

// ================= Birthdays =================

/** Member birthdays: on someone's day the bot gives a role (if they have the member role) and removes it next day. */
/** Окно выбора участника для дня рождения: только с ролью «Участник» (из настроек), поиск, фильтр «без даты». */
function pickBirthdayMember() {
  return new Promise(resolve => {
    const d = dialog();
    const list = h("div", { class: "pick-list", style: "max-height:55vh;overflow:auto" });
    const info = h("p", { class: "muted small" });
    const only = toggle(true, "Only members without a birthday yet");
    let timer = 0, seq = 0;
    const search = async () => {
      const my = ++seq;
      try {
        const r = await gApi("GET", `/birthdays/candidates?q=${encodeURIComponent(input.value)}&withoutDate=${only.cb.checked}`);
        if (my !== seq) return;
        info.textContent = `${r.role ? `Members with @${r.role}` : "All members (no member role set in the settings)"}: ${r.total} · with a date: ${r.withDate} · without: ${r.total - r.withDate}`;
        list.replaceChildren(...(r.members.length ? r.members.map(x =>
          h("button", { onclick: () => { d.close(); resolve(x); } }, member(x.member),
            x.birthday ? h("span", { class: "chip", style: "margin-left:auto" }, x.birthday) : h("span", { class: "muted small", style: "margin-left:auto" }, "no date"))) :
          [empty(only.cb.checked ? "Everyone found already has a birthday." : "Nobody found.")]));
      } catch (e) { list.replaceChildren(errorState(e.message)); }
    };
    const input = h("input", { placeholder: "Search by name…", style: "width:100%", oninput: () => { clearTimeout(timer); timer = setTimeout(search, 250); } });
    only.cb.addEventListener("change", search);
    d.replaceChildren(h("h2", {}, "Choose a member"), input, h("div", { class: "row", style: "margin:8px 0" }, only.el), info, list,
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => { d.close(); resolve(null); } }, "Cancel")));
    d.onclose = () => resolve(null);
    d.showModal();
    input.focus();
    search();
  });
}

async function pageBirthdays(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🎂 Birthdays"),
    h("button", { class: "btn btn-refresh", title: "Reload now", onclick: hardRefresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted small" },
    "Members set their birthday with /birthday in Discord (you can also add dates here). On someone's birthday — in the server time zone — the bot gives them the chosen role if they have the member role, and removes it the next day."));

  const setCard = h("div", { class: "card" }, skeletonLines(5));
  const addCard = h("div", { class: "card" });
  const ioCard = h("div", { class: "card" });
  const listCard = h("div", { class: "card" }, skeletonLines(4));
  const panelCard = h("div", { class: "card" });
  const greetCard = h("div", { class: "card" });
  // Разделы страницы вместо шести карточек подряд
  const sec = sectionTabs([
    ["people", "users", "People", h("div", {}, addCard, ioCard, listCard)],
    ["greeting", "megaphone", "Greeting post", greetCard],
    ["panel", "cake", "Birthday panel", panelCard],
    ["settings", "settings", "Settings", setCard]
  ], "gb.birthdaysSection");
  main.append(sec.nav, ...sec.panels);

  // Bulk import (one CSV/JSON file) and export of the current list
  const base = `/api/guilds/${state.guildId}/birthdays`;
  const csvBtn = h("a", { class: "btn secondary", href: `${base}/export`, download: "" }, icon("download", 14), "Export CSV");
  const impFile = h("input", { type: "file", accept: ".csv,.json,.txt,text/csv,text/plain,application/json" });
  const impBtn = h("button", { class: "btn secondary" }, icon("upload", 14), "Import file");
  impBtn.addEventListener("click", () => act(impBtn, async () => {
    const f = impFile.files && impFile.files[0];
    if (!f) return toast("Choose a CSV or JSON file first.", "warn");
    const isJson = /\.json$/i.test(f.name);
    const res = await fetch(`${base}/import`, { method: "POST", headers: { "X-Panel": "1", "Content-Type": isJson ? "application/json" : "text/csv" }, body: f, credentials: "same-origin" });
    const d = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(d.error || `Import failed (${res.status})`);
    impFile.value = "";
    showImport(d);
  }));
  // Вставить список текстом: «discord-ник / игровой ник - ДД.ММ» по строке
  const paste = h("textarea", { rows: "6", style: "width:100%", placeholder: "sionpol69 / Sivantar - 12.05\nsashali._. / FearlessFairy - 06.09\n… one person per line: Discord username / nickname - DD.MM (year optional)" });
  const pasteBtn = h("button", { class: "btn" }, icon("upload", 14), "Import list");
  pasteBtn.addEventListener("click", () => act(pasteBtn, async () => {
    if (!paste.value.trim()) return toast("Paste the list first.", "warn");
    const res = await fetch(`${base}/import`, { method: "POST", headers: { "X-Panel": "1", "Content-Type": "text/plain" }, body: paste.value, credentials: "same-origin" });
    const d = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(d.error || `Import failed (${res.status})`);
    showImport(d);
  }));
  const result = h("div", {});
  function showImport(d) {
    toast(`Birthdays: ${d.added} added, ${d.updated} updated` + (d.full ? `, ${d.full} skipped (list full)` : "") + ` of ${d.total}.`);
    const nf = d.notFound || [];
    result.replaceChildren(nf.length ? h("div", { class: "card", style: "margin-top:10px;border-color:var(--warn,#c90)" },
      h("b", {}, `Not found on the server (${nf.length}) — check the Discord username or add them by ID:`),
      h("pre", { class: "small", style: "white-space:pre-wrap;margin:6px 0 0" }, nf.join("\n"))) : null);
    if (nf.length) paste.value = nf.filter(l => !/ — bad date$/.test(l)).join("\n");
    load();
  }
  ioCard.replaceChildren(
    sectionLabel("Bulk import / export"),
    h("p", { class: "muted small" }, "Paste a list — one person per line “Discord username / nickname - DD.MM” (the bot finds them on the server by username, then by nickname; lines without a date are skipped). ",
      "Or upload a file: CSV columns user_id, day, month, year (year optional) — or user_id, date (DD.MM.YYYY); JSON: a list of {userId, day, month, year}; a .txt with the same lines as above works too."),
    paste, h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center;margin-top:6px" }, pasteBtn),
    result,
    h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center;margin-top:12px" }, csvBtn, impFile, impBtn));

  const fmt = p => `${pad2(p.day)}.${pad2(p.month)}${p.year ? "." + p.year : ""}`;

  function renderSettings(d) {
    const enabled = toggle(d.enabled, "Give the birthday role automatically");
    const memberRole = roleSelect("Anyone with a saved date");
    memberRole.value = d.memberRoleId || "";
    const bdayRole = roleSelect("Choose the birthday role…");
    bdayRole.value = d.birthdayRoleId || "";
    const channel = channelSelect("text", "Don't post a greeting");
    channel.value = d.announceChannelId || "";
    const save = h("button", { class: "btn primary" }, icon("check", 14), "Save settings");
    save.addEventListener("click", () => act(save, async () => {
      const r = await gApi("PUT", "/birthdays/settings", {
        enabled: enabled.cb.checked, memberRoleId: memberRole.value || null,
        birthdayRoleId: bdayRole.value || null, announceChannelId: channel.value || null
      });
      toast(`Settings saved.${r.granted || r.removed ? ` Applied now: +${r.granted} / −${r.removed}.` : ""}`);
      load();
    }));
    setCard.replaceChildren(
      sectionLabel("Settings"),
      enabled.el,
      h("div", { class: "field" }, h("label", {}, "Member role (\"Участник\") — only its holders get the birthday role"), memberRole),
      h("div", { class: "field" }, h("label", {}, "Birthday role — given on the day, removed next day"), bdayRole),
      h("div", { class: "field" }, h("label", {}, "Short line when the role is given (optional)"), channel,
        h("span", { class: "muted small" }, "A one-line “Happy birthday, @name!” per person at the moment the role is given. For a prepared post at a set time with pictures and templates use Greeting post below.")),
      h("div", { class: "row", style: "margin-top:6px" }, save),
      h("p", { class: "muted small" }, "The bot's own role must be ABOVE the birthday role in the server's role list, or it can't assign it."));
  }

  function renderAdd() {
    const uid = h("input", { class: "input", placeholder: "Discord user ID", style: "width:180px" });
    const day = h("input", { type: "number", min: "1", max: "31", placeholder: "DD", style: "width:70px" });
    const month = h("input", { type: "number", min: "1", max: "12", placeholder: "MM", style: "width:70px" });
    const year = h("input", { type: "number", min: "1900", max: "2100", placeholder: "YYYY (optional)", style: "width:150px" });
    const add = h("button", { class: "btn" }, icon("user-plus", 14), "Add / update");
    add.addEventListener("click", () => act(add, async () => {
      if (!uid.value.trim()) return toast("Enter a user ID.", "warn");
      if (!day.value || !month.value) return toast("Enter day and month.", "warn");
      await gApi("POST", "/birthdays/people", { userId: uid.value.trim(), day: +day.value, month: +month.value, year: year.value ? +year.value : null });
      uid.value = day.value = month.value = year.value = "";
      toast("Birthday saved."); load();
    }));
    // Выбор участника сервера (только с ролью «Участник» из настроек) — вместо ввода ID
    const chosen = h("span", { class: "muted small" });
    const pick = h("button", { class: "btn secondary", type: "button" }, icon("users", 14), "Choose member…");
    pick.addEventListener("click", async () => {
      const r = await pickBirthdayMember();
      if (!r) return;
      uid.value = r.member.id;
      chosen.replaceChildren(member(r.member), r.birthday ? h("span", { class: "chip", style: "margin-left:6px" }, `now ${r.birthday}`) : null);
      if (r.birthday) { const [dd, mm, yy] = r.birthday.split("."); day.value = +dd; month.value = +mm; year.value = yy || ""; }
      day.focus();
    });
    uid.addEventListener("input", () => chosen.replaceChildren());
    add.addEventListener("click", () => setTimeout(() => { if (!uid.value) chosen.replaceChildren(); }, 800));
    addCard.replaceChildren(
      sectionLabel("Add a birthday"),
      h("p", { class: "muted small" }, "Choose a member of the server (only people with the member role from the settings), or paste a Discord user ID."),
      h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center" }, pick, chosen),
      h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center;margin-top:8px" }, uid, day, h("span", { class: "muted" }, "."), month, year, add));
  }

  const removePerson = (id, name, el) => removeWithUndo(el, `${name}'s birthday removed.`,
    () => gApi("DELETE", `/birthdays/people/${id}`), load);

  // Поздравительный пост в заданное время: упоминает КАЖДОГО именинника, шаблоны по очереди/случайно
  function renderGreeting(gr) {
    const enabled = toggle(!!gr.enabled, "Post a greeting every day there are birthdays");
    const ch = channelSelect("text", "Channel…");
    ch.value = gr.channelId || "";
    const time = h("input", { type: "time", value: gr.time || "10:00" });
    const mode = h("select", {},
      h("option", { value: "shuffle" }, "Random, no repeats until all are used"),
      h("option", { value: "random" }, "Random every time"),
      h("option", { value: "sequence" }, "In turn (1 → 2 → 3 → 1 …)"));
    mode.value = gr.mode || "shuffle";
    const templates = (gr.templates || []).map(t => ({ ...t }));
    const sampleUsers = (gr.today && gr.today.length ? gr.today : [{ id: "1", name: "Sataella" }, { id: "2", name: "NimoN" }]);
    const mentionsMap = Object.fromEntries(sampleUsers.map(u => [`<@${u.id}>`, "@" + u.name]));
    const render = text => {
      const join = a => a.length <= 1 ? a.join("") : a.slice(0, -1).join(", ") + " & " + a[a.length - 1];
      const users = join(sampleUsers.map(u => `<@${u.id}>`)), names = join(sampleUsers.map(u => u.name));
      const out = (text || "").replace(/\{users\}/gi, users).replace(/\{names\}/gi, names).replace(/\{count\}/gi, String(sampleUsers.length))
        .replace(/\{server\}/gi, (currentGuild() || {}).name || "Server");
      return /\{users\}/i.test(text || "") ? out : `${users}\n${out}`;
    };
    const list = h("div", {});
    const draw = () => list.replaceChildren(...(templates.length ? templates.map((t, i) => {
      const name = h("input", { maxlength: "60", placeholder: `Template ${i + 1}`, value: t.name || "", style: "flex:1" });
      name.addEventListener("input", () => { t.name = name.value; });
      const on = toggle(t.enabled !== false, "on");
      on.cb.addEventListener("change", () => { t.enabled = on.cb.checked; });
      const ta = h("textarea", { rows: "4", maxlength: "20000", style: "width:100%", placeholder: "🎉 Happy birthday, {users}! The whole guild wishes you… (add a picture or GIF with 🖼 Image)" });
      ta.value = t.text || "";
      const prev = h("div", { class: "dc-frame compact" });
      const upd = () => prev.replaceChildren(dcMessage({ content: resolveEmojiShortcodes(render(ta.value), emojiMapOf([])), mentions: mentionsMap }));
      ta.addEventListener("input", () => { t.text = ta.value; upd(); });
      const test = h("button", { class: "btn ghost sm", type: "button", title: "Send this template to your DM with you as the birthday person (save first)" }, icon("send", 14), "Test in DM");
      test.addEventListener("click", () => act(test, async () => {
        if (!t.id) return toast("Save first — then test.", "warn");
        await gApi("POST", "/birthdays/greeting/test", { templateId: t.id }); toast("Sent to your DM.");
      }));
      const del = h("button", { class: "btn ghost sm", type: "button", title: "Remove template" }, icon("trash-2", 14));
      del.addEventListener("click", () => { templates.splice(i, 1); draw(); });
      upd();
      return h("div", { class: "card", style: "margin:8px 0;padding:12px" },
        h("div", { class: "row", style: "gap:8px;align-items:center;margin-bottom:6px" }, h("b", {}, `#${i + 1}`), name, on.el, test, del),
        h("div", { class: "editor-layout" }, h("div", {}, ta, h("div", { class: "row", style: "gap:8px;margin-top:6px" }, mediaTextButton(ta))), h("div", { class: "editor-preview" }, prev)));
    }) : [h("p", { class: "muted small" }, "No templates yet — add one.")]));
    const add = h("button", { class: "btn secondary", type: "button" }, icon("plus", 14), "Add template");
    add.addEventListener("click", () => { templates.push({ name: "", text: "🎉 Happy birthday, {users}! 🎂", enabled: true }); draw(); });
    const save = h("button", { class: "btn" }, icon("check", 14), "Save greeting");
    save.addEventListener("click", () => act(save, async () => {
      await gApi("PUT", "/birthdays/greeting", { enabled: enabled.cb.checked, channelId: ch.value || null, time: time.value, mode: mode.value,
        templates: templates.map(t => ({ id: t.id || null, name: t.name || "", text: t.text || "", enabled: t.enabled !== false })) });
      toast("Greeting saved."); load();
    }));
    const now = h("button", { class: "btn ghost", title: "Post the greeting for today's birthday people right now (once more if it was already posted)" }, icon("megaphone", 14), "Post now");
    now.addEventListener("click", () => act(now, async () => {
      if (!await confirmDialog("Post the greeting now?", `For today's birthday people (${(gr.today || []).length}) in the chosen channel. Save your changes first.`, "Post", false)) return;
      await gApi("POST", "/birthdays/greeting/now", {}); toast("Greeting posted."); load();
    }));
    draw();
    greetCard.replaceChildren(
      sectionLabel("Greeting post"),
      h("p", { class: "muted small" },
        "Every day at the set time (server time zone) the bot posts a greeting if someone has a birthday — each birthday person is mentioned personally (not the role). ",
        "In the text: {users} — mentions of everyone (if missing, they go on the first line), {names} — their names, {count}, {server}, :emoji:, and pictures / GIFs with 🖼 Image."),
      enabled.el,
      h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center;margin:8px 0" },
        h("span", { class: "muted" }, "Channel"), ch, h("span", { class: "muted" }, "at"), time, h("span", { class: "muted" }, "· template:"), mode),
      (gr.today && gr.today.length) ? h("p", { class: "small" }, "🎂 Today: ", ...gr.today.flatMap((m, i) => [i ? ", " : "", member(m)])) : h("p", { class: "muted small" }, "Nobody has a birthday today."),
      gr.lastDate ? h("p", { class: "muted small" }, `Last greeting: ${gr.lastDate}`) : null,
      list,
      h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;margin-top:8px" }, add, h("span", { class: "spacer" }), now, save));
  }

  // Панель «Укажи свой ДР»: сообщение с кнопкой 🎂 — окно ввода даты, дата сразу попадает в список
  function renderPanel(p) {
    const title = h("input", { maxlength: "256", placeholder: p.defaultTitle || "", value: p.title || "", style: "width:100%" });
    const text = h("textarea", { maxlength: "3000", rows: "4", placeholder: p.defaultText || "", style: "width:100%" });
    text.value = p.text || "";
    const btnText = h("input", { maxlength: "80", placeholder: p.defaultButton || "Set my birthday", value: p.button || "", style: "width:100%" });
    const preview = h("div", { class: "dc-frame compact" });
    const update = () => preview.replaceChildren(dcMessage({
      embed: { title: `${title.value || p.defaultTitle} | Bot Website`, url: "#", description: text.value || p.defaultText, color: color.value() || "#f47fff" },
      rows: [[dcButton(btnText.value || p.defaultButton || "Set my birthday", "🎂", "Blurple")]]
    }));
    const color = embedColorField(p.color || null, update);
    title.addEventListener("input", update); text.addEventListener("input", update); btnText.addEventListener("input", update);
    const ch = channelSelect("text", "Channel…");
    if (p.channelId) ch.value = p.channelId;
    const body = () => ({ title: title.value, text: text.value, color: color.value(), button: btnText.value });
    const save = h("button", { class: p.jumpUrl ? "btn" : "btn secondary" }, icon("check", 14), "Save");
    save.addEventListener("click", () => act(save, async () => {
      const r = await gApi("PUT", "/birthdays/panel", body());
      toast(r.updated ? "Saved — the posted panel is updated." : "Saved.");
    }));
    const post = h("button", { class: p.jumpUrl ? "btn ghost" : "btn" }, icon("pin", 14), p.jumpUrl ? "Re-post" : "Post");
    post.addEventListener("click", () => act(post, async () => {
      if (!ch.value) return toast("Choose a channel.", "warn");
      if (p.jumpUrl && !await confirmDialog("Re-post the birthday panel?", "The old message is deleted and the panel is posted in the chosen channel. To change the text, just Save.", "Re-post", false)) return;
      await gApi("PUT", "/birthdays/panel", body());
      await gApi("POST", "/birthdays/panel/post", { channelId: ch.value });
      toast("Birthday panel posted."); load();
    }));
    const remove = p.jumpUrl ? h("button", { class: "btn ghost", title: "Delete the posted message" }, icon("trash-2", 14), "Remove") : null;
    if (remove) remove.addEventListener("click", () => act(remove, async () => {
      if (!await confirmDialog("Remove the birthday panel?", "The posted message is deleted. Saved birthdays stay.", "Remove")) return;
      await gApi("DELETE", "/birthdays/panel"); toast("Panel removed."); load();
    }));
    update();
    panelCard.replaceChildren(
      h("div", { class: "row", style: "align-items:center;gap:8px" }, sectionLabel("Birthday panel"),
        p.jumpUrl ? h("a", { class: "chip ok", href: p.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 12), `#${p.channel || "posted"}`)
          : h("span", { class: "chip warn" }, "not posted")),
      h("p", { class: "muted small" }, "A message with a 🎂 button: members press it, enter their birthday (DD.MM, year optional) and it's saved to the list below right away. Empty fields use the default text."),
      h("div", { class: "editor-layout" },
        h("div", {},
          h("div", { class: "field" }, h("label", {}, "Title"), title),
          h("div", { class: "field" }, h("label", {}, "Text"), text),
          h("div", { class: "grid2" }, h("div", { class: "field" }, h("label", {}, "Button text"), btnText),
            h("div", { class: "field" }, h("label", {}, "Color"), color.el)),
          h("p", { class: "muted small" }, "The title links to the bot website (“ | Bot Website” is added, like on the other panels). The window and replies after pressing the button are in Panels & posts → Texts by module → Birthdays.")),
        h("div", { class: "editor-preview" }, preview)),
      h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;align-items:center;margin-top:8px" }, ch, post, save, remove));
  }

  function renderList(d) {
    if (!d.people.length) { listCard.replaceChildren(emptyState("cake", "No birthdays yet", "Members add theirs with /birthday, or add them above.")); return; }
    listCard.replaceChildren(
      sectionLabel(`Birthdays · ${d.count}`),
      h("div", { class: "list" }, d.people.map(p => h("div", { class: "list-row", style: "gap:8px;align-items:center" },
        h("div", { class: "grow" }, member(p.member)),
        h("span", { class: "chip" }, fmt(p)),
        p.inDays === 0 ? h("span", { class: "chip ok", title: "Birthday today" }, "🎂 today")
          : h("span", { class: "muted small" }, p.inDays === 1 ? "tomorrow" : `in ${p.inDays} d`),
        h("button", { class: "btn sm ghost icon-only", title: "Remove", onclick: e => removePerson(p.member.id, p.member.name, e.currentTarget.closest("tr")) }, icon("trash-2", 14))))));
  }

  async function load() {
    let d;
    try { d = await gApi("GET", "/birthdays"); } catch (e) { setCard.replaceChildren(errorState(e.message)); return; }
    renderSettings(d); renderGreeting(d.greeting || {}); renderAdd(); renderPanel(d.panel || {}); renderList(d);
  }
  await load();
}

// ================= Channel pages =================

const MODULE_PANEL_LABELS = { lfg: "LFG panel", tickets: "Tickets panel", rooms: "Temp Voice panel" };

/** Pages grouped by bot project: [[name, pages]], general ones first. */
function pageGroups(d) {
  const groups = d.groups || [];
  const out = [];
  const general = d.pages.filter(p => !p.projectKey || !groups.some(g => g.key === p.projectKey));
  if (general.length) out.push([groups.length ? "General" : "Pages", general]);
  for (const g of groups) {
    const list = d.pages.filter(p => p.projectKey === g.key);
    if (list.length) out.push([g.name, list]);
  }
  return out;
}

/** Shows what an update of a published page will do; resolves true to go on. */
const REPOST_NOTE = " Members keep their roles; someone who has the role and clicks the reaction again gets it removed."

async function confirmPagePlan(p, channelId) {
  const plan = await gApi("GET", `/pages/${p.id}/plan?channelId=${encodeURIComponent(channelId)}`);
  const label = id => (plan.blocks.find(b => b.id === id) || {}).label || id;
  const panelsReposted = plan.repost.filter(id => (plan.blocks.find(b => b.id === id) || {}).type === "rolepanel");
  if (plan.full) return confirmDialog("Publish to another channel?",
    "The page is posted in full in the chosen channel and removed from the old one. Reactions on role panels start from zero." + REPOST_NOTE, "Publish", false);
  if (!plan.edit.length && !plan.repost.length && !plan.removed) { toast("Nothing changed — the page is up to date."); return false; }
  return new Promise(resolve => {
    const d = dialog();
    const line = (ic, title, ids, cls) => ids.length ? h("div", { style: "margin-bottom:10px" },
      h("div", { class: `row ${cls || ""}`, style: "gap:6px;font-weight:600" }, icon(ic, 15), `${title} (${ids.length})`),
      h("ul", { class: "small", style: "margin:4px 0 0 22px" }, ids.map(id => h("li", {}, label(id))))) : null;
    let ok = false;
    const go = h("button", { class: "btn" }, "Update");
    go.addEventListener("click", () => { ok = true; d.close(); });
    d.replaceChildren(...[h("h2", {}, `Update “${p.name}”`),
      line("check", "Not touched", plan.keep, "muted"),
      line("pencil", "Edited in place", plan.edit),
      line("refresh-cw", "Re-posted (order changed or new)", plan.repost, panelsReposted.length ? "error-text" : ""),
      plan.removed ? h("p", { class: "small" }, `🗑️ ${plan.removed} message(s) of removed blocks are deleted.`) : null,
      panelsReposted.length ? h("p", { class: "error-text small" },
        "Role panels in the re-posted part lose their reactions." + REPOST_NOTE + " To avoid it, add new blocks at the end instead of in the middle.") : null,
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), go)].filter(Boolean));
    d.onclose = () => resolve(ok);
    d.showModal();
  });
}

/** Build a channel from several bot messages + existing panels, published in order as one unit. */
async function pageChannelPages(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🧱 Channel pages"),
    h("button", { class: "btn btn-refresh", title: "Reload", onclick: hardRefresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted small" },
    "Assemble a whole channel from several bot messages and panels — text blocks (with formatting and :emoji:), role panels and module panels — in one order. ",
    "Publishing posts them into one channel as one unit; Update later touches only changed blocks. Create the role panels in the Role panels tab, then drop them in here by reference."));

  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  main.append(holder);

  async function load() {
    let d;
    try { d = await gApi("GET", "/pages"); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }
    // Вкладки по проектам: All · General · <проект>… — выбранная запоминается; новая страница создаётся в ней
    const groups = d.groups || [];
    const isGeneral = p => !p.projectKey || !groups.some(g => g.key === p.projectKey);
    const tabs = [["all", "All", d.pages.length], ["", "General", d.pages.filter(isGeneral).length],
      ...groups.map(g => [g.key, g.name, d.pages.filter(p => p.projectKey === g.key).length])];
    let tab = store.get("gb.pagesTab") ?? "all";
    if (!tabs.some(t => t[0] === tab)) tab = "all";
    const shown = tab === "all" ? d.pages : tab === "" ? d.pages.filter(isGeneral) : d.pages.filter(p => p.projectKey === tab);
    const tabBar = h("div", { class: "tabs page-tabs" }, tabs.map(([k, label, n]) =>
      h("button", { class: `btn secondary sm${k === tab ? " active-tab" : ""}`, onclick: () => { store.set("gb.pagesTab", k); load(); } },
        label, h("span", { class: "tab-count" }, String(n)))));

    const add = h("button", { class: "btn" }, icon("plus", 15), "New page");
    add.addEventListener("click", async () => {
      const name = await promptDialog("New channel page", "Channel page");
      if (!name) return;
      const p = await act(add, () => gApi("POST", "/pages", { name, projectKey: tab === "all" ? "" : tab }));
      if (p) channelPageEditor(p.id, d.rolePanels, d.moduleKeys, load, d.groups);
    });
    const view = { ...d, pages: shown };
    holder.replaceChildren(h("div", { class: "card" },
      h("div", { class: "card-head" }, h("h2", {}, "Pages"), add),
      groups.length ? tabBar : null,
      !d.pages.length ? emptyState("layers", "No channel pages yet", "Create one to assemble a channel from several messages and panels.")
      : !shown.length ? h("p", { class: "muted small" }, "No pages in this project yet — “New page” creates one here.")
      : h("div", {}, pageGroups(view).map(([gname, list]) => h("div", { style: "margin-bottom:10px" }, tab === "all" ? sectionLabel(gname) : null, h("div", { class: "list" }, list.map(p => {
        const chSel = channelSelect("text", "Channel…");
        if (p.channelId) chSel.value = p.channelId;
        // Главное действие — Update / Publish; опасные (Re-post all, Unpublish, Delete) спрятаны в «⋯»
        const publish = h("button", { class: "btn sm", title: p.published ? "Update the posted page: only changed blocks are touched, reactions stay" : "Post the page" },
          icon(p.published ? "refresh-cw" : "pin", 14), p.published ? "Update" : "Publish");
        publish.addEventListener("click", () => act(publish, async () => {
          if (!chSel.value) return toast("Choose a channel.", "warn");
          if (p.published && !await confirmPagePlan(p, chSel.value)) return;
          await gApi("POST", `/pages/${p.id}/publish`, { channelId: chSel.value });
          toast(p.published ? "Page updated." : "Page published."); load();
        }));
        const edit = h("button", { class: "btn secondary sm", onclick: () => channelPageEditor(p.id, d.rolePanels, d.moduleKeys, load, d.groups) }, icon("pencil", 14), "Edit");
        const more = menuButton([
          p.published ? { icon: "repeat", label: "Re-post all (reactions reset)", danger: true, onclick: () => act(null, async () => {
            if (!chSel.value) return toast("Choose a channel.", "warn");
            if (!await confirmTyped("Re-post the whole page?",
              "Every message of the page is deleted and posted again. All reactions on its role panels disappear and the bot can't put them back." + REPOST_NOTE +
              " Usually Update is enough — it also re-posts messages someone deleted by hand.", p.name, "Re-post all")) return;
            await gApi("POST", `/pages/${p.id}/publish`, { channelId: chSel.value, full: true });
            toast("Page re-posted."); load();
          }) } : null,
          p.published ? { icon: "eye-off", label: "Unpublish (delete the messages)", danger: true, onclick: () => act(null, async () => {
            if (!await confirmTyped("Unpublish this page?", "All its messages in the channel are deleted, reactions on its role panels too. The page itself stays — you can publish it again.", p.name, "Unpublish")) return;
            await gApi("POST", `/pages/${p.id}/unpublish`); toast("Unpublished."); load();
          }) } : null,
          { icon: "trash-2", label: "Delete page", danger: true, onclick: () => act(null, async () => {
            const ok = p.published
              ? await confirmTyped(`Delete “${p.name}”?`, "The page AND all its posted messages are deleted, reactions on its role panels too. This can't be undone.", p.name, "Delete")
              : await confirmDialog(`Delete “${p.name}”?`, "The page is deleted (it isn't posted anywhere).", "Delete");
            if (!ok) return;
            await gApi("DELETE", `/pages/${p.id}`); toast("Page deleted."); load();
          }) }
        ].filter(Boolean));
        return h("div", { class: "list-row", style: "gap:8px;align-items:center;flex-wrap:wrap" },
          h("div", { class: "grow", style: "min-width:160px" }, h("b", {}, p.name),
            p.published ? h("span", { class: "chip ok", style: "margin-left:8px" }, icon("check", 12), `#${p.channel || "posted"}`)
              : h("span", { class: "chip warn", style: "margin-left:8px" }, "not published"),
            h("span", { class: "muted small", style: "margin-left:8px" }, `${p.blockCount} block${p.blockCount === 1 ? "" : "s"}`)),
          chSel, publish, edit, more);
      })))))));
  }
  await load();
}

function channelPageEditor(pageId, rolePanels, moduleKeys, reload, groups = []) {
  const d = dialog();
  d.classList.add("xwide", "page-dialog");
  d.onclose = () => d.classList.remove("xwide", "page-dialog");
  d.replaceChildren(h("div", { class: "muted" }, "Loading…"));
  d.showModal();

  const emojiMap = new Map();
  let emojiList = [];
  const emojisReady = gApi("GET", "/pages/emojis").then(list => { emojiList = list; for (const e of list) emojiMap.set(e.name.toLowerCase(), e.code); }).catch(() => {});

  Promise.all([gApi("GET", `/pages/${pageId}`), emojisReady]).then(([data]) => {
    const page = data.page;
    rolePanels = data.rolePanels || rolePanels;
    const name = h("input", { maxlength: "80", value: page.name, style: "width:100%" });
    const proj = h("select", {}, h("option", { value: "" }, "General (no project)"), groups.map(g => h("option", { value: g.key }, g.name)));
    proj.value = page.projectKey || "";
    const blocks = (page.blocks || []).map(b => ({ id: b.id, posted: b.messages > 0, type: b.type, text: b.text || "", rolePanelId: b.rolePanelId || "", moduleKey: b.moduleKey || "" }));
    const list = h("div", {});

    const panelTitle = id => (rolePanels.find(r => r.id === id) || {}).title;

    const draw = () => {
      list.replaceChildren(...blocks.map((b, i) => {
        const head = h("div", { class: "row binding-head", style: "gap:6px" },
          h("span", { class: "binding-num" }, `#${i + 1}`),
          h("span", { class: "chip" }, b.type === "text" ? "📝 Text" : b.type === "rolepanel" ? "🎭 Role panel" : "🧩 Module panel"),
          b.posted ? h("span", { class: "chip ok", title: "Posted — on Update it is kept or edited in place" }, "posted") : h("span", { class: "chip warn", title: "Not posted yet" }, "new"),
          h("span", { class: "spacer" }),
          h("button", { class: "btn ghost sm", type: "button", title: "Up", onclick: () => { if (i > 0) { [blocks[i - 1], blocks[i]] = [blocks[i], blocks[i - 1]]; draw(); } } }, icon("chevron-up", 14)),
          h("button", { class: "btn ghost sm", type: "button", title: "Down", onclick: () => { if (i < blocks.length - 1) { [blocks[i + 1], blocks[i]] = [blocks[i], blocks[i + 1]]; draw(); } } }, icon("chevron-down", 14)),
          h("button", { class: "btn ghost sm", type: "button", title: "Remove", onclick: () => { blocks.splice(i, 1); draw(); } }, icon("trash-2", 14)));
        let body;
        if (b.type === "text") {
          const ta = h("textarea", { rows: "4", maxlength: "20000", placeholder: "Text — formatting, :emoji: and mentions like <@&id> work", value: b.text, style: "width:100%" });
          const prev = h("div", { class: "dc-frame compact" });
          const redraw = () => prev.replaceChildren(ta.value.trim()
            ? dcMessage({ content: resolveEmojiShortcodes(ta.value, emojiMap).slice(0, 2000) })
            : h("div", { class: "muted small" }, "Empty text block."));
          ta.addEventListener("input", () => { b.text = ta.value; redraw(); queuePreview(); });
          // Вставить эмодзи сервера/бота как :name: в позицию курсора (картинки в списке)
          const pick = emojiList.length ? h("select", { style: "width:200px", title: "Insert a server / bot emoji" }, h("option", { value: "" }, "😀 Insert emoji…"),
            emojiList.map(e => h("option", { value: e.name, "data-icon": e.code }, `:${e.name}:${e.app ? " (bot)" : ""}`))) : null;
          if (pick) pick.addEventListener("change", () => {
            if (!pick.value) return;
            const ins = `:${pick.value}:`, st = ta.selectionStart ?? ta.value.length, en = ta.selectionEnd ?? ta.value.length;
            ta.value = ta.value.slice(0, st) + ins + ta.value.slice(en);
            b.text = ta.value; ta.focus(); ta.selectionStart = ta.selectionEnd = st + ins.length;
            pick.value = ""; redraw(); queuePreview();
          });
          redraw();
          body = h("div", {}, ta, h("div", { class: "row", style: "margin:6px 0;gap:8px;flex-wrap:wrap" }, pick, mediaTextButton(ta),
            h("span", { class: "muted small" }, "Images: 🖼 Image, or paste / drop a picture into the text.")));
        } else if (b.type === "rolepanel") {
          const sel = h("select", {}, h("option", { value: "" }, "Choose a role panel…"),
            rolePanels.map(r => h("option", { value: r.id }, r.title)));
          sel.value = b.rolePanelId;
          sel.addEventListener("change", () => { b.rolePanelId = sel.value; queuePreview(); });
          body = h("div", { class: "field" },
            rolePanels.length ? sel : h("p", { class: "muted small" }, "No role panels yet — create one in the Role panels tab first."));
        } else {
          const sel = h("select", {}, h("option", { value: "" }, "Choose a module panel…"),
            moduleKeys.map(k => h("option", { value: k }, MODULE_PANEL_LABELS[k] || k)));
          sel.value = b.moduleKey;
          sel.addEventListener("change", () => { b.moduleKey = sel.value; queuePreview(); });
          body = h("div", { class: "field" }, sel);
        }
        return h("div", { class: "binding-card" }, head, body);
      }));
      queuePreview();
    };

    // Итоговый вид всей страницы в канале — справа, обновляется при любых правках
    const whole = h("div", { class: "dc-frame page-preview" }, h("div", { class: "muted small" }, "Loading preview…"));
    let pvTimer = 0, pvSeq = 0;
    function queuePreview() {
      clearTimeout(pvTimer);
      pvTimer = setTimeout(async () => {
        const my = ++pvSeq;
        try {
          const r = await gApi("POST", "/pages/preview", { blocks: blocks.map(b => ({ id: b.id || null, type: b.type, text: b.text, rolePanelId: b.rolePanelId, moduleKey: b.moduleKey })) });
          if (my !== pvSeq) return;
          whole.replaceChildren(...(r.items.length ? r.items.map(it => it.error
            ? h("p", { class: "error-text small" }, it.error)
            : dcFromPreview(it.preview, { reactions: it.reactions || [] }))
            : [h("div", { class: "muted small" }, "Nothing to post yet — add blocks.")]));
        } catch (e) { if (my === pvSeq) whole.replaceChildren(h("p", { class: "error-text small" }, `Preview: ${e.message}`)); }
      }, 400);
    }
    draw();

    const addBtn = (label, type) => h("button", { class: "btn secondary sm", type: "button",
      onclick: () => { blocks.push({ type, text: "", rolePanelId: "", moduleKey: "" }); draw(); itemsWrap.scrollTop = itemsWrap.scrollHeight; } }, label);

    const itemsWrap = h("div", { style: "max-height:56vh;overflow:auto" }, list);
    const save = h("button", { class: "btn" }, icon("check", 15), "Save");
    const saveBody = () => ({ name: name.value, projectKey: proj.value, blocks: blocks.map(b => ({ id: b.id || null, type: b.type, text: b.text, rolePanelId: b.rolePanelId, moduleKey: b.moduleKey })) });
    save.addEventListener("click", async () => {
      if (await act(save, () => gApi("PUT", `/pages/${pageId}`, saveBody()), "Page saved.") !== undefined) { d.close(); reload(); }
    });

    d.replaceChildren(
      h("h2", {}, "Channel page"),
      h("div", { class: "grid2" }, h("div", { class: "field" }, h("label", {}, "Name"), name),
        h("div", { class: "field" }, h("label", {}, "Project (groups the pages)"), proj)),
      h("div", { class: "page-edit-split" },
        h("div", { class: "page-edit-left" },
          h("div", { class: "field" }, h("label", {}, "Blocks (posted top to bottom)"), itemsWrap),
          h("div", { class: "row", style: "gap:8px;flex-wrap:wrap;margin-bottom:6px" },
            addBtn("➕ Text", "text"), addBtn("➕ Role panel", "rolepanel"), addBtn("➕ Module panel", "modulepanel")),
          h("p", { class: "muted small" }, "Update keeps unchanged blocks as they are (role panel reactions stay) and edits changed text in place. Discord keeps messages in post order, so moving blocks or inserting one in the middle re-posts the blocks from that point on — add new blocks at the end.")),
        h("div", { class: "page-edit-right" }, h("label", {}, "How the channel will look"), whole)),
      h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Cancel"), save));
  }).catch(e => d.replaceChildren(h("h2", {}, "Channel page"), errorState(e.message),
    h("div", { class: "actions" }, h("button", { class: "btn secondary", onclick: () => d.close() }, "Close"))));
}

// ================= Media library =================

/** Картинки/гифки для постов: лежат вложениями в служебном канале Discord; в текстах и полях — метка [img:имя]. */
const MEDIA_PERMS = ["media", "announce", "pages", "roles", "welcome", "posts", "birthdays"];
const MEDIA_TOKEN = /\[img:([A-Za-z0-9_\-]{1,40})\]/;
const mediaLib = { items: [], map: new Map(), channelId: null, channel: null, maxMb: 10, loaded: null };

function loadMedia(fresh = false) {
  if (!fresh && mediaLib.loaded) return mediaLib.loaded;
  mediaLib.loaded = gApi("GET", "/media" + (fresh ? "?fresh=true" : "")).then(d => {
    mediaLib.items = d.items || [];
    mediaLib.channelId = d.channelId; mediaLib.channel = d.channel; mediaLib.maxMb = d.maxMb || 10;
    mediaLib.map = new Map(mediaLib.items.map(i => [i.name.toLowerCase(), i.url]));
    return mediaLib;
  }).catch(e => { mediaLib.loaded = null; throw e; });
  return mediaLib.loaded;
}

/** Ссылка для превью: [img:имя] → картинка медиатеки, обычная ссылка — как есть. */
function mediaUrl(v) {
  if (!v) return null;
  const m = MEDIA_TOKEN.exec(String(v));
  return m ? mediaLib.map.get(m[1].toLowerCase()) || null : v;
}

async function uploadMediaFile(file, name = "") {
  if (file.size > mediaLib.maxMb * 1024 * 1024) throw new Error(`The file is larger than ${mediaLib.maxMb} MB.`);
  const res = await fetch(`/api/guilds/${state.guildId}/media/upload`, {
    method: "POST", credentials: "same-origin", body: file,
    headers: { "X-Panel": "1", "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(file.name || "image.png"), "X-Media-Name": encodeURIComponent(name) }
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Upload failed (${res.status})`);
  await loadMedia(true);
  return data;
}

const imageFiles = list => [...(list || [])].filter(f => /^image\/(png|jpe?g|gif|webp)$/i.test(f.type));

/** Окно медиатеки: выбрать картинку, загрузить файл (кнопка, перетаскивание, Ctrl+V) или добавить по ссылке. → item | null */
function pickMedia(title = "Choose an image") {
  return new Promise(resolve => {
    const d = dialog();
    d.classList.add("wide");
    let done = false;
    const finish = item => { if (done) return; done = true; d.close(); resolve(item); };
    d.onclose = () => { d.classList.remove("wide"); document.removeEventListener("paste", onPaste); if (!done) { done = true; resolve(null); } };
    const grid = h("div", { class: "media-grid" });
    const status = h("p", { class: "muted small" });
    const upload = async files => {
      for (const f of imageFiles(files)) {
        status.textContent = `Uploading ${f.name || "image"}…`;
        try { const item = await uploadMediaFile(f); status.textContent = ""; if (item && item.name) return finish(item); }
        catch (e) { status.textContent = ""; toast(e.message, "error"); }
      }
    };
    const onPaste = e => { const files = imageFiles(e.clipboardData && e.clipboardData.files); if (files.length) { e.preventDefault(); upload(files); } };
    document.addEventListener("paste", onPaste);

    const fileInput = h("input", { type: "file", accept: "image/png,image/jpeg,image/gif,image/webp", style: "display:none" });
    fileInput.addEventListener("change", () => upload(fileInput.files));
    const drop = h("div", { class: "media-drop" }, icon("upload", 22),
      h("div", {}, h("b", {}, "Drop an image here"), " · paste it with Ctrl+V · or ",
        h("a", { href: "#", onclick: e => { e.preventDefault(); fileInput.click(); } }, "choose a file")),
      h("div", { class: "muted small" }, `PNG, JPG, GIF, WebP up to ${mediaLib.maxMb} MB`), fileInput);
    drop.addEventListener("dragover", e => { e.preventDefault(); drop.classList.add("over"); });
    drop.addEventListener("dragleave", () => drop.classList.remove("over"));
    drop.addEventListener("drop", e => { e.preventDefault(); drop.classList.remove("over"); upload(e.dataTransfer.files); });

    const url = h("input", { placeholder: "…or paste a link: image, Tenor or Giphy", style: "flex:1" });
    const addUrl = h("button", { class: "btn secondary", type: "button" }, icon("link", 15), "Add");
    addUrl.addEventListener("click", async () => {
      if (!url.value.trim()) return;
      const item = await act(addUrl, () => gApi("POST", "/media/url", { url: url.value.trim() }));
      if (item && item.name) { await loadMedia(true); finish(item); }
    });

    const draw = () => grid.replaceChildren(...(mediaLib.items.length ? mediaLib.items.map(i =>
      h("button", { class: "media-tile", type: "button", title: `${i.name} — click to use`, onclick: () => finish(i) },
        h("img", { src: i.url, alt: "", loading: "lazy" }), h("span", {}, i.name, i.gif ? h("em", {}, " GIF") : null)))
      : [h("p", { class: "muted small" }, "The library is empty — upload the first image above.")]));

    const body = () => mediaLib.channelId
      ? [drop, h("div", { class: "row", style: "gap:8px;margin:8px 0" }, url, addUrl), status, grid]
      : [mediaSetup(() => d.replaceChildren(...head(), ...body(), foot()))];
    const head = () => [h("h2", {}, title)];
    const foot = () => h("div", { class: "actions" }, h("button", { class: "btn secondary", type: "button", onclick: () => finish(null) }, "Cancel"));
    d.replaceChildren(...head(), h("div", { class: "muted" }, "Loading…"));
    d.showModal();
    loadMedia().then(() => { d.replaceChildren(...head(), ...body(), foot()); draw(); })
      .catch(e => d.replaceChildren(...head(), errorState(e.message), foot()));
  });
}

/** Нет служебного канала — выбрать или создать. */
function mediaSetup(onReady) {
  const sel = channelSelect("text", "Choose a channel…");
  const use = h("button", { class: "btn secondary", type: "button" }, "Use this channel");
  const create = h("button", { class: "btn", type: "button" }, icon("plus", 15), "Create a private #bot-media");
  const done = async r => { if (r !== undefined) { await loadMedia(true); onReady(); } };
  use.addEventListener("click", async () => { if (!sel.value) return toast("Choose a channel first.", "warn"); done(await act(use, () => gApi("PUT", "/media/channel", { channelId: sel.value }), "Media channel set.")); });
  create.addEventListener("click", async () => done(await act(create, () => gApi("PUT", "/media/channel", { create: true }), "Channel created.")));
  return h("div", { class: "card" },
    h("p", {}, "Images live as attachments in a private Discord channel — nothing is stored on the bot's server. Pick that channel once:"),
    h("div", { class: "row", style: "gap:8px;flex-wrap:wrap" }, create, h("span", { class: "muted" }, "or"), sel, use),
    h("p", { class: "muted small" }, "Members shouldn't see it. Anything an admin uploads there by hand appears in the library too."));
}

/** Вставить текст в позицию курсора поля. */
function insertAtCursor(field, text) {
  const st = field.selectionStart ?? field.value.length, en = field.selectionEnd ?? field.value.length;
  field.value = field.value.slice(0, st) + text + field.value.slice(en);
  field.focus(); field.selectionStart = field.selectionEnd = st + text.length;
  field.dispatchEvent(new Event("input", { bubbles: true }));
}

/** Для текстовых полей постов: кнопка «🖼 Image» + вставка картинки из буфера и перетаскивание прямо в поле. */
function mediaTextButton(field) {
  const token = item => `${field.value && !/\n$/.test(field.value.slice(0, field.selectionStart ?? field.value.length)) ? "\n" : ""}[img:${item.name}]\n`;
  const handle = async files => {
    for (const f of imageFiles(files)) {
      try { await loadMedia(); if (!mediaLib.channelId) { const it = await pickMedia(); if (it) insertAtCursor(field, token(it)); return; }
        toast(`Uploading ${f.name || "image"}…`); const it = await uploadMediaFile(f); insertAtCursor(field, token(it)); }
      catch (e) { toast(e.message, "error"); }
    }
  };
  field.addEventListener("paste", e => { const files = imageFiles(e.clipboardData && e.clipboardData.files); if (files.length) { e.preventDefault(); handle(files); } });
  field.addEventListener("dragover", e => { if ([...e.dataTransfer.items].some(i => i.kind === "file")) e.preventDefault(); });
  field.addEventListener("drop", e => { const files = imageFiles(e.dataTransfer.files); if (files.length) { e.preventDefault(); handle(files); } });
  return h("button", { class: "btn ghost sm", type: "button", title: "Insert an image or GIF from the media library (or paste / drop one into the text)",
    onclick: async () => { const it = await pickMedia("Insert an image"); if (it) insertAtCursor(field, token(it)); } }, icon("image", 15), "Image");
}

/** Для полей «картинка/миниатюра» (ссылка): кнопка выбора из медиатеки; поле получает [img:имя]. */
function mediaFieldButton(input) {
  const set = v => { input.value = v; input.dispatchEvent(new Event("input", { bubbles: true })); };
  input.addEventListener("paste", async e => {
    const files = imageFiles(e.clipboardData && e.clipboardData.files);
    if (!files.length) return;
    e.preventDefault();
    try { await loadMedia(); if (!mediaLib.channelId) { const it = await pickMedia(); if (it) set(`[img:${it.name}]`); return; }
      toast("Uploading…"); set(`[img:${(await uploadMediaFile(files[0])).name}]`); } catch (err) { toast(err.message, "error"); }
  });
  return h("button", { class: "btn ghost sm", type: "button", title: "Choose from the media library or upload",
    onclick: async () => { const it = await pickMedia(); if (it) set(`[img:${it.name}]`); } }, icon("image", 15));
}

/** Поле картинки + кнопка медиатеки в одну строку. */
const withMediaButton = input => h("div", { class: "row", style: "gap:6px;flex-wrap:nowrap" }, input, mediaFieldButton(input));

async function pageMedia(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🖼️ Media library"),
    h("button", { class: "btn btn-refresh", title: "Reload now", onclick: () => { loadMedia(true).then(() => renderShell()); } }, "🔄 Refresh")));
  main.append(h("p", { class: "muted small" },
    "Images and GIFs for the bot's posts. They are kept as attachments in a private Discord channel — the bot's server stores nothing. ",
    "In texts an image is a [img:name] line (the 🖼 Image button inserts it, or paste / drop a picture right into the text); in panels choose it with 🖼 next to the image field. ",
    "Each post gets its own copy of the file, so removing an image here doesn't break old posts."));
  try { await loadMedia(); } catch (e) { main.append(errorState(e.message)); return; }
  if (!mediaLib.channelId) { main.append(mediaSetup(() => renderShell())); return; }

  const fileInput = h("input", { type: "file", multiple: true, accept: "image/png,image/jpeg,image/gif,image/webp", style: "display:none" });
  const grid = h("div", { class: "media-grid big" });
  const uploadMany = async files => {
    for (const f of imageFiles(files)) {
      toast(`Uploading ${f.name || "image"}…`);
      try { await uploadMediaFile(f); } catch (e) { toast(e.message, "error"); }
    }
    draw();
  };
  fileInput.addEventListener("change", () => uploadMany(fileInput.files));
  const drop = h("div", { class: "media-drop" }, icon("upload", 22),
    h("div", {}, h("b", {}, "Drop images here"), " · paste with Ctrl+V · or ", h("a", { href: "#", onclick: e => { e.preventDefault(); fileInput.click(); } }, "choose files")),
    h("div", { class: "muted small" }, `PNG, JPG, GIF, WebP up to ${mediaLib.maxMb} MB · stored in #${mediaLib.channel || "?"}`), fileInput);
  drop.addEventListener("dragover", e => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", e => { e.preventDefault(); drop.classList.remove("over"); uploadMany(e.dataTransfer.files); });
  const onPaste = e => { if (state.tab !== "media") return document.removeEventListener("paste", onPaste);
    const files = imageFiles(e.clipboardData && e.clipboardData.files); if (files.length && !(e.target instanceof HTMLInputElement)) { e.preventDefault(); uploadMany(files); } };
  document.addEventListener("paste", onPaste);

  const url = h("input", { placeholder: "Link: image, Tenor or Giphy", style: "flex:1" });
  const name = h("input", { placeholder: "name (optional)", style: "width:180px" });
  const addUrl = h("button", { class: "btn secondary" }, icon("link", 15), "Add by link");
  addUrl.addEventListener("click", async () => {
    if (!url.value.trim()) return;
    if (await act(addUrl, () => gApi("POST", "/media/url", { url: url.value.trim(), name: name.value.trim() }), "Added.") !== undefined) {
      url.value = ""; name.value = ""; await loadMedia(true); draw();
    }
  });

  const draw = () => grid.replaceChildren(...(mediaLib.items.length ? mediaLib.items.map(i => {
    const copy = h("button", { class: "btn ghost sm", title: "Copy the text code" }, icon("copy", 14));
    copy.addEventListener("click", () => navigator.clipboard.writeText(i.token).then(() => toast(`${i.token} copied.`)));
    const ren = h("button", { class: "btn ghost sm", title: i.byBot ? "Rename" : "Uploaded by hand in Discord — named by its file" }, icon("pencil", 14));
    ren.disabled = !i.byBot;
    ren.addEventListener("click", async () => {
      const v = await promptDialog("Rename image (letters, digits, - and _)", i.name);
      if (v && await act(ren, () => gApi("PUT", `/media/${i.id}`, { name: v }), "Renamed.") !== undefined) { await loadMedia(true); draw(); }
    });
    const del = h("button", { class: "btn ghost sm danger", title: "Delete from the library" }, icon("trash-2", 14));
    del.addEventListener("click", async () => {
      if (!await confirmDialog(`Delete “${i.name}”?`, "Posts already sent keep their image. Texts and panels that still use it will be posted without it.", "Delete")) return;
      if (await act(del, () => gApi("DELETE", `/media/${i.id}`), "Deleted.") !== undefined) { await loadMedia(true); draw(); }
    });
    return h("div", { class: "media-card" },
      h("a", { href: i.url, target: "_blank", rel: "noopener" }, h("img", { src: i.url, alt: "", loading: "lazy" })),
      h("div", { class: "media-meta" }, h("b", {}, i.name), i.gif ? h("span", { class: "chip" }, "GIF") : null,
        h("span", { class: "muted small" }, `${i.width && i.height ? `${i.width}×${i.height} · ` : ""}${(i.size / 1024 / 1024).toFixed(1)} MB`)),
      h("div", { class: "row", style: "gap:4px;flex-wrap:nowrap" }, h("code", { class: "small", title: i.token }, i.token), h("span", { class: "spacer" }), copy, ren, del));
  }) : [emptyState ? emptyState("image", "No images yet", "Upload the first one above.") : empty("No images yet.")]));
  draw();

  const change = h("button", { class: "btn ghost sm" }, "Change channel");
  change.addEventListener("click", async () => {
    if (await act(change, () => gApi("PUT", "/media/channel", { channelId: null })) !== undefined) { await loadMedia(true); renderShell(); }
  });
  main.append(h("div", { class: "card" }, drop, h("div", { class: "row", style: "gap:8px;margin-top:10px;flex-wrap:wrap" }, url, name, addUrl)),
    sectionLabel(`Images · ${mediaLib.items.length}`), grid,
    h("div", { class: "row", style: "margin-top:12px" }, h("span", { class: "muted small" }, `Storage channel: #${mediaLib.channel}`), change));
}

async function pageAccess(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🔑 Access"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  main.append(h("p", { class: "muted" },
    "Who can log in to this panel and which sections they see. Only you (the bot owner) can open this page. ",
    "Changes apply immediately — no re-login needed."));

  const holder = h("div", {}, h("div", { class: "muted" }, "Loading…"));
  main.append(holder);

  let data;
  try { data = await api("GET", "/api/access"); } catch (e) { holder.replaceChildren(errorState(e.message)); return; }

  const permChecks = (selected) => {
    const boxes = data.permissions.map(p => {
      const cb = h("input", { type: "checkbox", checked: selected.includes(p) });
      return { p, cb, el: h("label", { class: `chip perm-chip ${toneOf(PERM_ICONS[p] || "key-round")}`, style: "cursor:pointer" }, cb, icon(PERM_ICONS[p] || "key-round", 13), PERM_LABELS[p] || p) };
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
    const tracker = track(`Access · ${g.user.name}`, () => perms.value(), async () => {
      const value = perms.value();
      if (!value.length) { toast("No sections ticked — use Remove to take away access.", "warn"); return false; }
      return await act(save, () => api("PUT", `/api/access/${g.user.id}`, { permissions: value }), "Saved.") !== undefined;
    });
    save.addEventListener("click", tracker.save);
    const rm = menuButton([{ icon: "trash-2", label: "Remove access", danger: true, onclick: async () => {
      if (!await confirmDialog(`Remove access for ${g.user.name}?`, "They won't be able to use the panel anymore.", "Remove")) return;
      if (await act(null, () => api("DELETE", `/api/access/${g.user.id}`), "Access removed.") !== undefined) refresh();
    } }]);
    return tracker.attach(h("tr", {},
      h("td", {}, member(g.user), h("div", { class: "muted small" }, g.user.id)),
      h("td", {}, perms.el),
      h("td", { class: "muted small" }, new Date(g.grantedAt).toLocaleDateString()),
      h("td", { class: "actions" }, save, " ", rm)));
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
      "**🗂️ Sent**: **Open**, **✏️ Edit** (changes the messages in Discord), **↩️ Reuse**, **Delete messages** (in Discord), **Remove** (only from this list).",
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
        "**➕ Create a group** — project, activity, slots, date/time, 🔊 voice room, 📝 note (optional) → **Publish**. It is posted in the project's LFG channel; you are the organizer and take slot 1.",
        "In your own groups: ⬇️ move a member to the reserve, ⬆️ move someone from the reserve into the group (they get a DM), ✖️ remove from the reserve, 🕒 **Time**, 📝 **Note**, **Close group**."
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
      "**Members** and **Open tickets** show the change ▲▼ since yesterday (24h) and over 7 days, plus a 30-day mini chart. The bot saves a snapshot once a day (start of the day, server time zone); the numbers appear from the next day.",
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
        "📝 **Note** (optional) — the organizer's text shown in the group post, e.g. what to bring; set it when creating or later.",
        "**Attendance** after the start → **statistics** and player reliability.",
        "Admin tab **LFG**: a tab per project + ⚙️ General — activities, panel, voice room category, duplicate posts, classes, templates, **recurring groups**, any group's roster, force-close, statistics.",
        "Member tab **Groups**: everyone with the member role signs up, creates groups and manages their own groups."
      ]]
    ],
    discord: [
      ["Create a group", [
        "Click **Create LFG** on the LFG panel (or use `/lfg`). A menu only you can see opens.",
        "Choose the size → the activity (or «Custom activity», no ping) → select slots and set their roles → **Next**.",
        "Pick the date and time (or «No time»), turn 🔊 **Voice room** on if needed, add a 📝 **Note** if you want → **Publish**. You take slot 1 with your last used character.",
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
        "⚙️ **Manage** — move members between the group and the reserve (moved-in members get a DM), mark **attendance** after the start, 🕒 **Change time**, 📝 **Note**.",
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
        "**Open tickets** — **Approve** (gives the roles, DM, closes in 15 s), **Deny** (reason by DM, closes in 15 s), Open, **Force close** (deletes the channels without an outcome).",
        "**🗄️ Archive** — filter by outcome, search by name, user ID, project, reason or answers; each row shows the user ID (click to copy); **Form** shows the answers."
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
  welcome: {
    title: "Welcome — new members",
    features: [["What it can do", [
      "A **message in a channel** and/or a **DM** to every new member: plain text, a **card** (embed: title, text, color, the member's avatar, a big image, a footer) or both.",
      "Placeholders: `{user}` mention, `{name}`, `{username}`, `{server}`, `{members}`, `{tickets}` link to the ticket panel — click a chip under a field to insert one.",
      "Buttons under the welcome: 📝 **Apply** (opens the ticket panel) and up to 4 link buttons (rules, website…).",
      "**Auto role** (off by default): roles given when a member joins, optionally after a delay; bots can be skipped."
    ]]],
    discord: [["In Discord", [
      "Nothing to set up there. For the auto role the bot needs **Manage Roles** and its role must be above the roles it gives."
    ]]],
    panel: [["Using it", [
      "Fill in the blocks on the left — the preview on the right shows the channel message or the DM (switch on top).",
      "**Send me a test** sends what is in the form to your DMs, even before saving. **Save** applies it to the next members.",
      "For a role after accepting the rules use a Role panel in the “Give only” mode."
    ]]]
  },
  roles: {
    title: "Role panels — members take roles themselves",
    features: [["What it can do", [
      "A panel is a message with a card and items. Every item is a 🔘 **button**, a 😀 **reaction** or a 📋 **list item** and gives **one or more roles** — mix types freely on one panel.",
      "Buttons: label, emoji, color (grey, blurple, green, red). List items: label, emoji, description. Reactions: emoji.",
      "Modes: **Get / remove**, **Only one** (a new pick replaces the old one), **Give only** (e.g. accept the rules), **Remove only**.",
      "Optional: only members with certain roles can use the panel; a max number of list items picked at once.",
      "The card: title, text, color, big image, thumbnail, footer, an automatic list “emoji — roles — description” and a hint about the mode."
    ]]],
    discord: [["In Discord", [
      "Members click a button, pick in the list or add a reaction — the bot answers only to them. Removing a reaction removes the roles (in Get / remove mode).",
      "The bot's role must be **above** the roles it gives (Server Settings → Roles) with **Manage Roles**; for reactions in “Only one” mode also **Manage Messages**."
    ]]],
    panel: [["Using it", [
      "**New role panel** → 1. Message, 2. items (+ Button / + Reaction / + List item, then pick the roles each one gives), 3. how it works → **Create**. The preview updates as you type.",
      "On a panel card: **Post / Repost** in a channel, **Edit** (a posted panel updates at once), **Copy**, **Delete** (removes the message too).",
      "Texts of the bot's answers and the hints: Panels & posts → 📝 Texts by module → Welcome & role panels."
    ]]]
  },
  texts: {
    title: "Panel texts",
    features: [["What it can do", [
      "Edit the title, text and button labels of the **LFG**, **Ticket** and **Room controls** panels.",
      "Button labels under **LFG group posts** (Leave, Invite…, Manage, Close, Voice room) and in **ticket messages** (Approve, Deny, Close).",
      "Saving updates the panels already posted in Discord. **Reset to default** restores the original text.",
      "The title is plain text (Discord shows no markdown there); leave it **empty** for no title and put headings (# / ## / -#) in the text. In Discord: `/panel-text`.",
      "LFG panel placeholders: `{hours}` — auto-delete hours, `{reminder}` — reminder minutes.",
      "**Stripe color** — the colored line on the left of each panel (the swatch, a #hex code, or ↩ for the default)."
    ]], ["Bot messages", [
      "**💬 Bot messages** — every other text the bot writes: replies, DMs, group posts, ticket messages, reminders, menu placeholders, buttons and window (modal) titles and fields.",
      "**📝 Texts by module** — a tab per module (LFG, Tickets, Temp Voice, Characters, Welcome & role panels, Birthdays, General): its panels first, then its bot messages. **All texts** — everything at once, for searching.",
      "Search finds a text by its name or words, **Only changed** shows what you edited.",
      "Words in `{braces}` are filled in by the bot — keep them where you need the value. In fields marked “- = hide”, a single `-` removes the line.",
      "Stripe colors of group posts (recruiting / full / closed), tickets, DMs, invites and the verification are set here too.",
      "**Save** in the bar at the bottom saves everything at once; texts of group posts and ticket messages update the active posts right away. ↩ returns one text to the default, **Reset all to default** — every text."
    ]]],
    discord: [["Where it shows", [
      "Panels posted by `/lfg-panel`, `/ticket-panel`, `/tempvoice panel`, `/admin` or this site use your text; `/room panel` too.",
      "Discord markdown works: **bold**, *italic*, `code`, lists, `-#` small text."
    ]]],
    panel: [["Editing", [
      "Change the fields of a panel → **Save & update panels**. Empty field = default text.",
      "Bot messages: open a group, edit the text or color (the row turns yellow), then **Save** in the bar at the bottom.",
      "In Discord, `/panel-text` edits the title, text and stripe color of a panel.",
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

// ================= Guild: calendar, attendance, roster, inactive =================

const SLOT_ROLE_CHIP = { Dps: "⚔️ DD", Tank: "🛡️ Tank", Healer: "💚 Sup" };

async function pageGuild(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "🏰 Guild"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const tab = ["calendar", "attendance", "roster", "inactive"].includes(store.get("gb.guildTab")) ? store.get("gb.guildTab") : "calendar";
  main.append(tabsRow([["calendar", "📅 Calendar"], ["attendance", "📊 Attendance"], ["roster", "👥 Roster"], ["inactive", "💤 Inactive"]], tab, "gb.guildTab"));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  main.append(holder);
  try {
    await ({ calendar: guildCalendar, attendance: guildAttendance, roster: guildRoster, inactive: guildInactive }[tab])(holder);
  } catch (e) { holder.replaceChildren(errorState(e.message)); }
}

/** "Add to Google Calendar" link for one event (2 hours by default). */
function googleCalendarUrl(ev) {
  const fmt = ms => new Date(ms).toISOString().replace(/[-:]/g, "").replace(/\.\d{3}/, "");
  const details = [ev.project ? `Project: ${ev.project}` : null, ev.note ? `Note: ${ev.note}` : null,
    ev.organizer ? `Organizer: ${ev.organizer.name}` : null, ev.jumpUrl ? `Discord: ${ev.jumpUrl}` : null].filter(Boolean).join("\n");
  const q = new URLSearchParams({ action: "TEMPLATE", text: ev.title, dates: `${fmt(ev.start)}/${fmt(ev.start + 2 * 3600000)}`, details });
  if (ev.channel) q.set("location", `#${ev.channel}`);
  return `https://calendar.google.com/calendar/render?${q}`;
}

async function guildCalendar(holder, path = "/guild/calendar", forMember = false) {
  const data = await gApi("GET", path);
  const days = Array.from({ length: 7 }, (_, i) => { const d = new Date(); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() + i); return d; });
  const sameDay = (ms, d) => { const x = new Date(ms); return x.getFullYear() === d.getFullYear() && x.getMonth() === d.getMonth() && x.getDate() === d.getDate(); };
  // Projects: tabs when the events belong to more than one project
  const projects = [...new Map(data.events.filter(e => e.projectKey).map(e => [e.projectKey, e.project || e.projectKey])).entries()];
  const storeKey = forMember ? "gb.calProjectM" : "gb.calProject";
  let pick = store.get(storeKey) || "";
  if (pick && !projects.some(([k]) => k === pick)) pick = "";
  const events = data.events.filter(e => !pick || e.projectKey === pick).sort((a, b) => a.start - b.start);
  const projTabs = projects.length > 1 ? h("div", { class: "seg screen-tabs", style: "margin-bottom:10px" },
    [["", "🌐 All projects"], ...projects.map(([k, n]) => [k, `🎮 ${n}`])].map(([k, label]) =>
      h("button", { type: "button", class: `seg-btn${pick === k ? " active" : ""}`, onclick: () => { store.set(storeKey, k); guildCalendar(holder, path, forMember); } }, label))) : null;
  const eventCard = ev => h("div", { class: `wk-event${ev.recurring ? " planned" : ""}` },
    h("div", { class: "wk-time" }, new Date(ev.start).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }),
      ev.recurring ? h("span", { class: "chip", title: "A recurring group — the post appears automatically before the start" }, "🔁 planned") : null),
    h("b", {}, ev.title),
    h("div", { class: "muted small" }, [ev.project, ev.channel ? `#${ev.channel}` : null].filter(Boolean).join(" · ")),
    h("div", { class: "small" }, ev.recurring ? `${ev.total} slots` : `👥 ${ev.filled}/${ev.total}${ev.reserve ? ` · reserve ${ev.reserve}` : ""}`),
    ev.organizer ? h("div", { class: "small" }, member(ev.organizer)) : null,
    ev.mine ? h("span", { class: `badge ${ev.mine === "group" ? "ok" : "wait"}` }, ev.mine === "group" ? "You're in" : "Reserve") : null,
    h("div", { class: "row wk-actions" },
      forMember && !ev.recurring ? h("button", { class: "btn sm", title: "Open this group in Groups to sign up",
        onclick: () => { store.set("gb.focusGroup", ev.id); store.set("gb.playProject", "all"); state.tab = "play"; store.set("gb.tab", "play"); renderShell(); } },
        icon(ev.mine ? "users-round" : "user-plus", 14), ev.mine ? "Open" : "Sign up") : null,
      ev.jumpUrl ? h("a", { class: "btn ghost sm", href: ev.jumpUrl, target: "_blank", rel: "noopener" }, icon("external-link", 14), "Discord") : null,
      h("a", { class: "btn ghost sm", href: googleCalendarUrl(ev), target: "_blank", rel: "noopener", title: "Add this group to your Google Calendar" },
        icon("calendar-plus", 14), "Google Calendar")));
  holder.replaceChildren(h("div", { class: "card" }, projTabs,
    h("p", { class: "muted small" }, `Groups with a start time and recurring groups for the next 7 days, in your local time (the server's time zone: ${data.zone}). `,
      "Google Calendar adds one event (2 hours) to your own calendar."),
    events.length ? h("div", { class: "wk-grid" }, days.map(d => {
      const list = events.filter(ev => sameDay(ev.start, d));
      return h("div", { class: `wk-day${list.length ? "" : " empty"}` },
        h("div", { class: "wk-day-head" }, d.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" })),
        list.length ? h("div", { class: "wk-day-events" }, list.map(eventCard)) : h("div", { class: "muted small", style: "padding-top:6px" }, "nothing planned"));
    })) : emptyState("calendar", "Nothing planned", "Groups with a start time and recurring groups appear here.")));
}

/** Sortable table: cols = [[title, value(row) for sorting, render(row)]]. */
function sortTable(rows, cols, initial = 0, desc = true) {
  let by = initial, down = desc;
  const wrap = h("div", { class: "table-wrap" });
  const draw = () => {
    const sorted = rows.slice().sort((a, b) => {
      const x = cols[by][1](a), y = cols[by][1](b);
      const r = typeof x === "string" ? String(x).localeCompare(String(y)) : (x ?? -Infinity) - (y ?? -Infinity);
      return down ? -r : r;
    });
    wrap.replaceChildren(h("table", { class: "table" },
      h("thead", {}, h("tr", {}, cols.map(([title], i) => h("th", { class: "sortable", onclick: () => { if (by === i) down = !down; else { by = i; down = true; } draw(); } },
        title, by === i ? (down ? " ▾" : " ▴") : "")))),
      h("tbody", {}, sorted.map(r => h("tr", {}, cols.map(c => h("td", {}, c[2](r))))))));
  };
  draw();
  return wrap;
}

async function guildAttendance(holder) {
  const period = h("select", {}, [[7, "7 days"], [30, "30 days"], [90, "90 days"], [365, "a year"]].map(([v, l]) => h("option", { value: v }, l)));
  period.value = store.get("gb.attDays") || "30";
  let projects = [];
  try { projects = (await gApi("GET", "/lfg/projects")).projects || []; } catch { /* no projects */ }
  const project = h("select", {}, h("option", { value: "" }, "All projects"), projects.map(p => h("option", { value: p.key }, p.name)));
  project.value = store.get("gb.attProject") || "";
  const body = h("div", {}, skeletonLines(4));
  const load = async () => {
    store.set("gb.attDays", period.value); store.set("gb.attProject", project.value);
    body.replaceChildren(skeletonLines(4));
    const d = await gApi("GET", `/guild/attendance?days=${period.value}${project.value ? `&project=${encodeURIComponent(project.value)}` : ""}`);
    const rate = r => r.came + r.noShow ? Math.round(100 * r.came / (r.came + r.noShow)) : null;
    body.replaceChildren(
      h("p", { class: "muted small" }, `${d.groups} closed group(s), attendance marked in ${d.marked}. `,
        "Came / No-show come from the organizer's attendance marks (Manage → Attendance after the start); unmarked groups count only as signed up."),
      d.rows.length ? sortTable(d.rows, [
        ["Member", r => r.member.name, r => member(r.member)],
        ["Signed up", r => r.groups, r => String(r.groups)],
        ["Came", r => r.came, r => h("span", { class: "badge ok" }, String(r.came))],
        ["No-show", r => r.noShow, r => r.noShow ? h("span", { class: "badge lock" }, String(r.noShow)) : "0"],
        ["Unmarked", r => r.unmarked, r => String(r.unmarked)],
        ["Rate", r => rate(r), r => rate(r) == null ? "—" : `${rate(r)}%`],
        ["Organized", r => r.organized, r => String(r.organized)],
        ["Roles", r => r.roles.length, r => r.roles.map(x => h("span", { class: "chip" }, `${x.role} ×${x.count}`))],
        ["Last", r => r.last, r => fmtRelative(r.last)]
      ], 2) : emptyState("chart-column", "No data yet", "Closed groups of this period appear here."));
  };
  period.addEventListener("change", load);
  project.addEventListener("change", load);
  holder.replaceChildren(h("div", { class: "card" }, h("div", { class: "row", style: "margin-bottom:10px" }, period, projects.length ? project : null), body));
  await load();
}

async function guildRoster(holder) {
  const d = await gApi("GET", "/guild/roster");
  const project = h("select", {}, h("option", { value: "" }, "All projects"), d.projects.map(p => h("option", { value: p.key }, p.name)));
  project.value = store.get("gb.rosterProject") || (d.projects.find(p => p.isDefault) || {}).key || "";
  const role = h("select", {}, h("option", { value: "" }, "Any group role"), Object.entries(SLOT_ROLE_CHIP).map(([k, l]) => h("option", { value: k }, l)));
  const cls = h("select", {}, h("option", { value: "" }, "Any class"));
  const minGs = h("input", { type: "number", min: "0", placeholder: "min GS", style: "width:110px" });
  const minCp = h("input", { type: "number", min: "0", placeholder: "min CP", style: "width:120px" });
  const srv = h("select", { "aria-label": "Server" }, h("option", { value: "" }, "Any server"));
  const leg = h("select", { "aria-label": "Legion" }, h("option", { value: "" }, "Any legion"));
  const search = h("input", { type: "search", placeholder: "Search member or character…", style: "flex:1;min-width:180px" });
  const onlyHere = h("input", { type: "checkbox", checked: true });
  const body = h("div", {});
  const fillClasses = () => {
    const seen = new Map();
    for (const c of d.characters) if (c.classRole && (!project.value || c.project === project.value)) seen.set(c.classRole.id, { ...c.classRole, icon: c.classIcon });
    const keep = cls.value;
    cls.replaceChildren(h("option", { value: "" }, "Any class"), ...[...seen.values()].sort((a, b) => a.name.localeCompare(b.name)).map(r => h("option", { value: r.id, "data-icon": r.icon || null }, r.name)));
    cls.value = seen.has(keep) ? keep : "";
    const keepSrv = srv.value;
    const servers = [...new Set(d.characters.filter(c => c.server && (!project.value || c.project === project.value)).map(c => c.server))].sort();
    srv.replaceChildren(h("option", { value: "" }, "Any server"), ...servers.map(x => h("option", { value: x }, x)));
    srv.value = servers.includes(keepSrv) ? keepSrv : "";
    srv.style.display = servers.length ? "" : "none";
    const keepLeg = leg.value;
    const legions = [...new Set(d.characters.filter(c => c.armory && c.armory.legion && (!project.value || c.project === project.value)).map(c => c.armory.legion))].sort();
    leg.replaceChildren(h("option", { value: "" }, "Any legion"), ...legions.map(x => h("option", { value: x }, x)));
    leg.value = legions.includes(keepLeg) ? keepLeg : "";
    leg.style.display = legions.length ? "" : "none";
  };
  const stale = ms => !ms || Date.now() - ms > 14 * 86400000;
  const draw = () => {
    store.set("gb.rosterProject", project.value);
    const q = search.value.trim().toLowerCase();
    const rows = d.characters.filter(c =>
      (!project.value || c.project === project.value) &&
      (!role.value || c.slotRoles.includes(role.value)) &&
      (!cls.value || (c.classRole && c.classRole.id === cls.value)) &&
      (minGs.value === "" || (c.gs ?? -1) >= Number(minGs.value)) &&
      (minCp.value === "" || ((c.armory && c.armory.combatPower) ?? -1) >= Number(minCp.value)) &&
      (!srv.value || c.server === srv.value) &&
      (!leg.value || (c.armory && c.armory.legion) === leg.value) &&
      (!onlyHere.checked || c.onServer) &&
      (!q || c.character.toLowerCase().includes(q) || c.member.name.toLowerCase().includes(q) || (c.armory && (c.armory.legion || "").toLowerCase().includes(q))));
    const players = new Set(rows.map(r => r.member.id)).size;
    body.replaceChildren(h("p", { class: "muted small" }, `${rows.length} character(s) of ${players} member(s). `,
      "The group-role filter uses the class (LFG → Classes: DD / Tank / Sup). GS older than 14 days is marked ⏳."),
      rows.length ? sortTable(rows, [
        ["Member", r => r.member.name, r => member(r.member)],
        ["Character", r => r.character, r => h("span", { class: "row", style: "gap:6px;flex-wrap:nowrap" }, h("b", {}, r.character), armoryLink(r.armory))],
        ["Class", r => r.classRole ? r.classRole.name : "", r => r.classRole ? h("span", { class: "row", style: "gap:4px" }, classMark(r) || null, r.classRole.name) : h("span", { class: "muted" }, "—")],
        ["Server", r => r.server || "", r => r.server ? serverChip(r.server, r.race) : h("span", { class: "muted" }, "—")],
        ["Legion", r => (r.armory && r.armory.legion) || "", r => r.armory && r.armory.legion ? h("span", { class: "chip legion" }, icon("shield", 12), r.armory.legion) : h("span", { class: "muted" }, "—")],
        ["GS", r => r.gs, r => r.gs == null ? h("span", { class: "muted" }, "—") :
          h("span", { title: r.gsUpdated ? `Updated ${new Date(r.gsUpdated).toLocaleDateString()}` : "Update date unknown" }, String(r.gs), stale(r.gsUpdated) ? " ⏳" : "")],
        ["Combat power", r => r.armory ? r.armory.combatPower ?? 0 : -1, armoryCell],
        ["On server since", r => r.joined, r => r.joined ? new Date(r.joined).toLocaleDateString() : "—"],
        ["Last active", r => r.lastActive, r => r.lastActive ? fmtRelative(r.lastActive) : h("span", { class: "muted small", title: `Activity is tracked since ${new Date(d.trackingSince).toLocaleDateString()}` }, "no data")]
      ], 5) : emptyState("users", "Nobody matches", "Change the filters."));
  };
  project.addEventListener("change", () => { fillClasses(); draw(); });
  for (const el of [role, cls, onlyHere, srv, leg]) el.addEventListener("change", draw);
  for (const el of [minGs, minCp, search]) el.addEventListener("input", draw);
  fillClasses();
  holder.replaceChildren(h("div", { class: "card" },
    h("div", { class: "row", style: "margin-bottom:10px;flex-wrap:wrap" }, d.projects.length ? project : null, role, cls, srv, leg, minGs, minCp, search,
      h("label", { class: "toggle-line" }, onlyHere, h("span", {}, "Only members on the server"))), body));
  draw();
}

async function guildInactive(holder) {
  const days = h("input", { type: "number", min: "1", max: "365", value: store.get("gb.inactiveDays") || "14", style: "width:90px" });
  const role = roleSelect("Any role");
  role.value = store.get("gb.inactiveRole") || "";
  const body = h("div", {}, skeletonLines(4));
  const load = async () => {
    store.set("gb.inactiveDays", days.value); store.set("gb.inactiveRole", role.value);
    const d = await gApi("GET", `/guild/inactive?days=${Number(days.value) || 14}${role.value ? `&roleId=${role.value}` : ""}`);
    const since = new Date(d.trackingSince);
    const tooEarly = Date.now() - d.trackingSince < (Number(days.value) || 14) * 86400000;
    body.replaceChildren(
      h("p", { class: "muted small" }, `${d.rows.length} of ${d.members} member(s) didn't write in any channel and didn't sit in voice for ${days.value}+ days. `,
        `Activity is tracked since ${since.toLocaleDateString()}.`),
      d.memberRoles && d.memberRoles.length
        ? h("p", { class: "muted small" }, "Only members with ", d.memberRoles.map(r => roleChip(r)), " (Settings → Member roles) are counted.")
        : h("p", { class: "warn-text small" }, "⚠️ Member roles aren't set (Settings → Member roles) — everyone on the server is counted."),
      tooEarly ? h("p", { class: "warn-text small" }, "⚠️ Tracking started less than this many days ago — “no data” only means nothing was seen since then.") : null,
      d.rows.length ? sortTable(d.rows, [
        ["Member", r => r.member.name, r => member(r.member)],
        ["On server since", r => r.joined, r => r.joined ? new Date(r.joined).toLocaleDateString() : "—"],
        ["Last message", r => r.lastMessage, r => r.lastMessage ? fmtRelative(r.lastMessage) : h("span", { class: "muted small" }, "no data")],
        ["Last in voice", r => r.lastVoice, r => r.lastVoice ? fmtRelative(r.lastVoice) : h("span", { class: "muted small" }, "no data")],
        ["Roles", r => r.roles.length, r => r.roles.map(x => roleChip(x))],
        ["Characters", r => r.characters, r => String(r.characters)]
      ], 2, false) : emptyState("check", "Everyone is active", "Nobody matches this period."));
  };
  days.addEventListener("change", load);
  role.addEventListener("change", load);
  holder.replaceChildren(h("div", { class: "card" },
    h("div", { class: "row", style: "margin-bottom:10px" }, h("span", { class: "muted" }, "No activity for"), days, h("span", { class: "muted" }, "days"), role),
    body));
  await load();
}

// ================= Member: calendar, top players, guild roster =================

async function pageSchedule(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Calendar"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  main.append(holder);
  try { await guildCalendar(holder, "/me/calendar", true); } catch (e) { holder.replaceChildren(errorState(e.message)); }
}

/** Member: guild roster of the member's projects (+ top active players, if the admins turned it on). */
async function pageMemberRoster(main) {
  main.append(h("div", { class: "card-head" }, h("h1", {}, "Guild roster"),
    h("button", { class: "btn btn-refresh", title: "Reload this page's data", onclick: hardRefresh }, "🔄 Refresh")));
  const holder = h("div", {}, h("div", { class: "card" }, skeletonLines(4)));
  let guild;
  try { guild = await gApi("GET", "/me/guild"); } catch (e) { main.append(errorState(e.message)); return; }
  const tabs = [["roster", "👥 Roster"]];
  if (guild.top) tabs.push(["top", "🏆 Top players"]);
  let tab = store.get("gb.rosterTab") || "roster";
  if (!tabs.some(([k]) => k === tab)) tab = "roster";
  if (tabs.length > 1) main.append(tabsRow(tabs, tab, "gb.rosterTab"));
  main.append(holder);
  try {
    if (tab === "top") memberTop(holder, guild.top, guild);
    else memberRoster(holder, guild);
  } catch (e) { holder.replaceChildren(errorState(e.message)); }
}

/** Project tabs for the member pages: all of the member's projects (even one — so it's clear what is shown). */
function memberProjectTabs(projects, current, onPick, withAll = true) {
  if (!projects.length) return null;
  const items = [...(withAll && projects.length > 1 ? [["", "🌐 All projects"]] : []), ...projects.map(p => [p.key, `🎮 ${p.name}`])];
  return h("div", { class: "seg screen-tabs", style: "margin-bottom:10px" }, items.map(([k, label]) =>
    h("button", { type: "button", class: `seg-btn${current === k ? " active" : ""}`, onclick: () => onPick(k) }, label)));
}

function memberTop(holder, top, guild = null) {
  const projects = guild ? guild.projects : [];
  let pick = store.get("gb.mTopProject") || "";
  if (pick && !projects.some(p => p.key === pick)) pick = "";
  if (projects.length === 1) pick = projects[0].key;
  if (pick && guild && guild.topByProject && guild.topByProject[pick]) top = guild.topByProject[pick];
  const tabs = memberProjectTabs(projects, pick, k => { store.set("gb.mTopProject", k); memberTop(holder, guild.top, guild); });
  const medal = i => ["🥇", "🥈", "🥉"][i] || `${i + 1}.`;
  holder.replaceChildren(h("div", { class: "card" }, tabs,
    h("p", { class: "muted small" }, "Most groups played in the last 30 days (no-shows are not counted)."),
    top.length ? h("div", { class: "list" }, top.map((t, i) => h("div", { class: `list-row${t.member.id === (state.me && state.me.id) ? " me" : ""}` },
      h("span", { class: "top-place" }, medal(i)), h("div", { class: "grow" }, member(t.member)),
      t.role ? h("span", { class: "chip" }, t.role) : null,
      h("b", {}, `${t.groups} group${t.groups === 1 ? "" : "s"}`))))
      : emptyState("trophy", "No groups yet", "Finished groups of the last 30 days appear here.")));
}

function memberRoster(holder, guild) {
  const list = guild.roster;
  // Project = tabs (like the calendar); a hidden select keeps the filter code simple
  const project = h("select", { style: "display:none" }, h("option", { value: "" }, "All projects"), guild.projects.map(p => h("option", { value: p.key }, p.name)));
  project.value = store.get("gb.mRosterProject") ?? (guild.projects.find(p => p.isDefault) || {}).key ?? "";
  if (guild.projects.length === 1) project.value = guild.projects[0].key;
  const tabsBox = h("div", {});
  const drawTabs = () => tabsBox.replaceChildren(memberProjectTabs(guild.projects, project.value, k => {
    project.value = k; drawTabs(); fillClasses(); draw();
  }) || "");
  const role = h("select", {}, h("option", { value: "" }, "Any group role"), Object.entries(SLOT_ROLE_CHIP).map(([k, l]) => h("option", { value: k }, l)));
  const cls = h("select", {}, h("option", { value: "" }, "Any class"));
  const minGs = h("input", { type: "number", min: "0", placeholder: "min GS", style: "width:110px" });
  const minCp = h("input", { type: "number", min: "0", placeholder: "min CP", style: "width:120px" });
  const srv = h("select", { "aria-label": "Server" }, h("option", { value: "" }, "Any server"));
  const leg = h("select", { "aria-label": "Legion" }, h("option", { value: "" }, "Any legion"));
  const search = h("input", { type: "search", placeholder: "Search member or character…", style: "flex:1;min-width:180px" });
  const body = h("div", {});
  // Classes of the chosen project (the ones characters have)
  const fillClasses = () => {
    const seen = new Map();
    for (const c of list) if (c.classRole && (!project.value || c.project === project.value)) seen.set(c.classRole.id, { ...c.classRole, icon: c.classIcon });
    const keep = cls.value;
    cls.replaceChildren(h("option", { value: "" }, "Any class"), ...[...seen.values()].sort((a, b) => a.name.localeCompare(b.name)).map(r => h("option", { value: r.id, "data-icon": r.icon || null }, r.name)));
    cls.value = seen.has(keep) ? keep : "";
    const keepSrv = srv.value;
    const servers = [...new Set(list.filter(c => c.server && (!project.value || c.project === project.value)).map(c => c.server))].sort();
    srv.replaceChildren(h("option", { value: "" }, "Any server"), ...servers.map(x => h("option", { value: x }, x)));
    srv.value = servers.includes(keepSrv) ? keepSrv : "";
    srv.style.display = servers.length ? "" : "none";
    const keepLeg = leg.value;
    const legions = [...new Set(list.filter(c => c.armory && c.armory.legion && (!project.value || c.project === project.value)).map(c => c.armory.legion))].sort();
    leg.replaceChildren(h("option", { value: "" }, "Any legion"), ...legions.map(x => h("option", { value: x }, x)));
    leg.value = legions.includes(keepLeg) ? keepLeg : "";
    leg.style.display = legions.length ? "" : "none";
  };
  const draw = () => {
    store.set("gb.mRosterProject", project.value);
    const q = search.value.trim().toLowerCase();
    const rows = list.filter(c => (!project.value || c.project === project.value) && (!role.value || c.slotRoles.includes(role.value)) &&
      (!cls.value || (c.classRole && c.classRole.id === cls.value)) &&
      (minGs.value === "" || (c.gs ?? -1) >= Number(minGs.value)) &&
      (minCp.value === "" || ((c.armory && c.armory.combatPower) ?? -1) >= Number(minCp.value)) &&
      (!srv.value || c.server === srv.value) &&
      (!leg.value || (c.armory && c.armory.legion) === leg.value) &&
      (!q || c.character.toLowerCase().includes(q) || c.member.name.toLowerCase().includes(q)));
    body.replaceChildren(h("p", { class: "muted small" }, `${rows.length} character(s). Find a tank or a healer for your group.`),
      rows.length ? sortTable(rows, [
        ["Member", r => r.member.name, r => member(r.member)],
        ["Character", r => r.character, r => h("span", { class: "row", style: "gap:6px;flex-wrap:nowrap" }, h("b", {}, r.character), armoryLink(r.armory))],
        ["Class", r => r.classRole ? r.classRole.name : "", r => r.classRole ? h("span", { class: "row", style: "gap:4px" }, classMark(r) || null, r.classRole.name) : h("span", { class: "muted" }, "—")],
        ["Server", r => r.server || "", r => r.server ? serverChip(r.server, r.race) : h("span", { class: "muted" }, "—")],
        ["Legion", r => (r.armory && r.armory.legion) || "", r => r.armory && r.armory.legion ? h("span", { class: "chip legion" }, icon("shield", 12), r.armory.legion) : h("span", { class: "muted" }, "—")],
        ["GS", r => r.gs, r => r.gs == null ? h("span", { class: "muted" }, "—") : String(r.gs)],
        ["Combat power", r => r.armory ? r.armory.combatPower ?? 0 : -1, armoryCell]
      ], 5) : emptyState("users", "Nobody matches", "Change the filters."));
  };
  project.addEventListener("change", () => { fillClasses(); draw(); });
  role.addEventListener("change", draw);
  cls.addEventListener("change", draw);
  srv.addEventListener("change", draw);
  leg.addEventListener("change", draw);
  for (const el of [minGs, minCp, search]) el.addEventListener("input", draw);
  fillClasses();
  drawTabs();
  holder.replaceChildren(h("div", { class: "card" }, tabsBox,
    h("div", { class: "row", style: "margin-bottom:10px;flex-wrap:wrap" }, role, cls, srv, leg, minGs, minCp, search), body,
    h("p", { class: "muted small", style: "margin-top:8px" }, "Only projects you have access to are shown.")));
  draw();
}
