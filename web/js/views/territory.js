// Territory workspace: picker (TerritoryType ⋈ PlaceName), workspace files,
// and the 3D viewport (composed-map glTF + collision OBJ overlay, per-layer
// toggles, identity-quadruple selection). Headless parity: atlas territory /
// mod Pcb dump --obj / mod Map gltf.
import { api } from "../api.js";
import { parseCsv } from "../csv.js";
import { el, debounce, toast, spinner, crumbs } from "../ui.js";
import { Viewport } from "../viewport.js";
import { STAGE_PRESETS, STAGE_KEY_LABELS } from "../stages.js";

let ttIndex = null;     // [{ id, name, place }]
let activeVp = null;    // dispose on re-render

async function loadTtIndex() {
  if (ttIndex) return ttIndex;
  const [ttCsv, pnCsv] = await Promise.all([
    api.sheetCsv("TerritoryType"),
    api.sheetCsv("PlaceName"),
  ]);
  const tt = parseCsv(ttCsv); const th = tt.shift();
  const pn = parseCsv(pnCsv); const ph = pn.shift();
  const pnName = new Map();
  const pnNameIdx = ph.indexOf("Name");
  for (const r of pn) pnName.set(r[0], r[pnNameIdx] || "");
  const iName = th.indexOf("Name"), iPlace = th.indexOf("PlaceName");
  ttIndex = tt
    .filter(r => r[iName])
    .map(r => ({ id: r[0], name: r[iName], place: pnName.get(r[iPlace]) || "" }));
  return ttIndex;
}

export async function renderTerritory(view, params) {
  crumbs("Territories");
  if (activeVp) { activeVp.dispose(); activeVp = null; }
  view.innerHTML = "";
  const split = el("div", { class: "pane-split" });
  const left = el("div", { class: "pane-left" });
  const main = el("div", { class: "pane-main" });
  split.append(left, main);
  view.append(split);

  // ---- picker ----
  const sb = el("div", { class: "searchbox" });
  const input = el("input", { type: "text", placeholder: "Filter territories…", autocomplete: "off", spellcheck: "false" });
  sb.append(input);
  const count = el("div", { class: "list-count" });
  const list = el("div", { class: "list" });
  left.append(sb, count, list);
  list.append(spinner("Loading TerritoryType…"));

  let idx = [];
  try { idx = await loadTtIndex(); } catch (e) { toast("TerritoryType load failed: " + e.message, "err"); }

  const renderList = (f) => {
    f = (f || "").toLowerCase();
    const hits = f ? idx.filter(t => t.id.includes(f) || t.name.toLowerCase().includes(f) || t.place.toLowerCase().includes(f)) : idx;
    count.textContent = `${hits.length} / ${idx.length} territories`;
    list.innerHTML = "";
    const frag = document.createDocumentFragment();
    for (const t of hits.slice(0, 500)) {
      frag.append(el("div", {
        class: "list-item" + (t.id === params.tt ? " active" : ""),
        title: `${t.id} ${t.name} — ${t.place}`,
        onclick: () => { location.hash = `#/territory/${t.id}`; },
      }, `${t.id}  ${t.name}`, t.place ? el("span", { style: "color:var(--fg2)" }, `  ${t.place}`) : null));
    }
    list.append(frag);
    if (hits.length > 500) list.append(el("div", { class: "list-count" }, `… ${hits.length - 500} more`));
  };
  input.addEventListener("input", debounce(() => renderList(input.value), 120));
  renderList();
  queueMicrotask(() => list.querySelector(".active")?.scrollIntoView({ block: "center" }));

  if (!params.tt) {
    main.append(el("div", { class: "empty" },
      el("div", {}, "Select a territory"),
      el("div", { class: "hint" }, "Builds the full workspace (11 CSVs), then renders the composed map + collision")));
    return;
  }
  await renderWorkspace(main, params.tt, idx.find(t => t.id === params.tt));
}

