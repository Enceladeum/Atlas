# Atlas

FFXIV game-data inspection, preview, and export toolkit: the consolidated xivtool
core, repackaged with a web GUI for interactive browsing — sheets, territories,
assets, maps — while every capability stays fully scriptable headless (CLI + HTTP
API).

**Provenance.** Forked 2026-08-24 from `xivtool/src` (the consolidation port of
the LgbDump/CutScan/LgbCheck lineage). The port is byte-identical to xivtool on
golden outputs (see CONTRACT.md, Acceptance). `xivtool` remains the tool of
record for the per-patch intake/diff pipeline; Atlas is the inspection/preview
surface and the home of the new asset modules (MDL/MTRL/TEX, glTF, composed maps).

## Layout

    Atlas.slnx
    CONTRACT.md          binding engineering contract — read before writing code
    VERIFY.md            what was verified where + 5-minute GUI walkthrough
    src/Atlas.Core/      all parsing (classlib, no console I/O)
    src/atlas/           CLI (`atlas`) — thin dispatch over Core
    src/Atlas.Server/    ASP.NET minimal API + static host for web/
    src/Atlas.App/       WebView2 desktop shell (Windows-only at runtime)
    web/                 frontend (vanilla ES modules + vendored three.js)

## Quickstart (CLI)

    set ATLAS_GAME=<game>\game\sqpack        # or --game; XIVTOOL_GAME also accepted
    set ATLAS_SCHEMA=<EXDSchema dir>         # or --schema; optional, names columns

    atlas sheets                             # list all EXD sheets
    atlas header TerritoryType               # column layout of a sheet
                                             #  (link columns show -> Target per
                                             #  EXDSchema)
    atlas dump Emote --out emote.csv         # sheet -> CSV
    atlas extract <gamepath> --out f         # raw file from sqpack
    atlas territory 1345 --out ws --library <dumps/library> --collision
                                             # full territory workspace (11 CSVs)
    atlas mod Pcb dump 1345 --out d --obj    # collision-mesh.obj export
    atlas mod Mdl info <path.mdl>            # meshes, materials, bbox per LOD
                                             #  (v6/Dawntrail chara mdls read via a
                                             #  transparent v6->v5 shim; all Mdl verbs)
    atlas mod Mdl obj <path.mdl> --out f     # model -> OBJ (--lod N)
    atlas mod Mdl gltf <path.mdl> --out d/   # model -> glTF (--lod N --textured
                                             #  --texsize N): visual meshes only,
                                             #  textured = <stem>-tex.gltf + tex/*.png
    atlas mod Mtrl dump <path.mtrl>          # shpk, textures, samplers, constants (CRC-named)
    atlas mod Chara imc <path.imc>           # equipment variant table (.imc): per-
                                             #  part default/variant entries as CSV
    atlas mod Chara resolve <path.mdl>       # chara mdl -> concrete per-variant
                                             #  mtrl paths via its .imc (--variant N
                                             #  --json); existence-checked
    atlas mod Tex info|png <path.tex> --out f  # texture info / -> PNG (--mip N)
    atlas mod Scd info <path.scd>            # sound container: entries table (format,
                                             #  channels, rate, length, loops, markers)
    atlas mod Scd extract <path.scd> --out f # one entry (--entry N): Ogg passthrough
                                             #  (XOR-deobfuscated), MS-ADPCM -> PCM16
                                             #  WAV, others raw .bin
    atlas mod Avfx info|dump <path.avfx>     # particle effect: counts + texture refs
                                             #  + embedded-model table / full block tree
    atlas mod Avfx gltf <path.avfx> --out d/ # embedded particle models -> glTF
    atlas mod Raw extract <p...> --out-dir d # batch raw extract; many paths and/or
                                             #  --list <file> through one process
    atlas mod Map gltf 1345 --out out/       # whole-territory bg visual as glTF
                                             # (map-1345.gltf/.bin, layers as groups,
                                             #  identity quadruple in node extras)
    atlas mod Map gltf 1345 --out out/ --textured --texsize 1024
    atlas mod Map gltf 128 --out out/ --no-terrain  # skip terrain bgplates (on by default)
                                             # textured variant: map-1345-tex.gltf/.bin
                                             # + tex/*.png (diffuse maps, mip-capped)
    atlas mod Library where tre --tt 919     # asset-usage query: every placement whose
                                             # asset path contains "tre" in that territory
                                             # (bg/sgb/vfx/sound; sgb parts expanded with
                                             #  world positions; --out f for the full CSV)
    atlas mod Library usage-index --out i.csv  # global usage index: level dirs are
                                             # auto-discovered from the live sheets
                                             # (TerritoryType + cutscene-stage scan, so a
                                             #  patch needs no list update; --dirs <file>
                                             #  overrides), ~26 s, ~2.36M rows; an i.csv.ver
                                             # sidecar records the game version — `where
                                             # --index` warns when the game has moved on
                                             #  atlas mod Library where <frag> --index i.csv
    atlas mod Deps index --out deps.csv     # dependency index: mdl -> mtrl -> tex
                                             # edges for every .mdl/.mtrl in the
                                             # ResLogger2 list (--paths <list[.gz]>
                                             # or ATLAS_PATHS); <out>.ver sidecar
                                             # records the game version
    atlas mod Deps refs <path> --index deps.csv
                                             # both directions for one asset: what
                                             # it uses + what uses it (mtrl -> the
                                             # models referencing it, tex -> the
                                             # materials); exit 2 on no match
    atlas mod <Module> <verb> ...            # Lgb / Sgb / Cutb / Tmb / Library / Bnpc ...
    atlas gui                                # spawn Atlas.Server + open browser
                                             # (--port N --no-browser --server <path>)

