// Atlas frontend boot: hash router, rail state, server status, command palette.
import { api } from "./api.js";
import { el, debounce } from "./ui.js";
import { renderSheets } from "./views/sheets.js";
import { renderTerritory } from "./views/territory.js";
import { renderAssets } from "./views/assets.js";

const view = document.getElementById("view");

const routes = {
  sheets: renderSheets,
  territory: renderTerritory,
  assets: renderAssets,
};

function parseHash() {
  // #/sheets/Emote?lang=en  →  { route:"sheets", sheet:"Emote", lang:"en" }
  const h = location.hash.replace(/^#\/?/, "");
  const [pathPart, queryPart] = h.split("?");
  const seg = pathPart.split("/").filter(Boolean);
  const params = Object.fromEntries(new URLSearchParams(queryPart || ""));
  const route = seg[0] || document.querySelector(".rail-item")?.dataset.route || "sheets";
  if (route === "sheets" && seg[1]) params.sheet = decodeURIComponent(seg[1]);
  if (route === "territory" && seg[1]) params.tt = seg[1];
  if (route === "assets" && seg[1]) params.path = decodeURIComponent(seg.slice(1).join("/"));
  return { route, params };
}

async function render() {
  const { route, params } = parseHash();
  document.querySelectorAll(".rail-item").forEach(a =>
    a.classList.toggle("active", a.dataset.route === route));
  const fn = routes[route] || renderSheets;
  try { await fn(view, params); }
  catch (e) {
    view.innerHTML = "";
    view.append(el("div", { class: "empty" }, el("div", {}, "View failed"), el("div", { class: "hint" }, e.message)));
  }
}
window.addEventListener("hashchange", render);

// ---- server status ----
async function pollStatus() {
  const conn = document.getElementById("conn");
  const label = document.getElementById("conn-label");
  try {
    const m = await api.meta();
    conn.className = "rail-status ok";
    const g = (m.game || "").split(/[\\/]/).filter(Boolean);
    label.textContent = g.length > 2 ? g[g.length - 3] : "connected";
    label.title = m.game;
    const ver = document.getElementById("ver");
    if (ver && m.version) {
      ver.textContent = m.version;
      ver.title = `build ${m.version}\ncontent: ${m.contentRoot || "?"}\nsettings: ${m.settingsSource || "?"}\ngame: ${m.game || "?"}`;
    }
  } catch {
    conn.className = "rail-status err";
    label.textContent = "offline";
  }
}
pollStatus();
setInterval(pollStatus, 15000);

// ---- command palette ----
const pal = document.getElementById("palette");
const palInput = document.getElementById("palette-input");
const palResults = document.getElementById("palette-results");
let palItems = [], palSel = 0, sheetCache = null, palSeq = 0;

function openPalette() {
  pal.classList.remove("hidden");
  palInput.value = "";
  palInput.focus();
  updatePalette("");
}
function closePalette() { pal.classList.add("hidden"); }

async function updatePalette(q) {
  if (!sheetCache) { try { sheetCache = await api.sheets(); } catch { sheetCache = []; } }
  q = q.trim();
  const out = [];
  if (/^\d+$/.test(q)) out.push({ tag: "territory", label: `Open territory ${q}`, go: `#/territory/${q}` });
  if (q.includes("/")) out.push({ tag: "asset", label: `Inspect path ${q}`, go: `#/assets/${encodeURIComponent(q)}` });
  const f = q.toLowerCase();
  for (const s of sheetCache) {
    if (!f || s.toLowerCase().includes(f)) {
      out.push({ tag: "sheet", label: s, go: `#/sheets/${s}` });
      if (out.length > 14) break;
    }
  }
  palItems = out; palSel = 0;
  palResults.innerHTML = "";
  if (!out.length) palResults.append(el("div", { class: "pal-empty" }, "No matches"));
  out.forEach((it, i) => {
    const d = el("div", { class: "pal-item" + (i === palSel ? " sel" : ""), onclick: () => { location.hash = it.go; closePalette(); } },
      el("span", { class: "tag" }, it.tag), it.label);
    palResults.append(d);
  });

  // live global asset-path search (needs --paths; quiet-fail)
  const seq = ++palSeq;
  if (q.length >= 3) {
    api.paths({ q, limit: 6 }).then(r => {
      if (seq !== palSeq || !r.hits?.length) return;
      palResults.querySelector(".pal-empty")?.remove();
      const wasEmpty = palItems.length === 0;
      for (const f of r.hits) {
        const it = { tag: "asset", label: f, go: `#/assets/${encodeURIComponent(f)}` };
        palItems.push(it);
        palResults.append(el("div", { class: "pal-item", onclick: () => { location.hash = it.go; closePalette(); } },
          el("span", { class: "tag" }, "asset"), f));
      }
      if (wasEmpty) palResults.children[0]?.classList.add("sel");
    }).catch(() => {});
  }
}
palInput.addEventListener("input", debounce(() => updatePalette(palInput.value), 100));
palInput.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { closePalette(); return; }
  if (e.key === "Enter" && palItems[palSel]) { location.hash = palItems[palSel].go; closePalette(); return; }
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    palSel = (palSel + (e.key === "ArrowDown" ? 1 : palItems.length - 1)) % palItems.length;
    [...palResults.children].forEach((c, i) => c.classList.toggle("sel", i === palSel));
  }
});
pal.addEventListener("mousedown", (e) => { if (e.target === pal) closePalette(); });
document.getElementById("palette-btn").addEventListener("click", openPalette);
window.addEventListener("keydown", (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); pal.classList.contains("hidden") ? openPalette() : closePalette(); }
});


