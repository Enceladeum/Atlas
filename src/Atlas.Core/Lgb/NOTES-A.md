# Agent A notes — Lgb/Lvb module (Wave 2)

## Files

- `Core/Lvb/LvbOps.cs` — `WriteLayerSets` (port of CutScan `lvbsets`) → layer-sets.csv
- `Core/Lgb/LayerFilterOps.cs` — `Write` (port of CutScan `layerfilters`) → layer-filters.csv
- `Core/Lgb/LayerCensus.cs` — layers.csv builder (rewritten, see Provenance)
- `Core/Lgb/InstanceCensus.cs` — instances.csv builder (rewritten, see Provenance)
- `Core/Lgb/leveldirs.txt` — the canonical 638-line dir list (checked in so the golden
  iteration order can never be lost again; copied from sandbox `/path/to/xivtool/leveldirs.txt`)
- `xivtool/Commands/LgbCommands.cs` — CLI dispatch (`xivtool mod Lgb <verb> ...`)

## CLI verbs

    xivtool mod Lgb sets      --dirs leveldirs.txt --out layer-sets.csv
    xivtool mod Lgb filters   --dirs leveldirs.txt --out layer-filters.csv
    xivtool mod Lgb layers    --dirs leveldirs.txt --out layers.csv    [--from 0 --to 160] [--append]
    xivtool mod Lgb instances --dirs leveldirs.txt --out instances.csv [--from 0 --to 160] [--append]

`--from/--to` = 0-based, to-exclusive line range over the dirs file; `--append` opens the
out file in append mode and suppresses the header, so chunked runs concatenate into a
byte-identical full file. `--dirs` defaults to `leveldirs.txt` in the CWD if present.
Timing (sandbox): `sets`/`filters` full 638 dirs in one 45 s call; `layers`/`instances`
(Lumina LgbFile parse) ~160 dirs per ~15 s → 3-4 chunks.

## Provenance of the "lost" builders

layers.csv/instances.csv were built by the old sandbox xivtool `lgbindex` command.
CONTRACT.md said the builder was lost with LgbCheck, but it survives verbatim in the
sandbox copy `/path/to/xivtool/Program.cs` (case "lgbindex", lines 246-308). LayerCensus
and InstanceCensus are line-faithful ports of that code (split into two passes; splitting
does not change row order because the two outputs never interleave within a file).

## Iteration order (inferred from goldens, then confirmed against the recovered source)

All four outputs iterate:

1. `leveldirs.txt` line order (638 dirs: TT-labeled Bg paths + STAGE:* dirs; first
   `bg/ex1/01_roc_r2/dun/r2d1/level TT1021`, last `bg/ffxiv/zon_z1/jai/z1j1/level TT176`).
2. Per dir, LGB probe order `bg, planmap, planevent, planlive, sound, vfx, planner`
   (`LayerFilterOps.LgbNames`). NOTE: this is NOT `InstanceIdentity.LgbFiles`
   canonical order (which has planner before sound/vfx) — golden row order depends on it.
3. Per LGB, layer order as stored in the file; per layer, instance-object order.
   (`sets` reads only `<leaf>.lvb` per dir; filter entries in SCN1 order.)

## Quirks kept for golden parity (do NOT "fix")

- `sets`/`filters` rows are written with NO CSV escaping (layer-filters.csv line 9339
  contains a raw comma in layer name `Phase4,5`). layers.csv/instances.csv DO escape
  (Csv.Escape) — same layer is `"Phase4,5"` there.
- layers.csv `LayerSetIds` = Lumina `LayerSetReferences[].LayerSetId`, known-wrong values
  (README bug note); reproduced as-is.
- instances.csv skips `BGInstanceObject` rows entirely (BG parts only appear as
  layers.csv `BgPartCount`).
- Golden schemas predate the identity rule (`Label,Dir` instead of `TerritoryId`);
  kept exactly per CONTRACT exception. Identity upgrade = Wave 3 flag.
- Floats via `Csv.Str` == `ToString("R")` (InvariantGlobalization), matching golden
  artifacts like `3.039233E-13` and `-0`.
- Lumina parity matters: build with `LuminaDll=/path/to/Lumina.dll`
  (the exact assembly the golden was built with) or the same Lumina-master source.

## Acceptance evidence (sandbox, 2026-08-03)

The live game was PATCHED after the goldens were generated (new ex5/seasonal quest
layers). Code parity was therefore proven two ways: (a) diff vs golden with every
delta explained, (b) re-running the ORIGINAL builders (CutScan binary; old xivtool
`lgbindex` at `/path/to/xivtool`) against today's game and diffing their output
against the ports — byte-identical in all cases.

- layer-sets.csv (`sets`, 638 dirs, one call): 1753 lines, diff vs golden:
  **byte-identical**. (LVBs untouched by the patch.)
- layer-filters.csv (`filters`, one call): 43174 lines vs golden 43155.
  Diff = 19 additions, 0 removals/changes — all new patch quest layers
  (x6d6/x6f2/x6t1 ChrHdb951/952/PUB, y6f1-3/y6t1 BanAll410/420, o6e1 KinGkd/KinGkw,
  w1eb QST_KTG). Original CutScan `layerfilters` today == my output byte-identical.
- layers.csv (`layers`, 3 chunks 0-160-400-638): 43174 lines vs golden 43155.
  Diff = the same 19 new layers + 1 changed row (f1ec QST_Xms2026_Fst4_Lively
  InstanceCount 18->32; golden already had the layer, patch added objects).
  Original `lgbindex` today == my output **byte-identical** (full file).
- instances.csv (`instances`, 4 chunks 0-160-320-480-638): 428934 lines vs golden
  428815. Diff = 158 additions / 39 removals, every one confined to the same
  drifted planevent layers (plus c1w4 Cosmic housing + s1f3 rows the patch edited);
  no removed-row group exists outside the changed-layer set. Original `lgbindex`
  today == my output **byte-identical** (full file, both halves).
  Sampled-slice check: TT134 (1160), TT156 (2202), TT628 (2035), TT819 (2055),
  TT1044 (1144), TT176 (21) all byte-identical vs golden; TT1187 differs by
  exactly +14 rows == the new QST_BanAll420 layer (InstanceCount 14).

## Caveats

- `sound.lgb`/`vfx.lgb` occasionally throw in Lumina's parser; the original swallowed the
  exception and skipped the file (`try/catch continue`) — ports do the same.
- STRETCH mapvfx census (mapvfx-census.csv) NOT ported: its builder was a multi-stage
  join (LgbCheck direct scan + sgblayouts recursion + world-transform composition) whose
  join stage is also lost; the surviving pipelines/LgbCheck/Program.cs is only the
  VFX direct scanner with a different schema. Also, the stretch precondition
  ("all four golden checks pass byte-identical") is unmeetable on the patched game.
  Left for a follow-up wave.
- Game-data drift means Wave 3 full-file verification will show the same explained
  deltas until dumps/library goldens are regenerated on the current game version;
  regenerating them from these verbs is one command per file.