## Build

Windows: `dotnet build Atlas.slnx -c Release` at the repo root — Lumina is
source-referenced from `../../../Lumina-master` by default (`-p:LuminaRoot=` /
`-p:LuminaDll=` to override). Always name the slnx: a bare `dotnet build` inside
`src/` fails (no project in cwd). Sandbox recipe and output-redirect rules:
CONTRACT.md, Build.

## Product dist

`publish.cmd` at the repo root builds the shippable flat layout into `dist/`:
`atlas.exe`, `Atlas.Server.exe`, `Atlas.App.exe`, and `web/` side by side — run
anything straight from that folder. Framework-dependent by default (needs the
.NET 10 runtime); `publish.cmd -self` produces a self-contained build. First
run of `Atlas.App.exe` asks for the game folder and persists it to
`%LOCALAPPDATA%\Atlas\settings.json`; `atlas gui` and later runs reuse it
(`--game`/`ATLAS_GAME` always override; only picker-supplied paths persist).

`settings.json` also accepts optional data-source keys — `schemaPath`
(EXDSchema dir; names sheet columns, required for the territory picker),
`libraryPath` (xivtool `dumps/library`; required for territory workspaces),
and `pathsFile` (ResLogger2 path list, .txt/.gz; enables the asset browser).
Atlas.App and `atlas gui` forward each as `ATLAS_SCHEMA`/`ATLAS_LIBRARY`/
`ATLAS_PATHS` only when flag/env left it unset — env always wins. Without
them, Sheets still works; Territories and Assets degrade as above.

## GUI

`Atlas.Server` exposes Core over HTTP and serves `web/`: sheet browser with
virtualized grids and CSV export, territory workspace with a three.js viewport
(collision overlay, per-layer toggles, textured-map toggle, and a Find panel
that searches asset placements — e.g. `tre` — and flies the camera to any hit),
asset browser with
tex/mdl/mtrl/scd/avfx preview and export — scd shows the entries table with
in-browser playback (Ogg plays natively in the WebView2/Chromium shell, ADPCM
arrives decoded as WAV) and avfx shows counts, texture thumbnails, and a 3D
preview + glTF export of any embedded particle models (structural inspection —
playback of the simulation itself needs the game engine); the mdl 3D preview has its own Textured toggle
(default on, remembered; self-contained glTF from `/api/mdl/gltf`, diffuse PNGs
embedded as data URIs), an Export glTF button, and a Used-in section listing
every territory that places the model (backed by the global usage index —
one click builds it, ~26 s, cached in the work dir; level dirs are discovered
live from TerritoryType plus the cutscene-stage scan, the built index is
stamped with the game version, and after a patch the pane flags it stale and
offers Rebuild) — and composed textured
map export (glTF). The API
doubles as the second headless surface — everything clickable is also curl-able.

Three ways in:

    atlas gui                    # spawns the server, waits for ready, opens browser
    Atlas.App.exe                # WebView2 desktop shell (spawns + owns the server,
                                 # logs to %LOCALAPPDATA%\Atlas\server.log)
    Atlas.Server.exe             # bare server; browse to the printed URL

Server env: `ATLAS_GAME` (required), `ATLAS_SCHEMA`, `ATLAS_LIBRARY`,
`ATLAS_WEB`, `ATLAS_WORK`, `ATLAS_PORT`, `ATLAS_PATHS` (ResLogger2 path list,
.txt or .gz — enables the asset browser's path tree). `atlas gui` forwards
`ATLAS_GAME`/`ATLAS_SCHEMA` from its own `--game`/`--schema`/env resolution.
With neither `--game` nor env set, `atlas gui` and `Atlas.App` fall back to the
picker-saved `settings.json`; the server finds `web/` beside its own exe when
`ATLAS_WEB` is absent (the dist layout).

Feature plan and phasing: `../atlas-spec.md`. Verification status: `VERIFY.md`.