// ---- rail order: drag grippers, persisted ----
// Default puts territories first (most-used); saved order wins. Routes added
// in future builds that aren't in the saved array append in DOM order.
const RAIL_ORDER_KEY = "atlas.railOrder";
const rail = document.getElementById("rail");
const railSpacer = rail.querySelector(".rail-spacer");
const railItems = () => [...rail.querySelectorAll(".rail-item")];
{
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(RAIL_ORDER_KEY) || "null"); } catch { /* corrupt: fall through */ }
  const order = Array.isArray(saved) && saved.length ? saved : ["territory", "assets", "sheets"];
  const byRoute = new Map(railItems().map(a => [a.dataset.route, a]));
  for (const r of order) { const a = byRoute.get(r); if (a) { rail.insertBefore(a, railSpacer); byRoute.delete(r); } }
  for (const a of byRoute.values()) rail.insertBefore(a, railSpacer);
}
let railDrag = null;
for (const a of railItems()) {
  a.draggable = false; // anchors default draggable; only the grip arms it
  const grip = el("span", { class: "rail-grip", title: "drag to reorder" });
  grip.innerHTML = '<svg viewBox="0 0 24 24" width="10" height="14"><path d="M9 5h.01M9 12h.01M9 19h.01M15 5h.01M15 12h.01M15 19h.01" stroke="currentColor" stroke-width="3" stroke-linecap="round" fill="none"/></svg>';
  a.append(grip);
  grip.addEventListener("pointerdown", () => { a.draggable = true; });
  grip.addEventListener("click", (e) => { e.preventDefault(); e.stopPropagation(); });
  a.addEventListener("dragstart", (e) => {
    railDrag = a; a.classList.add("dragging");
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", a.dataset.route);
  });
  a.addEventListener("dragend", () => {
    a.classList.remove("dragging"); a.draggable = false; railDrag = null;
    localStorage.setItem(RAIL_ORDER_KEY, JSON.stringify(railItems().map(x => x.dataset.route)));
  });
  a.addEventListener("dragover", (e) => {
    if (!railDrag || railDrag === a) return;
    e.preventDefault(); // required for the move cursor; reorder live
    const r = a.getBoundingClientRect();
    rail.insertBefore(railDrag, e.clientY < r.top + r.height / 2 ? a : a.nextSibling);
  });
}
window.addEventListener("pointerup", () => railItems().forEach(x => { x.draggable = false; }));

render();
