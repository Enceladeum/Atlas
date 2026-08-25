# Cutb/Tmb module notes (agent D)

Port of `pipelines/CutScan` subcommands `sceneparts`, `proptransforms`, `cttl`
and the standalone `pipelines/BinScan` into XivTool.Core.Cutb / XivTool.Core.Tmb
+ the `mod Cutb` CLI dispatch. Logic is a faithful line-for-line port; golden
schemas and float formatting (`0.###` / `0.####`, invariant) preserved exactly.

## Files

- `XivTool.Core/Cutb/ScenePartsOps.cs` — `CutsceneCatalog` (cutscene row list from
  the live Cutscene sheet or a dumped Cutscene.csv) + `ScenePartsOps.Write`
  (CTRL resource-table string grep -> part/prop/collision rows).
- `XivTool.Core/Cutb/PropTransformOps.cs` — `FindCtalEntryTable` (shared CTAL
  entry-table locator) + `PropTransformOps.Write` (CTAL prop placements).
- `XivTool.Core/Cutb/BinScanOps.cs` — little-endian u32 pattern grep over cutbs.
- `XivTool.Core/Tmb/TimelineOps.cs` — TMLB -> TMAC -> TMTR -> C018 keyframe walk,
  handles resolved against CTAL (uses `PropTransformOps.FindCtalEntryTable`).
- `xivtool/Commands/CutbCommands.cs` — CLI dispatch (`xivtool mod Cutb <verb>`).

## Usage

    xivtool mod Cutb sceneparts --out scene-parts.csv [--from N --to N] [--append] [--list Cutscene.csv]
    xivtool mod Cutb props      --out cutscene-prop-transforms.csv [--from N --to N] [--append] [--list f]
    xivtool mod Cutb keyframes  --out cutscene-timeline-keyframes.csv [--from N --to N] [--append] [--list f]
    xivtool mod Cutb scan 1035331,1034217 [--list f] [--out f] [--from N --to N]

`--from/--to` = Cutscene RowId range [from, to) — the CutScan chunking pattern
(45 s sandbox limit; measured: props ~1500 rows / 15 s, keyframes ~1200 / 15 s,
sceneparts ~100 rows / 12 s — sceneparts is the slow one, chunk at ~200).
`--append` opens the file in append mode and suppresses the header, so chunked
runs concatenate cleanly. Without `--list`, the cutscene list is read straight
from the Cutscene sheet (first string column = cutb path); verified identical
parsing vs the dumped `dumps/raw/Cutscene.csv` used by the original pipeline.

Chunk-invariance: the original deduped cutb paths per chunk; Cutscene.csv has
zero duplicate paths (checked), so chunk boundaries cannot change output.

## Acceptance evidence (2026-08-03)

IMPORTANT context: the goldens were consolidated 2026-07-15; the mounted game
was patched to **2026.07.16.0001.0000** the next day. The patch added cutb
files (manfst00001/2/3, mancmn00000), filled previously empty Cutscene rows
(3847 banall41010, 3849/3851 anvxiv), and changed wkscos1w417. Therefore the
primary equivalence proof is port vs the ORIGINAL CutScan/BinScan binaries
(`/path/to/CutScan`, `/path/to/BinScan`, source diffed identical to
`pipelines/`) on identical inputs and today's data; golden deltas below are
fully enumerated and all attributable to that patch.

- `cutscene-prop-transforms.csv`: full regen (`props`, chunks 0/1500/3000/4200,
  `--list` archived Cutscene.csv, `--append`): 13,738 rows.
  vs original CutScan full run: **byte-identical** (`cmp`).
  vs golden: all 13,734 golden rows byte-identical (after CRLF normalization —
  the golden is stored with CRLF; keyframes/scene-parts goldens are LF, and
  this port writes LF per contract); 4 extra rows only, CutsceneRows 23/24/25/40
  (patch-added cutbs; the original binary emits them identically today).
- `cutscene-timeline-keyframes.csv`: full regen (`keyframes`, chunks
  0/1000/2200/3400/4200): 180,226 rows. `cmp` vs golden: **byte-identical**
  (100% coverage). Also byte-identical to a full original-CutScan `cttl` run.
- `scene-parts.csv` (1,039,706 rows, not fully regenerated per plan): sampled
  slices 0-100 (24,102 rows), 1200-1300 (17,870), 2500-2600 (32,749),
  3500-3600 (41,189), 4000-4101 (end, 2,966) diffed vs awk-extracted golden
  ranges: **first four byte-identical**; 4000-4101 differs by exactly ONE
  golden-only row (4034 wkscos1w417 collision c1w4_t0_rck02b.pcb) — the
  original binary run today also omits it (port==original byte-identical on
  that slice), i.e. the cutb changed in the patch.
- `scan`: ids 1035331,1034217,1033643,1032572 over rows 2300-2600 ->
  hits luckyw00150/00220/00230/00310/00320 with exactly the id sets in
  valens-cutscene-cast.csv (and correctly none for luckyw00340);
  697 gaiuse50130 hits 1010842|1001040. Output **byte-identical** to the
  original BinScan binary on the same trimmed csv.
- CLI dispatch (`xivtool mod Cutb <verb>`) smoke-tested for all four verbs;
  outputs byte-identical to the Core-driven acceptance runs.

## Caveats

- `props`/`keyframes`/`sceneparts` skip cutbs whose path repeats an earlier row
  (per-invocation dedup, as in CutScan). No-op on current data (paths unique).
- `--append` bypasses `Csv.OpenWriter` (which always truncates); it still
  writes UTF-8 no-BOM. Header only on non-append runs.
- Known-by-design gaps inherited from the source (see dumps/README.md):
  props covers only CTAL-inline placements (~40% of prop-preloading cutbs);
  keyframes rows with empty Kind = handle absent from that cutb's CTAL;
  each TMLB's last ~2 entry refs are string-pool junk and are tolerated.
- `scan` ports the 41-line BinScan verbatim: unaligned little-endian u32 match,
  no zero-padding check; one output line per cutb with any hit (`id|id|...`).
- Default (sheet) catalog vs `--list` archived csv: identical parsing, but the
  live sheet reflects the current patch (extra prop rows for banall41010 and
  anvxiv01310/01330 vs the 2026-07-15 csv). Use `--list dumps/raw/Cutscene.csv`
  when reproducing goldens.
- The prop-transforms golden file is stored with CRLF line endings; this port
  (and the contract) writes LF. Compare after CRLF normalization.
