# Pcb module notes (agent B) — territory geometry/collision dumper

Port of `LgbDump/DumpCore.cs` (491 lines) into `XivTool.Core.Pcb`, plus two
flag-gated collision upgrades. Default output is **byte-identical** to the
original LgbDump tool.

## Files

- `XivTool.Core/Pcb/PcbParser.cs` — `PcbMesh` + `PcbParser.ParsePcb` (PCB BVH-node
  walk, raw + AABB-compressed verts, per-tri u64 material), analytic unit
  primitives (`BoxVerts/BoxTris`, 16-seg `CylVerts/CylTris`), `LocalMatrix`
  (S·Rx·Ry·Rz·T, world = local × parent), `ShapeName`.
- `XivTool.Core/Pcb/TerritoryDump.cs` — `TerritoryDump.Run(GameData, territory,
  outRoot, TerritoryDumpOptions?, Action<string>? log)`; the whole integrated
  dumper (7 per-LGB CSVs, collider harvest incl. SGB recursion, terrain tiles,
  collision.csv, pcb-meshes.csv, optional OBJ).
- `xivtool/Commands/PcbCommands.cs` — CLI dispatch.

Core policy: no Console (all progress via `log`), writers/escaping via
`XivTool.Core.Csv` (`Csv.OpenWriter` = UTF-8 no BOM, `Csv.Escape`); floats
`ToString("R", InvariantCulture)` exactly as the golden tool.

## Usage

    xivtool mod Pcb dump <territoryId|bg-level-dir> --out <dir> [--obj] [--tri-groups] [--modelbox]

Creates `<out>/lgb-<id>-<mapcode>/` (e.g. `lgb-1345-m6d2/`). `--tri-groups`
requires `--obj`. Example:

    xivtool mod Pcb dump 1345 --out out/ --obj --tri-groups --modelbox
    xivtool mod Pcb dump bg/ex5/07_mid_m6/dun/m6d2/level --out out/ --obj

## Golden acceptance (2026-08-03)

Golden regenerated with the original DumpCore via `lgbdumpcli` wrapper, then
this port run on the same inputs, `diff -r`:

| TT | zone | files | result |
|---|---|---|---|
| 1160 | m5e7 | 9 (6 LGB CSVs + collision.csv + pcb-meshes.csv + OBJ) | **byte-identical** |
| 1345 | m6d2 | 9 | **byte-identical** |

1160: 855 colliders, 93 pcbs, OBJ 40,277 tris / 36,233 verts.
1345: 5,830 colliders (terrain 225, bgpart 4,466, box 1, sgb 1,138), 395 pcbs
(0 missing), OBJ 892,736 tris / 806,332 verts. `planner.lgb` does not exist in
either zone, hence 6 not 7 LGB CSVs (the dumper always tries all 7).

Identity-rule note: this port matches the golden schema exactly (pre-rule);
`collision.csv` carries `LgbFile,LayerId,InstanceId` but no `TerritoryId`
column — per CONTRACT, schema upgrades are Wave 3.

## Upgrade 1 — `--tri-groups` (with `--obj`)

Instead of one OBJ group per collider, faces are bucketed per triangle class
and emitted as up to four groups named `<Source>_<InstanceId>_<n>_<class>`
(same `<n>` numbering as default mode; the per-collider `# pcbpath` comment and
the vertex block are written once, before the group lines; group emit order is
invis, unland, wall, floor; empty classes are omitted).

Exact semantics, evaluated per triangle in this precedence order:

1. `invis`  — `(mat & 0x1F) == 0x11` (vnavmesh invisible-wall material id)
2. `unland` — `(mat & 0x200000) != 0` (unlandable)
3. `wall` / `floor` — world-space face normal `n = cross(b-a, c-a)`:
   `|n.y| < 0.5 * |n|` (i.e. |ŷ·n̂| < 0.5) → `wall`, else `floor`.
   Degenerate triangles (|n| = 0) fall to `floor`.

`mat` is the per-tri u64 from the PCB primitive (@+4). Geometry without per-tri
materials (analytic Box/Sphere/Cylinder/Board, ModelBox boxes) uses `mat = 0`,
so it splits wall/floor by normal only.

