// Asset browser: drill through the ResLogger2 path index, preview tex/mdl/mtrl,
// export everything. Headless parity: atlas extract / mod Mdl / mod Mtrl / mod Tex.
import { api } from "../api.js";
import { el, debounce, toast, spinner, crumbs } from "../ui.js";
import { Viewport } from "../viewport.js";

let activeVp = null;

async function paths(params) {
  const q = new URLSearchParams();
  if (params.prefix) q.set("prefix", params.prefix);
  if (params.q) q.set("q", params.q);
  if (params.limit) q.set("limit", params.limit);
  const r = await fetch("/api/paths?" + q);
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText);
  return r.json();
}

export async function renderAssets(view, params) {
  crumbs("Assets");
  if (activeVp) { activeVp.dispose(); activeVp = null; }
  view.innerHTML = "";
  const split = el("div", { class: "pane-split" });
  const left = el("div", { class: "pane-left", style: "width:340px" });
  const main = el("div", { class: "pane-main" });
  split.append(left, main);
  view.append(split);

  const path = params.path || "";
  const isDir = path === "" || path.endsWith("/");
  const browsePrefix = isDir ? path : path.slice(0, path.lastIndexOf("/") + 1);

  // ---- left: search + breadcrumb + listing ----
  const sb = el("div", { class: "searchbox" });
  const input = el("input", { type: "text", placeholder: "Search all paths (substring)…", autocomplete: "off", spellcheck: "false" });
  sb.append(input);
  const crumbBar = el("div", { class: "asset-crumbs" });
  const count = el("div", { class: "list-count" });
  const list = el("div", { class: "list" });
  left.append(sb, crumbBar, count, list);

  const renderCrumbs = (prefix) => {
    crumbBar.innerHTML = "";
    crumbBar.append(el("a", { onclick: () => { location.hash = "#/assets"; } }, "sqpack"));
    let acc = "";
    for (const seg of prefix.split("/").filter(Boolean)) {
      acc += seg + "/";
      const target = acc;
      crumbBar.append(el("span", { class: "sep" }, "/"),
        el("a", { onclick: () => { location.hash = "#/assets/" + encodeURIComponent(target); } }, seg));
    }
  };

  const renderListing = async (prefix) => {
    renderCrumbs(prefix);
    list.innerHTML = "";
    list.append(spinner("Listing…"));
    try {
      const r = await paths({ prefix, limit: 500 });
      list.innerHTML = "";
      count.textContent = `${r.dirs.length} folders · ${r.files.length} files`;
      for (const d of r.dirs) {
        list.append(el("div", {
          class: "list-item dir",
          onclick: () => { location.hash = "#/assets/" + encodeURIComponent(prefix + d.name + "/"); },
        }, d.name + "/", el("span", { class: "cnt" }, d.count)));
      }
      for (const f of r.files) {
        const base = f.slice(f.lastIndexOf("/") + 1);
        list.append(el("div", {
          class: "list-item" + (f === path ? " active" : ""), title: f,
          onclick: () => { location.hash = "#/assets/" + encodeURIComponent(f); },
        }, base));
      }
      queueMicrotask(() => list.querySelector(".active")?.scrollIntoView({ block: "center" }));
    } catch (e) {
      list.innerHTML = "";
      list.append(el("div", { class: "empty" },
        el("div", {}, "Path index unavailable"),
        el("div", { class: "hint" }, e.message)));
      count.textContent = "";
    }
  };

  const renderSearch = async (q) => {
    crumbBar.innerHTML = "";
    list.innerHTML = "";
    list.append(spinner("Searching 1.8M paths…"));
    try {
      const r = await paths({ q, limit: 300 });
      list.innerHTML = "";
      count.textContent = `${r.hits.length}${r.truncated ? "+" : ""} matches`;
      for (const f of r.hits) {
        list.append(el("div", {
          class: "list-item", title: f,
          onclick: () => { location.hash = "#/assets/" + encodeURIComponent(f); },
        }, f));
      }
    } catch (e) { list.innerHTML = ""; toast("Search failed: " + e.message, "err"); }
  };

  input.addEventListener("input", debounce(() => {
    const q = input.value.trim();
    q ? renderSearch(q) : renderListing(browsePrefix);
  }, 250));
  renderListing(browsePrefix);

  // ---- main: preview ----
  if (isDir) {
    main.append(el("div", { class: "empty" },
      el("div", {}, path ? path : "Browse the game archive"),
      el("div", { class: "hint" }, "1.82M crowdsourced paths (ResLogger2) — pick a file to preview")));
    return;
  }
  await renderPreview(main, path);
}

