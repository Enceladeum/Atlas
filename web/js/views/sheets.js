// Sheet browser: filterable sheet list, virtualized grid, header drawer, CSV export.
import { api } from "../api.js";
import { parseCsv } from "../csv.js";
import { el, debounce, toast, spinner, crumbs } from "../ui.js";
import { VGrid } from "../grid.js";

const PREVIEW_ROWS = 2001; // rows fetched for the interactive view (header + 2000)
const LANGS = [["", "default"], ["en", "English"], ["ja", "日本語"], ["de", "Deutsch"], ["fr", "Français"]];

let allSheets = null;

export async function renderSheets(view, params) {
  crumbs("Sheets");
  view.innerHTML = "";
  const split = el("div", { class: "pane-split" });
  const left = el("div", { class: "pane-left" });
  const main = el("div", { class: "pane-main" });
  split.append(left, main);
  view.append(split);

  // ---- left: sheet list ----
  const sb = el("div", { class: "searchbox" });
  const input = el("input", { type: "text", placeholder: "Filter sheets…", autocomplete: "off", spellcheck: "false" });
  sb.append(el("span", { html: `<svg viewBox="0 0 24 24"><circle cx="10.5" cy="10.5" r="6.5" stroke="currentColor" stroke-width="2" fill="none"/><path d="M15.5 15.5L21 21" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>` }).firstChild, input);
  const count = el("div", { class: "list-count" });
  const list = el("div", { class: "list" });
  left.append(sb, count, list);

  if (!allSheets) {
    list.append(spinner("Loading sheet list…"));
    try { allSheets = await api.sheets(); }
    catch (e) { toast("Sheet list failed: " + e.message, "err"); allSheets = []; }
  }

  const renderList = (filter) => {
    const f = (filter || "").toLowerCase();
    const hits = f ? allSheets.filter(s => s.toLowerCase().includes(f)) : allSheets;
    count.textContent = `${hits.length} / ${allSheets.length} sheets`;
    list.innerHTML = "";
    const frag = document.createDocumentFragment();
    for (const s of hits.slice(0, 800)) {
      const it = el("div", { class: "list-item" + (s === params.sheet ? " active" : ""), onclick: () => { location.hash = `#/sheets/${s}`; } }, s);
      frag.append(it);
    }
    list.append(frag);
    if (hits.length > 800) list.append(el("div", { class: "list-count" }, `… ${hits.length - 800} more (narrow the filter)`));
  };
  input.addEventListener("input", debounce(() => renderList(input.value), 120));
  renderList();
  // keep the active item in view
  queueMicrotask(() => list.querySelector(".active")?.scrollIntoView({ block: "center" }));

  // ---- main: grid or empty state ----
  if (!params.sheet) {
    main.append(el("div", { class: "empty" },
      el("div", { html: `<svg viewBox="0 0 24 24"><path d="M4 5h16M4 10h16M4 15h16M4 20h10" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" fill="none"/></svg>` }).firstChild,
      el("div", {}, "Select a sheet to browse"),
      el("div", { class: "hint" }, "Every sheet here is also `atlas dump <name>` on the CLI")));
    return;
  }
  await renderSheet(main, params.sheet, params.lang || "");
}

async function renderSheet(main, name, lang) {
  crumbs("Sheets", name);
  main.innerHTML = "";

  const tb = el("div", { class: "grid-toolbar" });
  const title = el("h2", {}, name);
  const meta = el("span", { class: "meta" }, "");
  const langSel = el("select", { class: "btn", title: "Language", onchange: () => { location.hash = `#/sheets/${name}${langSel.value ? "?lang=" + langSel.value : ""}`; } });
  for (const [v, label] of LANGS) langSel.append(el("option", { value: v, ...(v === lang ? { selected: "" } : {}) }, label));
  const exportBtn = el("a", { class: "btn primary", href: api.sheetCsvUrl(name, lang), download: `${name}.csv`, html: `<svg viewBox="0 0 24 24"><path d="M12 3v12m0 0l-4.5-4.5M12 15l4.5-4.5M4 20h16" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" fill="none"/></svg><span>Export CSV</span>` });
  tb.append(title, meta, el("span", { class: "spacer" }), langSel, exportBtn);
  main.append(tb);

  const headerDrawer = el("details", { class: "drawer" });
  headerDrawer.append(el("summary", {}, "Column layout — atlas header " + name));
  const headerPre = el("pre", {}, "…");
  headerDrawer.append(headerPre);
  headerDrawer.addEventListener("toggle", async () => {
    if (headerDrawer.open && headerPre.textContent === "…") {
      try { headerPre.textContent = await api.sheetHeader(name); }
      catch (e) { headerPre.textContent = "header failed: " + e.message; }
    }
  }, { once: false });
  main.append(headerDrawer);

  const bannerSlot = el("div");
  main.append(bannerSlot);
  const gridHost = el("div", { style: "flex:1; min-height:0; display:flex; flex-direction:column;" });
  main.append(gridHost);
  const load = spinner(`Loading ${name}…`);
  gridHost.append(load);

  try {
    const t0 = performance.now();
    const csv = await api.sheetCsv(name, { lang, max: PREVIEW_ROWS });
    const rows = parseCsv(csv);
    const header = rows.shift() || [];
    load.remove();
    const grid = new VGrid(gridHost);
    grid.setData(header, rows);
    const ms = Math.round(performance.now() - t0);
    meta.textContent = `${header.length} cols · ${rows.length}${rows.length >= PREVIEW_ROWS - 1 ? "+" : ""} rows · ${ms} ms`;
    if (rows.length >= PREVIEW_ROWS - 1) {
      const banner = el("div", { class: "banner" },
        `Showing the first ${PREVIEW_ROWS - 1} rows. `,
        el("a", { onclick: async () => {
          banner.textContent = "Loading full sheet…";
          try {
            const full = parseCsv(await api.sheetCsv(name, { lang }));
            full.shift();
            grid.setData(header, full);
            meta.textContent = `${header.length} cols · ${full.length} rows`;
            banner.remove();
          } catch (e) { toast("Full load failed: " + e.message, "err"); }
        } }, "Load all rows"),
        " or use Export CSV for the complete dump.");
      bannerSlot.append(banner);
    }
  } catch (e) {
    load.remove();
    gridHost.append(el("div", { class: "empty" }, el("div", {}, "Failed to load sheet"), el("div", { class: "hint" }, e.message)));
  }
}
