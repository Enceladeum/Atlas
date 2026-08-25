# Territory module notes — Territory workspace v1

## Files

- `Core/Territory/TerritoryWorkspace.cs` — the whole engine (`TerritoryWorkspace.Run`)
- `xivtool/Commands/TerritoryCommands.cs` — CLI dispatch:
  `xivtool mod Territory dump <tt> --out <dir> [--library <libdir>] [--collision]`

Output workspace: `summary.md`, `territorytype.csv` (Name,Value schematized row),
`layer-sets.csv` / `layer-filters.csv` / `layers.csv` / `instances.csv` (LIVE single-dir
runs of the Lvb/Lgb library builders), `layer-axes.csv`, `npcs.csv`, `quest-npcs.csv`,
`quest-policy.csv`, `exits.csv`, `cfc.csv`, `vfx.csv`, and with `--collision` a
`collision/lgb-<tt>-<code>/` Pcb TerritoryDump (default options, golden-identical).

## Design decisions

- **Library label lookup (shared level dirs).** Slice files must be byte-identical row
  subsets of `dumps/library`, whose `Label` column comes from `leveldirs.txt` — and
  multiple TerritoryTypes share one level dir (e.g. Mist TT339 lives in
  `bg/ffxiv/sea_s1/hou/s1h1/level`, labeled **TT136** in the library). The label is
  therefore resolved from `<libdir>/layer-sets.csv` (Dir → Label), falling back to
  `TT<tt>` when the dir is not in the library. Running the builders with the library's
  own label is what makes `cmp` against the library slice succeed.
- **layer-filters.csv parsing.** That file is written UNescaped (golden quirk: raw comma
  in layer name `Phase4,5`), so downstream parsing is end-anchored: `FilterKeys` = last
  field, `FilterOp` = second-last; Label/Dir/File/LayerId are the first four (never
  contain commas). Properly escaped files (layers/instances/manifest) are parsed with
  `LibraryDiff.SplitCsv`.
- **`--library` is effectively required** (quest-npcs/quest-policy slice the manifest,
  exits needs library instances for in-edges, and the label lookup reads layer-sets).
  The CLI errors up front with the file it could not find. Default probe: `dumps/library`
  relative to CWD.
- **Schema fallbacks.** All named-column sheet access goes through EXDSchema
  (`SchemaNames`). Without a schema dir the code falls back to fixed raw column indices
  (current as of patch 7.55 / 2026.07.16): TerritoryType Bg=1/PlaceName=5/IntendedUse=9,
  ENpcResident Singular=0, Quest Name=0 PreviousQuest[0..2]=9/11/12,
  ContentFinderCondition TerritoryType=1 ContentLinkType=2 Content=3
  ClassJobLevelRequired=17 ClassJobLevelSync=18 HighEndDuty=33 Name=43, PlaceName Name=0.
  These are patch-fragile; a log warning is emitted whenever the fallback engages.
  Bg additionally has the Program.cs heuristic (first string column containing
  `/level/`) as a second-tier fallback.

## layer-axes.csv semantics & known ambiguities

Axis decision per layer (row count always == layers.csv):
1. `FilterKey` — layer-filters FilterKeys non-empty; Detail `op=<Match|NoMatch>;keys=a|b`.
2. `Festival` — FestivalID != 0; Detail `festival=N;phase=M`.
3. `ServerToggle` — IsTemporary set. **Never fires in current data:** IsTemporary is `0`
   for all 43k layers game-wide, confirming dumps/README's finding that the third
   visibility axis (server-toggled quest layers, e.g. the z3b1 hangar) has NO file-side
   gate. Such layers land in `Always` here — file-side they are indistinguishable.
4. `Always` — none of the above.

`sets=[...]` appendix: layers.csv `LayerSetIds` joined to the dir's layer-sets rows by
Key first, then by FilterIndex. **Caveat: LayerSetIds is the documented golden BUG column**
(Lumina LayerSetReferences values often duplicate InstanceCount — see dumps/README).
Key-matches (e.g. `keys=75193;sets=[75193]`) look genuine; FilterIndex-matches (e.g.
TT132 `LVD_fcchest_01` LayerSetIds=2=InstanceCount → `sets=[76841]`) are likely bug
noise. Emitted as specified, flagged here rather than silently filtered. The
authoritative activation join remains layer-filters keys × layer-sets Keys.