async function renderPreview(main, path) {
  crumbs("Assets", path.slice(path.lastIndexOf("/") + 1));
  main.innerHTML = "";
  const card = el("div", { class: "preview-card", style: "flex:1; min-height:0" });
  main.append(card);
  const base = path.slice(path.lastIndexOf("/") + 1);
  const ext = base.slice(base.lastIndexOf(".") + 1).toLowerCase();
  card.append(el("h2", {}, base), el("div", { class: "sub" }, path));
  const body = el("div");
  card.append(body);
  body.append(spinner("Loading…"));

  try {
    if (ext === "tex") await previewTex(body, path);
    else if (ext === "mdl") await previewMdl(body, path);
    else if (ext === "mtrl") await previewMtrl(body, path);
    else if (ext === "scd") await previewScd(body, path);
    else if (ext === "avfx") await previewAvfx(body, path);
    else await previewRaw(body, path);
  } catch (e) {
    body.innerHTML = "";
    body.append(el("div", { class: "empty", style: "padding:30px 0" },
      el("div", {}, "Preview failed"), el("div", { class: "hint" }, e.message)));
    body.append(el("div", { class: "toolbar-row" },
      el("a", { class: "btn", href: api.extractUrl(path), download: base }, "Extract raw")));
  }
}

const link = (p) => el("a", { onclick: () => { location.hash = "#/assets/" + encodeURIComponent(p); } }, p);

