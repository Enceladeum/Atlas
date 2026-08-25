// Atlas API client. All data comes from Atlas.Server; no filesystem assumptions.
const BASE = "";

async function jget(url) {
  const r = await fetch(BASE + url);
  if (!r.ok) throw new Error(`${r.status} ${r.statusText} — ${url}`);
  return r.json();
}
async function tget(url) {
  const r = await fetch(BASE + url);
  if (!r.ok) throw new Error(`${r.status} ${r.statusText} — ${url}`);
  return r.text();
}

export const api = {
  meta: () => jget("/api/meta"),
  sheets: (filter) => jget("/api/sheets" + (filter ? `?filter=${encodeURIComponent(filter)}` : "")),
  sheetHeader: (name) => tget(`/api/sheet/${encodeURIComponent(name)}/header`),
  sheetCsv: (name, { lang, max } = {}) => {
    const q = new URLSearchParams();
    if (lang) q.set("lang", lang);
    if (max) q.set("max", max);
    const qs = q.toString();
    return tget(`/api/sheet/${encodeURIComponent(name)}.csv` + (qs ? "?" + qs : ""));
  },
  sheetLinks: (name) => jget(`/api/sheet/${encodeURIComponent(name)}/links`),
  sheetCsvUrl: (name, lang) =>
    `${BASE}/api/sheet/${encodeURIComponent(name)}.csv` + (lang ? `?lang=${lang}` : ""),
  paths: ({ prefix, q, limit } = {}) => {
    const p = new URLSearchParams();
    if (prefix) p.set("prefix", prefix);
    if (q) p.set("q", q);
    if (limit) p.set("limit", limit);
    return jget("/api/paths?" + p.toString());
  },
  exists: (path) => jget(`/api/exists?path=${encodeURIComponent(path)}`),
  extractUrl: (path) => `${BASE}/api/extract?path=${encodeURIComponent(path)}`,
  mdlInfo: (path) => jget(`/api/mdl/info?path=${encodeURIComponent(path)}`),
  mdlObjUrl: (path, lod = 0) => `${BASE}/api/mdl/obj?path=${encodeURIComponent(path)}&lod=${lod}`,
  mdlGltfUrl: (path, lod = 0, textured = false) =>
    `${BASE}/api/mdl/gltf?path=${encodeURIComponent(path)}&lod=${lod}` + (textured ? "&textured=1" : ""),
  mtrl: (path) => jget(`/api/mtrl?path=${encodeURIComponent(path)}`),
  charaImc: (path) => jget(`/api/chara/imc?path=${encodeURIComponent(path)}`),
  charaResolve: (path, variant) =>
    jget(`/api/chara/resolve?path=${encodeURIComponent(path)}` + (variant != null ? `&variant=${variant}` : "")),
  texInfo: (path) => jget(`/api/tex/info?path=${encodeURIComponent(path)}`),
  texPngUrl: (path, mip = 0) => `${BASE}/api/tex/png?path=${encodeURIComponent(path)}&mip=${mip}`,
  scdInfo: (path) => jget(`/api/scd/info?path=${encodeURIComponent(path)}`),
  scdAudioUrl: (path, entry = 0) => `${BASE}/api/scd/audio?path=${encodeURIComponent(path)}&entry=${entry}`,
  avfxInfo: (path) => jget(`/api/avfx/info?path=${encodeURIComponent(path)}`),
  avfxGltfUrl: (path) => `${BASE}/api/avfx/gltf?path=${encodeURIComponent(path)}`,
  territory: (tt, refresh) => jget(`/api/territory/${tt}` + (refresh ? "?refresh=1" : "")),
  territoryFileUrl: (tt, name) => `${BASE}/api/territory/${tt}/file/${encodeURIComponent(name)}`,
  territoryFile: (tt, name) => tget(`/api/territory/${tt}/file/${encodeURIComponent(name)}`),
  collisionObjUrl: (tt) => `${BASE}/api/territory/${tt}/collision.obj`,
  mapGltfUrl: (tt, textured) => `${BASE}/api/territory/${tt}/map.gltf${textured ? "?textured=1" : ""}`,
  usages: ({ q, tt, limit } = {}) => {
    const p = new URLSearchParams();
    if (q) p.set("q", q);
    if (tt) p.set("tt", tt);
    if (limit) p.set("limit", limit);
    const qs = p.toString();
    return jget("/api/usages" + (qs ? "?" + qs : ""));
  },
  usagesStatus: () => jget("/api/usages/status"),
  usagesBuild: async () => {
    const r = await fetch(BASE + "/api/usages/build", { method: "POST" });
    if (!r.ok) throw new Error(`${r.status} ${r.statusText} — /api/usages/build`);
    return r.json();
  },
  deps: (path) => jget(`/api/deps?path=${encodeURIComponent(path)}`),
  depsStatus: () => jget("/api/deps/status"),
  depsBuild: async () => {
    const r = await fetch(BASE + "/api/deps/build", { method: "POST" });
    if (!r.ok) throw new Error(`${r.status} ${r.statusText} — /api/deps/build`);
    return r.json();
  },
};