## quest-policy.csv semantics (v1)

One row per distinct QuestId appearing in quest-npcs.csv (first-appearance order).
`PreviousQuests` = non-zero `PreviousQuest[0..2]` from the live Quest sheet (`|`-joined).
`Actors` = distinct manifest `Name` values of this quest's rows **with Kind=Actor and
TerritoryId==tt** — i.e. actors of that quest placed in this territory; a quest touching
the territory only via Territory/Item/Other rows legitimately shows ActorCount=0
(e.g. TT1010 quest 69919). This is the v1 consumer table for quest-populace policies
({activeQuest, seq, completedSet}).

**DEFERRED:** deep sequence resolution (per-Seq actor visibility from quest script
`.luab`/ToDo data), Kind=Hide suppression modeling, and rebuilding
quest-npc-manifest.csv from sheets (v1 slices the shipped library manifest row-exact).

## exits.csv semantics

- `out` rows: ExitRange instances of this territory's slice; `OtherTerritory` = Extra
  `TerritoryType` (destination). Count is asserted == slice ExitRange count.
- `in` rows: full scan of `<libdir>/instances.csv` for ExitRange rows whose Extra
  `TerritoryType == tt`; `OtherTerritory` = source territory parsed from the row's
  Label (`TT135` → `135`; non-TT labels, e.g. `STAGE:*`, kept raw). X/Y/Z of `in` rows
  are in the SOURCE territory's coordinate space.
- Self-loop exits (a dir exiting to its own tt) would appear as both out and in; none
  observed in the verified territories.

## Verification evidence (sandbox, 2026-08-03, library patch 2026.07.16)

Slice subset checks — `awk 'NR==1 || $2==<dir>' dumps/library/<f>.csv` vs live output,
`cmp` **byte-identical** for all four files on all four territories; quest-npcs.csv
verified byte-identical via csv-parsed TerritoryId==tt row extraction (python, RFC-4180):

| tt | place | dir (label) | sets | filters/layers/axes | instances | npcs | quest-npcs | policy | exits (out/in) | cfc | vfx |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 132 | New Gridania | f1t1 (TT132) | 3 | 231/231/231 | 2371 | 366 | 543 | 205 | 5/4 | 0 | 6 |
| 1160 | Senatus | m5e7 (TT1160) | 1 | 15/15/15 | 382 | 22 | 10 | 1 | 0/0 | 0 | 0 |
| 1010 | Magna Glacies | m5b1 (TT1010) | 2 | 46/46/46 | 243 | 15 | 1 | 1 | 0/0 | 1 | 2 |
| 339 | Mist (housing) | s1h1 (**TT136**) | 2 | 558/558/558 | 12221 | 41 | 9 | 3 | 2/1 | 0 | 22 |

Join sanity: layer-axes rows == layers rows everywhere; exits out-count == slice
ExitRange count (132:5, 339:2); vfx.csv rows == slice `,VFX,` rows (AssetType string in
data is uppercase `VFX`, verified — not `Vfx`); TT1010 cfc row 812 "A Frosty Reception"
(ContentLinkType 1, Content 5051, lvl 82); TT132 axes split FilterKey 107 / Festival 81 /
Always 43; TT339 exits pair with Lower La Noscea (135) both directions. `--collision`
(TT1010) produces `collision/lgb-1010-m5b1/` with collision.csv + pcb-meshes.csv + 7
per-LGB sheets via the untouched Pcb TerritoryDump. Missing-library error path verified
(exit 1, names the missing file). Build: 0 warnings / 0 errors.

## Deferred (beyond quest-policy above)

- Per-tri collision analytics over the `--collision` output (flags exist in Pcb module:
  `--tri-groups`, `--modelbox` — not surfaced here; run `mod Pcb dump` directly).
- Festival/phase name join (Festival sheet) in layer-axes Detail.
- ENpcBase/EObj joins beyond ENpcResident.Singular in npcs.csv.