// Shared "used by" pane backed by the dependency index (mdl→mtrl→tex, both
// directions; built from the ResLogger2 path list). dirKind picks the copy.
async function depsPane(body, path, dirKind, kindLabel) {
  const wrap = el("div", {});
  body.append(wrap);
  const render = async () => {
    if (!wrap.isConnected) return;
    wrap.innerHTML = "";
    let res;
    try { res = await api.deps(path); } catch { return; }  // route unavailable; stay quiet
    if (!wrap.isConnected) return;
    wrap.append(el("div", { class: "section-h" }, `Used by ${kindLabel}`));
    if (!res.indexed) {
      let st = null;
      try { st = await api.depsStatus(); } catch { }
      if (st && !st.hasPaths) {
        wrap.append(el("div", { class: "hint" },
          "needs the ResLogger2 path list — set pathsFile in settings.json (or --paths/ATLAS_PATHS) and restart"));
        return;
      }
      const note = el("div", { class: "hint" },
        res.building
          ? "Building the dependency index…"
          : `Build the dependency index to see every ${kindLabel.replace(/s$/, "")} that uses this ${dirKind === "mtrl" ? "material" : "texture"} (one sweep of all known mdl+mtrl paths; a few minutes, persists in the work dir).`);
      const btn = el("button", { class: "btn", disabled: res.building ? "" : undefined, onclick: async () => {
        btn.disabled = true; note.textContent = "Building the dependency index…";
        try { await api.depsBuild(); } catch (e) { toast("Index build failed to start: " + e.message, "err"); btn.disabled = false; return; }
        poll();
      } }, res.building ? "Building…" : "Build dependency index");
      const prog = el("span", { class: "hint", style: "margin-left:8px" });
      const poll = async () => {
        if (!wrap.isConnected) return;
        let s;
        try { s = await api.depsStatus(); } catch { return; }
        if (s.building) {
          prog.textContent = `${s.files} files · ${s.edges} edges`;
          setTimeout(poll, 2000);
        } else if (s.error) {
          toast("Deps index build failed: " + s.error, "err");
          btn.disabled = false; btn.textContent = "Build dependency index";
        } else { render(); }
      };
      if (res.building) poll();
      wrap.append(note, el("div", { style: "margin-top:6px" }, btn, prog));
      return;
    }
    if (res.stale) {
      let sv = null;
      try { sv = await api.depsStatus(); } catch { /* banner still useful without versions */ }
      if (!wrap.isConnected) return;
      const lbl = sv && sv.indexVersion && sv.gameVersion
        ? `index built for ${sv.indexVersion}, game is ${sv.gameVersion}`
        : "index predates the current game version";
      const prog = el("span", { class: "hint", style: "margin-left:8px" });
      const rb = el("button", { class: "btn", style: "margin-left:8px", onclick: async () => {
        rb.disabled = true; rb.textContent = "Rebuilding…";
        try { await api.depsBuild(); } catch (e) { toast("Rebuild failed to start: " + e.message, "err"); rb.disabled = false; rb.textContent = "Rebuild"; return; }
        const poll = async () => {
          if (!wrap.isConnected) return;
          let st;
          try { st = await api.depsStatus(); } catch { return; }
          if (st.building) { prog.textContent = `${st.files} files · ${st.edges} edges`; setTimeout(poll, 2000); }
          else if (st.error) { toast("Rebuild failed: " + st.error, "err"); rb.disabled = false; rb.textContent = "Rebuild"; }
          else render();
        };
        poll();
      } }, "Rebuild");
      wrap.append(el("div", { class: "hint", style: "color:#d7a557" }, "⚠ " + lbl, rb, prog));
    }
    const rows = res.usedBy || [];
    const h = wrap.querySelector(".section-h");
    h.textContent = `Used by ${rows.length} ${rows.length === 1 ? kindLabel.replace(/s$/, "") : kindLabel}`;
    if (!rows.length) {
      wrap.append(el("div", { class: "hint" }, "nothing in the index references this file"));
      return;
    }
    const t = el("table", { class: "mini" });
    for (const r of rows.slice(0, 200)) t.append(el("tr", {}, el("td", {}, link(r))));
    wrap.append(t);
    if (rows.length > 200)
      wrap.append(el("div", { class: "hint" }, `…and ${rows.length - 200} more`));
  };
  render();
}

async function previewTex(body, path) {
  const info = await api.texInfo(path);
  body.innerHTML = "";
  const mipSel = el("select", { class: "btn" });
  for (let m = 0; m < (info.mipCount || 1); m++) mipSel.append(el("option", { value: m }, `mip ${m}`));
  const img = el("img", { src: api.texPngUrl(path, 0), alt: path });
  mipSel.addEventListener("change", () => { img.src = api.texPngUrl(path, mipSel.value); });
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "format"), el("span", { class: "v" }, `${info.format} (0x${(info.formatValue ?? 0).toString(16)})`),
      el("span", { class: "k" }, "size"), el("span", { class: "v" }, `${info.width} × ${info.height}${info.depth > 1 ? " × " + info.depth : ""}`),
      el("span", { class: "k" }, "mips"), el("span", { class: "v" }, String(info.mipCount)),
      el("span", { class: "k" }, "kind"), el("span", { class: "v" }, info.isCube ? "cube" : info.isArray ? "array" : info.is3D ? "3D" : "2D"),
    ),
    el("div", { class: "toolbar-row" },
      mipSel,
      el("a", { class: "btn primary", href: api.texPngUrl(path, 0), download: path.replaceAll("/", "_") + ".png" }, "Export PNG"),
      el("a", { class: "btn", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract .tex"),
    ),
    el("div", { class: "tex-stage" }, img),
  );
  depsPane(body, path, "tex", "materials");
}

