# Sgb module — agent C notes (2026-08-03)

Port of CutScan `sgblayouts` (pipelines/CutScan/Program.cs ~line 262) into
XivTool.Core.Sgb.SgbLayouts + XivTool.Cli.Commands.SgbCommands.

## Usage

    export XIVTOOL_GAME=.../game/sqpack
    # 1. build + persist the sgb enumeration (stable order for chunking):
    xivtool mod Sgb paths   --out sgb-paths.txt --library <dumps/library dir>
    # 2. chunked census (first chunk writes header, rest --append):
    xivtool mod Sgb layouts --out sgb-layouts.csv --paths sgb-paths.txt --from 0    --to 5000
    xivtool mod Sgb layouts --out sgb-layouts.csv --paths sgb-paths.txt --from 5000 --append

`--library <dir>` (or explicit `--instances <csv> --sceneparts <csv>`) may replace
`--paths` on `layouts` directly. Each 5,000-file chunk runs in ~15 s in the sandbox.

## Enumeration (reconstructed — original paths file was not in the repo)

The golden's input list is exactly the distinct union of:
- instances.csv `Asset=...sgb` references (9,692), and
- scene-parts.csv prop paths ending `.sgb` (807 distinct),

ordinal-sorted => 10,103 paths. Verified: the sorted union contains every distinct
Sgb in the golden (9,925 row-emitting) with 178 non-emitting (empty/env-only;
7 absent from sqpack, reported as `missing 7`). `BuildPathList` reproduces it exactly.

## Acceptance evidence (full-file, not sampled)

Full regeneration in 2 chunks ([0,5000) + [5000,10103) --append):
- 98,508 lines (header + 98,507 rows; 43,868 + 54,639), files 9,096+, missing 7, bad 0.
- `cmp` vs dumps/library/sgb-layouts.csv: **byte-identical**;
  md5 7a8c70d5e90d00902f726ddb8ff9bcd7 on both.

## Caveats / deviations

- **Vfx AssetPath**: current CutScan source extracts the path @ io+0x30 for Type=4
  (Vfx) too, but the golden predates that addition (it was added later for
  mapvfx-census) — golden AssetPath is empty for all Vfx rows. Default output
  matches the golden; the newer behavior is behind the additive `--vfx-paths`
  flag (Core: `vfxPaths: true`). ~9.8k Vfx rows gain an avfx path with the flag.
- Names: layer/instance names have `,` replaced by `;` (no CSV quoting) — legacy
  CutScan convention kept because golden diffs are textual. Do not "fix".
- Identity rule: sgb-layouts is game-wide (not territory-scoped); carries none of
  TerritoryId/LgbFile/LayerId/InstanceId, per the CONTRACT exception for
  pre-rule goldens. Nesting (SharedGroup members referencing other sgbs, 19.6k
  refs) is NOT expanded inline — consumers resolve recursively by joining
  AssetPath back onto the Sgb column, exactly as documented in dumps/README.md.
- CutScan wrote the header only when `lo == 0` (chunks to separate files, then
  concatenated). Here: header iff not `--append`; chunk 1 plain, rest `--append`.
- Per-file try/catch granularity is kept from CutScan: a mid-file parse exception
  counts `bad` but leaves already-written rows in place (never observed: bad 0).