Results:

| TT | invis | unland | wall | floor |
|---|---|---|---|---|
| 1345 (m6d2, dungeon) | 0 | 6,829 | 231,488 | 654,419 |
| 1160 (m5e7) | 0 | 112 | 24,891 | 15,274 |
| 1187 (y6f1, Urqopacha overworld) | 0 | 104,678 | 940,839 | 1,112,301 |

1345 group counts: 4,996 `_wall`, 5,510 `_floor`, 17 `_unland` (10,523 groups
total vs 5,829 in default mode; total tri/vert counts unchanged at
892,736/806,332 — classification is a pure repartition).

Caveat (verified empirically): `invis` is 0 in every zone tested. A raw
histogram over the 4 largest PCBs of TT 1187 shows only 9 distinct per-tri
materials with low-5-bits in {0x0,0x2,0x4,0x5,0x7,0xA} (surface/footstep ids)
plus bit 0x200000 on 360 tris — low5 == 0x11 simply does not occur *per-tri*
there. At runtime the effective material is the per-tri u64 combined with the
collider's instance-level material value/mask (FFXIVClientStructs
`Collider.ObjectMaterialValue/ObjectMaterialMask`; cf. the exported
`Attr`/`AttrMask` columns), and invisible walls typically get 0x11 from that
instance-level override or live on analytic CollisionBox instances. Folding the
instance override into the class test is deliberate future work — the flag
implements exactly the mandated per-tri rule; consumers can also join
collision.csv `Attr` themselves.

## Upgrade 2 — `--modelbox` (ModelBox bbox fix)

`ModelBox` colliders (BG parts with `CollisionType=Box` and no collision asset;
the collider is the *model's* bounding box) previously emitted no AABB in
collision.csv and only a meaningless ±1 unit box in the OBJ. With `--modelbox`:

- The referenced `.mdl` (held in the `PcbPath` column) is loaded via Lumina
  `MdlFile`; the local box is `ModelBoundingBoxes` (`MdlStructs.BoundingBoxStruct`,
  float[4] Min/Max, xyz used), falling back to `BoundingBoxes` if degenerate
  (min == max on all axes). No manual header offsets were needed — Lumina's
  MdlFile parsed 100% of encountered models.
- collision.csv: the 8 box corners are transformed by the instance world matrix
  and fill `WorldMin*/WorldMax*` (previously empty). All other rows/columns are
  unchanged — verified: golden vs `--modelbox` collision.csv differ **only** in
  ModelBox rows' last 6 columns.
- OBJ: the ModelBox group becomes the transformed bbox box (12 tris, same
  winding table as the unit box). Without bounds (mdl missing/unparseable) it
  falls back to the default unit box.

Results: TT 1345 — 592/592 ModelBox colliders resolved (0 empty-extent rows,
0 unresolved mdls); TT 1160 — 191/191. Sample (1345):

    BgPart,bg,299114,phs03,12301584,,ModelBox,bg/ex5/07_mid_m6/dun/m6d2/bgparts/m6d2_a3_wal05.mdl,
      ...,494.90582,-58.609673,-351.0005,497.0592,32.62496,-300.9997

(a 2×91×50 wall segment — plausible vs neighbouring mesh colliders).

## Caveats

- All original DumpCore caveats stand: SGB member offsets are reverse-
  engineered (BgPart coll @+0x34, CollisionBox pcb @+0x48), composed SGB
  rotations unverified for non-Y axes, sphere approximated as cylinder.
- `GameData` is supplied by the caller (CLI uses `XivEnv`); the original tool
  constructed its own with `PanicOnSheetChecksumMismatch=false` — no observable
  difference on the golden zones.
- OBJ line endings are the runtime's newline (LF in the sandbox where goldens
  were made). Cross-OS byte-identity of goldens requires matching platforms.
- `--modelbox` uses whichever of the two header bboxes is non-degenerate;
  `BoundingBoxes` (the first, purpose officially unknown) was never needed on
  the tested zones.