async function previewMdl(body, path) {
  const info = await api.mdlInfo(path);
  body.innerHTML = "";
  const lodSel = el("select", { class: "btn" });
  for (let l = 0; l < (info.lodCount || 1); l++) lodSel.append(el("option", { value: l }, `LOD ${l}`));
  const objBtn = el("a", { class: "btn primary", href: api.mdlObjUrl(path, 0), download: path.replaceAll("/", "_") + ".obj" }, "Export OBJ");
  const gltfBtn = el("a", { class: "btn", href: api.mdlGltfUrl(path, 0, true), download: path.replaceAll("/", "_") + ".gltf" }, "Export glTF");
  const texChk = el("input", { type: "checkbox" });
  texChk.checked = (localStorage.getItem("atlas.assets.textured") ?? "1") === "1";
  const sync = () => {
    objBtn.href = api.mdlObjUrl(path, lodSel.value);
    gltfBtn.href = api.mdlGltfUrl(path, lodSel.value, true);
  };
  const bb = info.bboxMin && info.bboxMax
    ? `(${fmt3(info.bboxMin)}) → (${fmt3(info.bboxMax)})` : "n/a";
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "LODs"), el("span", { class: "v" }, String(info.lodCount)),
      el("span", { class: "k" }, "bones"), el("span", { class: "v" }, String(info.boneCount)),
      el("span", { class: "k" }, "shapes"), el("span", { class: "v" }, String(info.shapeCount)),
      el("span", { class: "k" }, "bbox"), el("span", { class: "v" }, bb),
    ),
    el("div", { class: "toolbar-row" },
      lodSel,
      el("label", { class: "btn", title: "Preview with diffuse textures (self-contained glTF; textures embedded)" }, texChk, " Textured"),
      objBtn, gltfBtn,
      el("a", { class: "btn", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract .mdl"),
    ),
  );
  // 3D preview
  const host = el("div", { class: "mini-vp" });
  body.append(host);
  const vp = new Viewport(host);
  activeVp = vp;
  const reload = async () => {
    texChk.disabled = lodSel.disabled = true;
    const load = el("div", { class: "vp-loading" }, el("div", { class: "spinner" }),
      el("div", {}, texChk.checked ? "Composing textured preview…" : "Building preview…"));
    host.append(load);
    try {
      if (texChk.checked) await vp.loadMapGltf(api.mdlGltfUrl(path, lodSel.value, true));
      else await vp.loadObjShaded(api.mdlObjUrl(path, lodSel.value));
    } catch (e) { toast("3D preview failed: " + e.message, "err"); }
    load.remove();
    texChk.disabled = lodSel.disabled = false;
  };
  await reload();
  lodSel.addEventListener("change", () => { sync(); reload(); });
  texChk.addEventListener("change", () => {
    localStorage.setItem("atlas.assets.textured", texChk.checked ? "1" : "0");
    reload();
  });
  // ---- used in (global usage index) ----
  const usedWrap = el("div", {});
  body.append(usedWrap);
  const renderUsedIn = async () => {
    if (!usedWrap.isConnected) return;
    usedWrap.innerHTML = "";
    let res;
    try { res = await api.usages({ q: path, limit: 50000 }); }
    catch { return; }  // route unavailable; stay quiet
    if (!usedWrap.isConnected) return;
    usedWrap.append(el("div", { class: "section-h" }, "Used in"));
    if (!res.indexed) {
      const note = el("div", { class: "hint" },
        res.building
          ? "Building the usage index\u2026"
          : "Build the global usage index to see every territory that places this model (one sweep of all level dirs; takes well under a minute, persists in the work dir).");
      const btn = el("button", { class: "btn", disabled: res.building ? "" : undefined, onclick: async () => {
        btn.disabled = true; note.textContent = "Building the usage index\u2026";
        try { await api.usagesBuild(); } catch (e) { toast("Index build failed to start: " + e.message, "err"); btn.disabled = false; return; }
        poll();
      } }, res.building ? "Building\u2026" : "Build usage index");
      const prog = el("span", { class: "hint", style: "margin-left:8px" });
      const poll = async () => {
        if (!usedWrap.isConnected) return;
        let s;
        try { s = await api.usagesStatus(); } catch { return; }
        if (s.building) {
          prog.textContent = `${s.dirs} dirs \u00b7 ${s.rows} rows`;
          setTimeout(poll, 2000);
        } else if (s.error) {
          toast("Usage index build failed: " + s.error, "err");
          btn.disabled = false; btn.textContent = "Build usage index";
        } else { renderUsedIn(); }
      };
      if (res.building) poll();
      usedWrap.append(note, el("div", { style: "margin-top:6px" }, btn, prog));
      return;
    }
    if (res.stale) {
      let sv = null;
      try { sv = await api.usagesStatus(); } catch { /* banner still useful without versions */ }
      if (!usedWrap.isConnected) return;
      const lbl = sv && sv.indexVersion && sv.gameVersion
        ? `index built for ${sv.indexVersion}, game is ${sv.gameVersion}`
        : "index predates the current game version";
      const prog = el("span", { class: "hint", style: "margin-left:8px" });
      const rb = el("button", { class: "btn", style: "margin-left:8px", onclick: async () => {
        rb.disabled = true; rb.textContent = "Rebuilding…";
        try { await api.usagesBuild(); } catch (e) { toast("Rebuild failed to start: " + e.message, "err"); rb.disabled = false; rb.textContent = "Rebuild"; return; }
        const poll = async () => {
          if (!usedWrap.isConnected) return;
          let st;
          try { st = await api.usagesStatus(); } catch { return; }
          if (st.building) { prog.textContent = `${st.dirs} dirs · ${st.rows} rows`; setTimeout(poll, 2000); }
          else if (st.error) { toast("Rebuild failed: " + st.error, "err"); rb.disabled = false; rb.textContent = "Rebuild"; }
          else renderUsedIn();
        };
        poll();
      } }, "Rebuild");
      usedWrap.append(el("div", { class: "hint", style: "color:#d7a557" },
        "⚠ " + lbl, rb, prog));
    }
    const byLabel = new Map();
    for (const r of res.rows || []) {
      const g = byLabel.get(r.label) || { n: 0, via: 0 };
      g.n++; if (r.via) g.via++;
      byLabel.set(r.label, g);
    }
    if (!byLabel.size) {
      usedWrap.append(el("div", { class: "hint" }, "no placements found in any level dir"));
      return;
    }
    const h = usedWrap.querySelector(".section-h");
    h.textContent = `Used in \u2014 ${byLabel.size} ${byLabel.size === 1 ? "location" : "locations"}, ${res.total} placements`;
    const t = el("table", { class: "mini" });
    const ordered = [...byLabel.entries()].sort((a, b) => b[1].n - a[1].n);
    for (const [label, g] of ordered) {
      const ttId = label.startsWith("TT") ? label.slice(2) : null;
      t.append(el("tr", {},
        el("td", {}, ttId
          ? el("a", { href: `#/territory/${ttId}`, title: "open territory (use Find there to fly to placements)" }, label)
          : label),
        el("td", { style: "text-align:right; color:var(--fg2)" },
          `${g.n}\u00d7${g.via ? ` (${g.via} via sgb)` : ""}`)));
    }
    usedWrap.append(t);
  };
  renderUsedIn();

  // materials
  if (info.materialPaths?.length) {
    body.append(el("div", { class: "section-h" }, "Materials"));
    const t = el("table", { class: "mini" });
    for (const m of info.materialPaths) t.append(el("tr", {}, el("td", {}, link(m))));
    body.append(t);
  }
}