async function renderWorkspace(main, tt, info, refresh = false) {
  crumbs("Territories", `${tt} ${info ? info.name : ""}`, info?.place || "");
  main.innerHTML = "";

  const tb = el("div", { class: "grid-toolbar" });
  tb.append(
    el("h2", {}, `${info?.place || "Territory"} `),
    el("span", { class: "meta" }, `TT ${tt}${info ? " · " + info.name : ""}`),
    el("span", { class: "spacer" }),
    el("a", { class: "btn", href: api.collisionObjUrl(tt), download: `collision-${tt}.obj` }, "Collision OBJ"),
    el("a", { class: "btn", href: api.mapGltfUrl(tt), download: `map-${tt}.gltf` }, "Map glTF"),
    el("button", { class: "btn", onclick: () => renderWorkspace(main, tt, info, true), title: "Rebuild the cached workspace AND recompose the map glTF" }, "Refresh"),
  );
  main.append(tb);

  // ---- workspace files ----
  const filesWrap = el("div", { class: "tt-files" });
  main.append(filesWrap);
  const vpWrap = el("div", { class: "tt-main" });
  main.append(vpWrap);
  const loading = el("div", { class: "vp-loading" },
    el("div", { class: "spinner" }), el("div", {}, "Building territory workspace…"),
    el("div", { class: "sub" }, "first build parses every LGB layer; cached afterwards"));
  vpWrap.append(loading);

  let ws;
  try { ws = await api.territory(tt, refresh); }
  catch (e) {
    loading.remove();
    vpWrap.append(el("div", { class: "empty" }, el("div", {}, "Workspace build failed"), el("div", { class: "hint" }, e.message)));
    return;
  }

  const drawer = el("details", { class: "drawer" });
  drawer.append(el("summary", {}, `Workspace files — ${ws.files.length} artifacts (atlas territory ${tt})`));
  const row = el("div", { class: "files-row" });
  for (const f of ws.files) row.append(el("a", { class: "chip", href: api.territoryFileUrl(tt, f), download: f }, f));
  drawer.append(row);
  filesWrap.append(drawer);

  // ---- viewport ----
  const host = el("div", { class: "viewport-host" });
  vpWrap.insertBefore(host, loading);

  const selPanel = el("div", { class: "vp-panel vp-sel", style: "display:none" });
  const statbar = el("div", { class: "vp-statbar" });
  const overlays = el("div", { class: "vp-panel vp-overlays" });
  const layersPanel = el("div", { class: "vp-panel vp-layers", style: "display:none" });
  const findPanel = el("div", { class: "vp-panel vp-find" });
  host.append(overlays, layersPanel, selPanel, statbar, findPanel);

  // ---- movable panels: drag by a panel's header (h3), position remembered;
  // double-click the header to snap back to the CSS default spot ----
  const PANEL_POS_KEY = "atlas.vpPanels";
  let panelPos = {};
  try { panelPos = JSON.parse(localStorage.getItem(PANEL_POS_KEY) || "{}"); } catch { /* corrupt: defaults */ }
  const savePanelPos = () => localStorage.setItem(PANEL_POS_KEY, JSON.stringify(panelPos));
  const clampPanel = (p) => {
    const hw = host.clientWidth, hh = host.clientHeight;
    if (!hw || !hh) return;
    p.style.left = Math.min(Math.max(0, p.offsetLeft), Math.max(0, hw - 60)) + "px";
    p.style.top = Math.min(Math.max(0, p.offsetTop), Math.max(0, hh - 26)) + "px";
    p.style.right = "auto"; p.style.bottom = "auto";
  };
  const draggablePanel = (panel, key) => {
    const saved = panelPos[key];
    if (saved) requestAnimationFrame(() => {
      panel.style.left = saved.l + "px"; panel.style.top = saved.t + "px";
      panel.style.right = "auto"; clampPanel(panel);
    });
    panel.addEventListener("pointerdown", (e) => {
      const h3 = e.target.closest("h3");
      if (!h3 || h3.parentElement !== panel) return;
      e.preventDefault();
      const r = panel.getBoundingClientRect(), hr = host.getBoundingClientRect();
      const dx = e.clientX - r.left, dy = e.clientY - r.top;
      const move = (ev) => {
        panel.style.left = (ev.clientX - hr.left - dx) + "px";
        panel.style.top = (ev.clientY - hr.top - dy) + "px";
        panel.style.right = "auto"; panel.style.bottom = "auto";
      };
      const up = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        clampPanel(panel);
        panelPos[key] = { l: panel.offsetLeft, t: panel.offsetTop };
        savePanelPos();
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
    });
    panel.addEventListener("dblclick", (e) => {
      const h3 = e.target.closest("h3");
      if (!h3 || h3.parentElement !== panel) return;
      delete panelPos[key]; savePanelPos();
      panel.style.left = panel.style.top = panel.style.right = panel.style.bottom = "";
    });
  };
  draggablePanel(overlays, "overlays");
  draggablePanel(layersPanel, "layers");
  draggablePanel(findPanel, "find");
  draggablePanel(selPanel, "sel");
  // Find's CSS default (top 150px) predates the taller Overlays panel; when the
  // user hasn't placed it, flow it just below Overlays instead of overlapping
  if (!panelPos.find) requestAnimationFrame(() => {
    if (findPanel.isConnected) findPanel.style.top = (overlays.offsetTop + overlays.offsetHeight + 10) + "px";
  });
  new ResizeObserver(() => {
    for (const p of [overlays, layersPanel, findPanel, selPanel]) if (p.style.left) clampPanel(p);
  }).observe(host);

  const vp = new Viewport(host, {
    onSelect: (sel) => {
      if (!sel) { selPanel.style.display = "none"; return; }
      selPanel.style.display = "";
      selPanel.innerHTML = "";
      const ex = sel.extras || {};
      const quad = [ex.territoryId, ex.lgbFile, ex.layerId, ex.instanceId];
      const K = (t) => el("div", { class: "k" }, t);
      const V = (t, path) => el("div", { class: "v" + (path ? " path" : "") }, t);
      const body = [K("node"), V(sel.name || "(unnamed)")];
      if (quad.every(v => v !== undefined))
        body.push(K("identity (TerritoryId · LgbFile · LayerId · InstanceId)"),
          el("div", { class: "v accent" }, quad.join(" · ")));
      if (ex.assetPath) body.push(K(ex.water ? "model (water surface)" : "model"), V(ex.assetPath, true));
      if (ex.sgbPath) body.push(K(ex.sgbState ? `via sgb · state ${ex.sgbState}` : "via sgb"), V(ex.sgbPath, true));
      if (ex.collider) body.push(K("collider group"), V(ex.collider, true));
      if (ex.pcbPath) body.push(K("pcb"), V(ex.pcbPath, true));
      if (!ex.assetPath && !ex.collider && !quad.every(v => v !== undefined) && Object.keys(ex).length)
        body.push(K("extras"), V(JSON.stringify(ex)));
      body.push(K("point"), V([sel.point.x, sel.point.y, sel.point.z].map(n => n.toFixed(2)).join(", ")));
      const copyPath = ex.assetPath || ex.pcbPath || ex.sgbPath;
      body.push(el("div", { style: "margin-top:8px; display:flex; gap:6px; flex-wrap:wrap" },
        el("button", { class: "btn", onclick: () => sel.focus() }, "Focus"),
        quad.every(v => v !== undefined) ? el("button", { class: "btn", onclick: () => {
          navigator.clipboard.writeText(quad.join(","));
          toast("Identity copied");
        } }, "Copy identity") : null,
        copyPath ? el("button", { class: "btn", onclick: () => {
          navigator.clipboard.writeText(copyPath);
          toast("Path copied");
        } }, "Copy path") : null));
      selPanel.append(el("h3", {}, "Selection"), el("div", { class: "body" }, ...body));
    },
  });
  activeVp = vp;

  // overlay toggles
  let mapOn = true, colOn = false, colLoaded = false;
  // textured is the default view; the toggle is remembered across sessions
  let texOn = localStorage.getItem("atlas.textured") !== "0";
  let skyOn = localStorage.getItem("atlas.sky") !== "0";
  let waterOn = localStorage.getItem("atlas.water") !== "0";
  const mapChk = el("input", { type: "checkbox", checked: "" });
  const colChk = el("input", { type: "checkbox" });
  const texChk = el("input", { type: "checkbox" });
  const skyChk = el("input", { type: "checkbox" });
  const waterChk = el("input", { type: "checkbox" });
  texChk.checked = texOn;
  skyChk.checked = skyOn;
  waterChk.checked = waterOn;
  overlays.append(
    el("h3", {}, "Overlays"),
    el("label", { class: "vp-row" }, mapChk, el("span", { class: "n" }, "Map visual (glTF)")),
    el("label", { class: "vp-row", title: `Compose with diffuse textures (map-${tt}-tex.gltf; first compose exports the PNGs, cached afterwards)` },
      texChk, el("span", { class: "n" }, "Textured")),
    el("label", { class: "vp-row", title: "In-model water surfaces (harbors, rivers, terrain oceans) as translucent planes" },
      waterChk, el("span", { class: "n" }, "Water")),
    el("label", { class: "vp-row", title: "Gradient sky dome + horizon fog" },
      skyChk, el("span", { class: "n" }, "Sky")),
    el("label", { class: "vp-row" }, colChk, el("span", { class: "n" }, "Collision (OBJ)")),
  );
  vp.setSkyVisible(skyOn);
  skyChk.addEventListener("change", () => {
    skyOn = skyChk.checked; localStorage.setItem("atlas.sky", skyOn ? "1" : "0");
    vp.setSkyVisible(skyOn);
  });
  waterChk.addEventListener("change", () => {
    waterOn = waterChk.checked; localStorage.setItem("atlas.water", waterOn ? "1" : "0");
    vp.setWaterVisible(waterOn);
  });
  texChk.addEventListener("change", async () => {
    const want = texChk.checked;
    texChk.disabled = true;
    const l = el("div", { class: "vp-loading" }, el("div", { class: "spinner" }),
      el("div", {}, want ? "Composing textured map…" : "Loading map…"),
      el("div", { class: "sub" }, want ? "first compose exports diffuse PNGs; cached afterwards" : ""));
    host.append(l);
    try { await loadMap(want); texOn = want; localStorage.setItem("atlas.textured", want ? "1" : "0"); }
    catch (e) {
      toast("Textured map failed: " + e.message, "err");
      texChk.checked = texOn;
      try { await loadMap(texOn); } catch { /* keep whatever renders */ }
    }
    l.remove(); texChk.disabled = false;
  });
  mapChk.addEventListener("change", () => { mapOn = mapChk.checked; vp.setMapVisible(mapOn); });
  colChk.addEventListener("change", async () => {
    colOn = colChk.checked;
    if (colOn && !colLoaded) {
      colChk.disabled = true;
      const l = el("div", { class: "vp-loading" }, el("div", { class: "spinner" }), el("div", {}, "Loading collision mesh…"), el("div", { class: "sub" }, "first build walks every .pcb; cached afterwards"));
      host.append(l);
      try { await vp.loadCollisionObj(api.collisionObjUrl(tt)); colLoaded = true; }
      catch (e) { toast("Collision load failed: " + e.message, "err"); colChk.checked = colOn = false; }
      l.remove(); colChk.disabled = false;
    }
    vp.setCollisionVisible(colOn);
  });

  // ---- find assets (usage inventory: bg/sgb/vfx/sound placements) ----
  const findHint = "search placements by asset path (e.g. tre, rock, _towe)";
  const findInput = el("input", { type: "text", placeholder: "Find assets\u2026", autocomplete: "off", spellcheck: "false" });
  const findCount = el("div", { class: "list-count" }, findHint);
  const findRows = el("div", { class: "rows" });
  findPanel.append(el("h3", {}, "Find"), el("div", { class: "vp-findbox" }, findInput), findCount, findRows);
  let findSeq = 0;
  const runFind = async () => {
    const q = findInput.value.trim();
    const seq = ++findSeq;
    findRows.innerHTML = "";
    if (q.length < 2) { findCount.textContent = findHint; return; }
    findCount.textContent = "searching\u2026 (first search walks all 7 lgbs; cached afterwards)";
    let res;
    try { res = await api.usages({ q, tt, limit: 500 }); }
    catch (e) { if (seq === findSeq) findCount.textContent = "search failed: " + e.message; return; }
    if (seq !== findSeq) return;
    const rows = res.rows || [];
    findCount.textContent = rows.length
      ? `${rows.length}${res.total > rows.length ? " / " + res.total : ""} placements \u2014 click to fly`
      : "no placements match";
    const frag = document.createDocumentFragment();
    for (const r of rows) {
      frag.append(el("div", {
        class: "vp-hit",
        title: `${r.asset}${r.via ? "\nvia " + r.via : ""}\n${r.lgbFile} \u00b7 layer ${r.layerId} \u00b7 instance ${r.instanceId}\n${r.x.toFixed(2)}, ${r.y.toFixed(2)}, ${r.z.toFixed(2)}`,
        onclick: () => vp.flyTo(r.x, r.y, r.z),
      },
        el("span", { class: "n" }, (r.asset.split("/").pop() || r.asset) + (r.via ? " \u2299" : "")),
        el("span", { class: "c" }, `${r.x.toFixed(0)},${r.z.toFixed(0)}`)));
    }
    findRows.append(frag);
  };
  findInput.addEventListener("input", debounce(runFind, 300));

  // map glTF loading. The final load goes through the file route so the glTF's
  // relative URIs (.bin, tex/*.png) resolve next to it — but ensureComposed()
  // ALWAYS hits the map.gltf route first, because that route owns the format-
  // version check (.gen sidecar) and recomposes stale caches. Skipping it when
  // the file already existed is how pre-terrain maps got served forever (958).
  // force=true (Refresh button) passes refresh=1 for an unconditional recompose.
  async function ensureComposed(textured, force) {
    const url = api.mapGltfUrl(tt, textured, force);
    let r = null;
    try { r = await fetch(url, { method: "HEAD" }); } catch { /* fall through to GET */ }
    if (!r || r.status === 405) { r = await fetch(url); try { r.body?.cancel(); } catch { /* drained */ } }
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
  }

  let mapLoaded = false; // after the first load, later swaps keep the camera
  let stagesPromise = null; // /api/territory/{tt}/stages, fetched once per workspace (null on 404)
  async function loadMap(textured, force = false) {
    const gltfName = textured ? `map-${tt}-tex.gltf` : `map-${tt}.gltf`;
    await ensureComposed(textured, force);
    if (!ws.files.includes(gltfName)) ws.files.push(gltfName);
    const layers = await vp.loadMapGltf(api.territoryFileUrl(tt, gltfName), undefined, { fit: !mapLoaded });
    if (!waterOn) vp.setWaterVisible(false);
    mapLoaded = true;
    vp.setMapVisible(mapOn);
    buildLayersPanel(layers);
  }

  function buildLayersPanel(layers) {
    if (!layers.length) { layersPanel.style.display = "none"; return; }
    layersPanel.style.display = "";
    layersPanel.innerHTML = "";
    // Stage inspector: many zones ship several stages of the same map as
    // separate LGB layers (Doman Enclave rebuild, festival dressing). Badges
    // come from compose extras (festival id.phase, layer-set ids, temporary);
    // "solo" isolates one layer plus terrain to reconstruct a single stage.
    const disp = (ly) => ly.meta?.layer || ly.name.replace(/^layer-\d+-/, "");
    layers.sort((x, y) => ((y.meta?.terrain ? 1 : 0) - (x.meta?.terrain ? 1 : 0)) || disp(x).localeCompare(disp(y)));
    const nFest = layers.filter(l => l.meta?.festivalId > 0).length;
    const nSets = layers.filter(l => l.meta?.layerSets?.length).length;
    const nTmp = layers.filter(l => l.meta?.temporary).length;
    const flags = [nFest && `${nFest} festival`, nSets && `${nSets} set-flagged`, nTmp && `${nTmp} temporary`].filter(Boolean).join(" \u00b7 ");
    layersPanel.append(el("h3", {}, `Layers \u2014 ${layers.length}`));
    if (flags) layersPanel.append(el("div", { class: "sub", style: "margin:-2px 0 4px" }, flags));
    const rows = el("div", { class: "rows" });
    const allChk = el("input", { type: "checkbox", checked: "" });
    rows.append(el("label", { class: "vp-row" }, allChk, el("span", { class: "n", style: "font-weight:600" }, "all layers")));
    const checks = layers.map(ly => {
      const c = el("input", { type: "checkbox", checked: "" });
      c.addEventListener("change", () => ly.setVisible(c.checked));
      const m = ly.meta || {};
      const badges = [];
      if (m.terrain) badges.push("base");
      if (m.festivalId > 0) badges.push(`f${m.festivalId}${m.festivalPhase ? "." + m.festivalPhase : ""}`);
      if (m.layerSets?.length) badges.push(`set ${m.layerSets.join(",")}`);
      if (m.temporary) badges.push("tmp");
      if (m.housing) badges.push("housing");
      const solo = el("button", { class: "vp-solo", title: "show only this layer (plus terrain)" }, "solo");
      solo.addEventListener("click", (e) => {
        e.preventDefault();
        checks.forEach(([cc, ll]) => { cc.checked = ll === ly || !!ll.meta?.terrain; ll.setVisible(cc.checked); });
        allChk.checked = false;
      });
      rows.append(el("label", { class: "vp-row" }, c,
        el("span", { class: "n", title: `${disp(ly)} \u00b7 layerId ${m.layerId ?? "?"} \u00b7 ${ly.count} instances` }, disp(ly)),
        ...badges.map(b => el("span", { class: "vp-badge" }, b)),
        el("span", { class: "c" }, ly.count), solo));
      return [c, ly];
    });
    allChk.addEventListener("change", () => checks.forEach(([c, ly]) => { c.checked = allChk.checked; ly.setVisible(allChk.checked); }));
    layersPanel.append(rows);
    buildStagesSection(rows, checks, allChk);
  }

  // Stages: quest-progression compositions. Two data sources, both optional:
  // - /api/territory/{tt}/stages (library layer-sets/filters CSVs): the LVB
  //   filter keys; picking one evaluates each layer's FilterOp/FilterKeys
  //   (visible iff None, or Match && key in keys, or NoMatch && key not in).
  // - web/js/stages.js curated presets (e.g. 759 ruined/finished, where the
  //   rebuild is not key-gated): flip only the listed LayerIds, leave the
  //   rest untouched. Ids absent from the compose are silently skipped.
  async function buildStagesSection(rows, checks, allChk) {
    stagesPromise ??= api.stages(tt).catch(() => null);
    const sd = await stagesPromise;
    if (!rows.isConnected) return; // panel rebuilt (variant swap) while fetching
    const curated = STAGE_PRESETS[tt];
    const keys = sd?.keys?.length ? sd.keys : null;
    if (!keys && !curated) return;
    const syncAll = () => { allChk.checked = checks.every(([c]) => c.checked); };
    const apply = (fn) => {
      checks.forEach(([c, ly]) => {
        const v = fn(ly);
        if (v == null) return;
        c.checked = v; ly.setVisible(v);
      });
      syncAll();
    };
    layersPanel.append(el("h3", {}, "Stages"));
    // keys that gate no composed layer (e.g. 759's populace-only 138739)
    // would render a dead dropdown; say so instead of offering it
    const composedIds = new Set(checks.map(([, ly]) => ly.meta?.layerId).filter(id => id != null));
    const live = !!keys && (sd.filters || []).some(f =>
      (f.op === "Match" || f.op === "NoMatch") && composedIds.has(f.layerId));
    if (keys && !live)
      layersPanel.append(el("div", { class: "sub", style: "margin:-2px 0 2px" },
        `key${keys.length > 1 ? "s" : ""} ${keys.map(k => k.key).join(", ")} gate${keys.length > 1 ? "" : "s"} no composed geometry (populace/planevent only)`));
    let sel = null;
    if (keys && live) {
      const filterById = new Map((sd.filters || []).map(f => [f.layerId, f]));
      sel = el("select", { style: "width:100%;margin:2px 0" },
        el("option", { value: "" }, "(no filter \u2014 all layers)"),
        ...keys.map(k => {
          const lbl = STAGE_KEY_LABELS[tt]?.[k.key];
          return el("option", { value: String(k.key) },
            `key ${k.key} \u00b7 idx ${k.index}` +
            (k.territoryTypeId !== tt ? ` \u00b7 tt ${k.territoryTypeId}` : "") +
            (lbl ? ` \u00b7 ${lbl}` : ""));
        }));
      const applyKey = () => {
        if (sel.value === "") { apply(() => true); return; }
        const K = Number(sel.value);
        apply(ly => {
          if (ly.meta?.terrain) return true;
          const f = filterById.get(ly.meta?.layerId);
          if (!f) return true;
          if (f.op === "Match") return f.keys.includes(K);
          if (f.op === "NoMatch") return !f.keys.includes(K);
          return true; // None or unknown op
        });
      };
      sel.addEventListener("change", applyKey);
      const step = (d) => {
        const n = sel.options.length;
        sel.selectedIndex = (sel.selectedIndex + d + n) % n;
        applyKey();
      };
      const prev = el("button", { class: "vp-stagebtn", title: "previous composition" }, "\u25c0");
      const next = el("button", { class: "vp-stagebtn", title: "next composition" }, "\u25b6");
      prev.addEventListener("click", (e) => { e.preventDefault(); step(-1); });
      next.addEventListener("click", (e) => { e.preventDefault(); step(1); });
      layersPanel.append(el("div", { class: "sub", style: "margin:-2px 0 2px" },
        `${keys.length} keyed composition${keys.length > 1 ? "s" : ""} (library layer-sets)`));
      layersPanel.append(el("div", { style: "display:flex;gap:4px;align-items:center" }, prev, sel, next));
    }
    if (curated) {
      const box = el("div", { style: "display:flex;gap:4px;flex-wrap:wrap;margin:4px 0" });
      for (const p of curated.presets) {
        const b = el("button", { class: "vp-stagebtn", title: p.note || "" }, p.name);
        b.addEventListener("click", (e) => {
          e.preventDefault();
          const show = new Set(p.show), hide = new Set(p.hide);
          apply(ly => {
            const id = ly.meta?.layerId;
            return show.has(id) ? true : hide.has(id) ? false : null;
          });
          if (p.resetStates) vp.applyStates(null, null, true);
          if (p.states) vp.applyStates(new Set(p.states.show), new Set(p.states.hide));
          if (!waterOn) vp.setWaterVisible(false);   // state re-show must not resurrect hidden water
          if (sel) sel.value = "";
        });
        box.append(b);
      }
      layersPanel.append(el("div", { class: "sub", style: "margin:2px 0 0" }, "curated presets"), box);
    }
  }

  loading.querySelector("div:nth-child(2)").textContent = texOn ? "Composing textured map…" : "Composing map glTF…";
  loading.querySelector(".sub").textContent = texOn
    ? "first compose exports diffuse PNGs; cached afterwards"
    : "bg.lgb → one scene; first compose takes ~30 s, cached afterwards";
  try {
    await loadMap(texOn, refresh);
    loading.remove();
  } catch (e) {
    let ok = false;
    if (texOn) {
      // graceful degrade: textured compose failed; try untextured before giving up
      texChk.checked = texOn = false;
      toast("Textured map failed (" + e.message + "); loading untextured", "err");
      try { await loadMap(false, refresh); ok = true; } catch { /* fall through */ }
    }
    loading.remove();
    if (!ok) {
      toast("Map compose/load failed: " + e.message, "err");
      // still usable with collision only
      colChk.checked = true;
      colChk.dispatchEvent(new Event("change"));
    }
  }

  // stats
  const statTick = () => {
    if (activeVp !== vp) return;
    const s = vp.stats();
    statbar.textContent = `draws ${s.calls} · tris ${(s.tris / 1e6).toFixed(2)}M`;
    setTimeout(statTick, 1000);
  };
  statTick();
}
