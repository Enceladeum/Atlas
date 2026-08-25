// Curated per-zone stage presets and key labels for the territory viewer.
//
// Provenance: runtime ground truth (759 layer lists; forced cast keys
// game-confirmed keys). Two different levers exist in the game:
//   Gate 1  keyed compositions - LVB layer-filter keys (layer-sets.csv);
//           the /api/territory/{tt}/stages endpoint serves these generically.
//   Gate 3a SGB-internal step0..N states (759 Doman Enclave): the rebuild is
//           NOT key-gated (all bg layers FilterOp=None); each facility is one
//           master SharedGroup whose nested per-step sgbs the compose expands
//           recursively, tagging parts with extras.sgbState = nested stem.
//           Presets flip curated LayerIds AND per-state visibility; states
//           absent from both lists stay untouched. Appliers must tolerate
//           absent layer ids / stems.
// Populace/quest-state (Gate 2, read-spoof) is out of viewer scope: Atlas
// does not place NPCs.
//
// 759 facility masters (layer -> master -> steps):
//   141903 min00 residence min0..2 (+min12 shared 1&2)   141938 wor0 smith wor00..05 (+wkom4/5)
//   142083 pap00 craft pap0..5 (+pkom4, kaz0)            152867 jik00 security jik0..3
//   140505 ter00 school ter0/1/2/4 (+niwa0/1, isuka/b)   140188 tew00 tower twr1/3/4/5 (tiers, stack)
//   140451 tei00 park tei0..2 (+15 always-on parts)      144394 koz00 kozakura koz0..2
// Finished = highest step per facility + its companion dressing; tower tiers
// stack (all shown). Field/farm SGBs (tan/ine/gear/food) are left uncurated.

// STAGE_PRESETS[tt] = { presets: [{ name, note, show: [layerId], hide: [layerId],
//   states?: { show: [stem], hide: [stem] }, resetStates?: true }] }
export const STAGE_PRESETS = {
  759: {
    presets: [
      {
        name: "ruined (pre-rebuild)",
        note: "destruction dressing on, rebuilt facilities hidden",
        show: [138746, 139860, 140214, 152074],
        hide: [141903, 140494, 152867, 140505, 162823, 140451, 162824, 140188, 140104, 140150, 141938, 142083, 144394],
        resetStates: true,
      },
      {
        name: "finished (canon end-state)",
        note: "rebuilt facilities at final step, destruction dressing hidden",
        show: [141903, 140494, 152867, 140505, 162823, 140451, 162824, 140188, 140104, 140150, 141938, 142083, 144394],
        hide: [138746, 139860, 140214, 152074],
        states: {
          show: [
            "sgbg_e3ec_f1_min2", "sgbg_e3ec_f1_min12",
            "sgbg_e3ec_f1_wor05", "sgbg_e3ec_f1_wkom5",
            "sgbg_e3ec_f1_pap5", "sgbg_e3ec_f1_pkom4", "sgbg_e3ec_f1_kaz0",
            "sgbg_e3ecf3_jik3",
            "sgbg_e3ec_f3_ter4", "sgbg_e3ec_f3_niwa1", "sgbg_e3ec_f3_isukb",
            "sgbg_e3ec_tw_twr1", "sgbg_e3ec_tw_twr3", "sgbg_e3ec_tw_twr4", "sgbg_e3ec_tw_twr5",
            "sgbg_e3ec_f3_tei2",
            "sgbg_e3ec_f1_koz2",
          ],
          hide: [
            "sgbg_e3ec_f1_min0", "sgbg_e3ec_f1_min1",
            "sgbg_e3ec_f1_wor00", "sgbg_e3ec_f1_wor01", "sgbg_e3ec_f1_wor02",
            "sgbg_e3ec_f1_wor03", "sgbg_e3ec_f1_wor04", "sgbg_e3ec_f1_wkom4",
            "sgbg_e3ec_f1_pap0", "sgbg_e3ec_f1_pap1", "sgbg_e3ec_f1_pap2",
            "sgbg_e3ec_f1_pap3", "sgbg_e3ec_f1_pap4",
            "sgbg_e3ecf3_jik0", "sgbg_e3ecf3_jik1", "sgbg_e3ecf3_jik2",
            "sgbg_e3ec_f3_ter0", "sgbg_e3ec_f3_ter1", "sgbg_e3ec_f3_ter2",
            "sgbg_e3ec_f3_niwa0", "sgbg_e3ec_f3_isuka",
            "sgbg_e3ec_f3_tei0", "sgbg_e3ec_f3_tei1",
            "sgbg_e3ec_f1_koz0", "sgbg_e3ec_f1_koz1",
          ],
        },
      },
    ],
  },
};

// STAGE_KEY_LABELS[tt][key] = human label for a layer-filter key (only
// game-confirmed meanings; unlabeled keys still list generically).
export const STAGE_KEY_LABELS = {
  919: { 257010: "finale (war memorial)" },
  925: { 254214: "finished twin (926 key)" },
};