async function previewMtrl(body, path) {
  const d = await api.mtrl(path);
  body.innerHTML = "";
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "shader"), el("span", { class: "v" }, d.shaderPackage),
      el("span", { class: "k" }, "version"), el("span", { class: "v" }, String(d.version)),
    ),
    el("div", { class: "toolbar-row" },
      el("a", { class: "btn", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract .mtrl")),
  );
  if (d.textures?.length) {
    body.append(el("div", { class: "section-h" }, "Textures"));
    const t = el("table", { class: "mini" });
    t.append(el("tr", {}, el("th", {}, "#"), el("th", {}, "path"), el("th", {}, "samplers")));
    for (const tx of d.textures)
      t.append(el("tr", {}, el("td", {}, String(tx.index)), el("td", {}, link(tx.path)),
        el("td", {}, (tx.samplers || []).join(", "))));
    body.append(t);
  }
  if (d.constants?.length) {
    body.append(el("div", { class: "section-h" }, "Constants"));
    const t = el("table", { class: "mini" });
    t.append(el("tr", {}, el("th", {}, "name"), el("th", {}, "values")));
    for (const c of d.constants)
      t.append(el("tr", {}, el("td", {}, c.name || `0x${(c.id ?? 0).toString(16)}`),
        el("td", {}, (c.values || []).map(v => typeof v === "number" ? +v.toFixed(4) : v).join(", "))));
    body.append(t);
  }
  depsPane(body, path, "mtrl", "models");
}

async function previewScd(body, path) {
  const info = await api.scdInfo(path);
  body.innerHTML = "";
  const entries = info.entries || [];
  const playable = entries.filter(e => !e.error && e.format !== "Empty" && e.dataBytes > 0);
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "sounds"), el("span", { class: "v" }, String(info.soundCount)),
      el("span", { class: "k" }, "tracks"), el("span", { class: "v" }, String(info.trackCount)),
      el("span", { class: "k" }, "audio entries"), el("span", { class: "v" }, String(info.audioCount)),
    ),
    el("div", { class: "toolbar-row" },
      el("a", { class: "btn", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract .scd")),
  );
  if (!playable.length) {
    body.append(el("div", { class: "hint" }, "no playable audio entries in this scd"));
    return;
  }
  // one shared player; clicking an entry row loads it
  const audio = el("audio", { controls: "", style: "width:100%; margin-top:8px" });
  const fmtSecs = (s) => s == null ? "?" : (s < 10 ? s.toFixed(1) : Math.round(s)) + "s";
  const t = el("table", { class: "mini" });
  t.append(el("tr", {}, el("th", {}, "#"), el("th", {}, "format"), el("th", {}, "ch"),
    el("th", {}, "rate"), el("th", {}, "length"), el("th", {}, "loop"), el("th", {}, "")));
  let activeRow = null;
  const load = (e2, row, autoplay) => {
    if (activeRow) activeRow.classList.remove("active");
    (activeRow = row).classList.add("active");
    audio.src = api.scdAudioUrl(path, e2.index);
    if (autoplay) audio.play().catch(() => { /* autoplay policy; user presses play */ });
  };
  let firstRow = null;
  for (const e2 of entries) {
    if (e2.error) {
      t.append(el("tr", {}, el("td", {}, String(e2.index)),
        el("td", { colspan: "6", style: "color:var(--fg2)" }, "error: " + e2.error)));
      continue;
    }
    if (e2.format === "Empty") continue;
    const row = el("tr", { style: "cursor:pointer" });
    row.addEventListener("click", () => load(e2, row, true));
    row.append(
      el("td", {}, String(e2.index)),
      el("td", {}, e2.format),
      el("td", {}, String(e2.channels)),
      el("td", {}, `${e2.rate} Hz`),
      el("td", {}, fmtSecs(e2.seconds)),
      el("td", {}, e2.loopEnd > 0 ? `${e2.loopStart}..${e2.loopEnd}` : "—"),
      el("td", {}, el("a", { class: "btn", style: "padding:1px 8px",
        href: api.scdAudioUrl(path, e2.index),
        download: path.replaceAll("/", "_") + `.${e2.index}.${e2.format === "OggVorbis" ? "ogg" : "wav"}`,
        onclick: (ev) => ev.stopPropagation() }, "↓")),
    );
    if (!firstRow) { firstRow = row; firstRow._entry = e2; }
    t.append(row);
  }
  body.append(el("div", { class: "section-h" }, "Audio entries — click to play"), t, audio);
  if (firstRow) load(firstRow._entry, firstRow, false);
}

async function previewAvfx(body, path) {
  const info = await api.avfxInfo(path);
  body.innerHTML = "";
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "version"), el("span", { class: "v" }, "0x" + (info.version ?? 0).toString(16)),
      el("span", { class: "k" }, "schedulers"), el("span", { class: "v" }, String(info.schedulers)),
      el("span", { class: "k" }, "timelines"), el("span", { class: "v" }, String(info.timelines)),
      el("span", { class: "k" }, "emitters"), el("span", { class: "v" }, String(info.emitters)),
      el("span", { class: "k" }, "particles"), el("span", { class: "v" }, String(info.particles)),
      el("span", { class: "k" }, "effectors"), el("span", { class: "v" }, String(info.effectors)),
      el("span", { class: "k" }, "binders"), el("span", { class: "v" }, String(info.binders)),
    ),
    el("div", { class: "toolbar-row" },
      el("a", { class: "btn", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract .avfx")),
    el("div", { class: "hint" },
      "structural inspection — playback needs the game's particle engine"),
  );
  // embedded models: 3D preview via the existing glTF viewer
  const models = info.models || [];
  const drawable = models.filter(m => m.vertexCount > 0 && m.triCount > 0);
  body.append(el("div", { class: "section-h" },
    `Embedded models — ${models.length}${drawable.length !== models.length ? ` (${drawable.length} drawable)` : ""}`));
  if (models.length) {
    const t = el("table", { class: "mini" });
    for (const m of models)
      t.append(el("tr", {}, el("td", {}, `[${m.index}]`),
        el("td", {}, `${m.vertexCount} verts`), el("td", {}, `${m.triCount} tris`),
        el("td", {}, m.vertexCount === 0 && m.triCount === 0 ? "emit-only" : "")));
    body.append(t);
  } else {
    body.append(el("div", { class: "hint" }, "none"));
  }
  if (drawable.length) {
    body.append(el("div", { class: "toolbar-row" },
      el("a", { class: "btn", href: api.avfxGltfUrl(path), download: path.replaceAll("/", "_") + ".gltf" }, "Export glTF")));
    const host = el("div", { class: "mini-vp" });
    body.append(host);
    const vp = new Viewport(host);
    activeVp = vp;
    const load = el("div", { class: "vp-loading" }, el("div", { class: "spinner" }), el("div", {}, "Building preview…"));
    host.append(load);
    try { await vp.loadMapGltf(api.avfxGltfUrl(path)); }
    catch (e) { toast("3D preview failed: " + e.message, "err"); }
    load.remove();
  }
  // texture refs, with thumbnails through the existing tex png route
  const texes = info.textures || [];
  body.append(el("div", { class: "section-h" }, `Textures — ${texes.length}`));
  if (texes.length) {
    const t = el("table", { class: "mini" });
    for (const tx of texes) {
      const atex = tx.trim();
      t.append(el("tr", {},
        el("td", {}, el("img", { src: api.texPngUrl(atex, 0), loading: "lazy", style: "height:40px; max-width:120px; object-fit:contain; background:#222",
          onerror: (ev) => { ev.target.style.display = "none"; } })),
        el("td", {}, link(atex))));
    }
    body.append(t);
  } else {
    body.append(el("div", { class: "hint" }, "none"));
  }
}

async function previewRaw(body, path) {
  const ex = await api.exists(path);
  body.innerHTML = "";
  body.append(
    el("div", { class: "kv" },
      el("span", { class: "k" }, "exists"), el("span", { class: "v" }, ex.exists ? "yes" : "no — not in this game install"),
    ),
    ex.exists ? el("div", { class: "toolbar-row" },
      el("a", { class: "btn primary", href: api.extractUrl(path), download: path.slice(path.lastIndexOf("/") + 1) }, "Extract raw")) : null,
  );
}

const fmt3 = (v) => [v.x, v.y, v.z].map(n => +n.toFixed(2)).join(", ");
