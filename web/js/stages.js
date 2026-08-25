// Curated per-zone stage presets and key labels for the territory viewer.
//
// Provenance: runtime ground truth (759 layer lists; forced cast keys
// game-confirmed keys). Two different levers exist in the game:
//   Gate 1  keyed compositions - LVB layer-filter keys (layer-sets.csv);
//           the /api/territory/{tt}/stages endpoint serves these generically.
//   Gate 3a SGB-internal step0..N states (759 Doman Enclave): the rebuild is
//           NOT key-gated (all bg layers FilterOp=None); ruined vs finished
//           is which layers/parts draw. The presets below flip only the
//           curated LayerIds and leave neutral layers untouched. Some listed
//           ids (162823/162824/140188) are not in Atlas's current bg compose;
//           appliers must tolerate absent ids.
// Populace/quest-state (Gate 2, read-spoof) is out of viewer scope: Atlas
// does not place NPCs.

// STAGE_PRESETS[tt] = { presets: [{ name, note, show: [layerId], hide: [layerId] }] }
export const STAGE_PRESETS = {
  759: {
    presets: [
      {
        name: "ruined (pre-rebuild)",
        note: "destruction dressing on, rebuilt facilities hidden",
        show: [138746, 139860, 140214, 152074],
        hide: [141903, 140494, 152867, 140505, 162823, 140451, 162824, 140188, 140104, 140150, 141938, 142083],
      },
      {
        name: "finished (canon end-state)",
        note: "rebuilt facilities on, destruction dressing hidden",
        show: [141903, 140494, 152867, 140505, 162823, 140451, 162824, 140188, 140104, 140150, 141938, 142083],
        hide: [138746, 139860, 140214, 152074],
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
