# Plan: Lift UI cleanup — remove badges, derive capacity from physics, restyle map labels

Execute top to bottom. All paths are relative to the repo root. The app is a React +
MapLibre GL v5 ski-resort designer; the lift feature spans `src/lifts.ts` (domain math),
`src/types.ts` (save schema), `src/app/liftLayers.ts` (GL layers), `src/app/liftBadge.ts`
(HTML badge markers), `src/app/LiftControl.tsx` (side panel UI), and `src/app/MapView.tsx`
(map owner / state).

## Decisions already made (do not re-litigate)

1. **Remove the base-terminal badge markers entirely** (red plaque + chairlift emblem SVG
   + "/hr" caption). Keep the terminal end-dot circles (`lift-terminals` layer) and keep
   `public/lift-symbols/` untouched (orphaned but intentionally retained).
2. **Capacity is no longer user-set or stored.** It is derived from fixed-grip physics:
   - Rope speed: **450 ft/min = 2.286 m/s** (replaces the current 2.3 m/s).
   - Chairs arrive every **6 seconds** (fixed headway).
   - Therefore `capacityPph = seats × 3600 / 6 = 600 × seats` → Double 1,200 / Triple
     1,800 / Quad 2,400.
   - Capacity remains **displayed** read-only (stats block + lift list rows); only the
     ability to set it is removed.
3. **Remove the Single chair size.** `ChairSize` becomes `2 | 3 | 4`; the Double/Triple/
   Quad selector stays. Default stays Double.
4. **On-map lift name label**: 20 px **bold**, lift red `#d42027`, white halo (bump halo
   width to 2 for readability at that size).

## Step 1 — Domain model: `src/lifts.ts`

- `FIXED_GRIP_SPEC`: set `ropeSpeedMps: 2.286` (update the comment: 450 ft/min line
  speed), add `headwayS: 6`, set `chairSizes: [2, 3, 4]`. Keep `defaultChairSize: 2`.
- Delete `capacityRange()` and the `CapacityRange` interface.
- Add:
  ```ts
  /** Fixed-grip hourly capacity: one chair of `seats` every headwayS seconds. */
  export function fixedGripCapacityPph(chairSize: ChairSize): number {
    return (chairSize * 3600) / FIXED_GRIP_SPEC.headwayS; // 600 × seats
  }
  ```
- `fixedGripDerived`: change signature to `(chairSize, lengthM)` — capacity is no longer
  an input. `headwayS` is now the constant `FIXED_GRIP_SPEC.headwayS`; drop the
  `aggressive` field entirely (a fixed 6 s headway can never be "too tight").
  `carrierSpacingM`, `carriersOnLine`, `rideTimeS` keep their formulas.
- `CHAIR_LABELS`: drop the `1: 'Single'` entry.
- `sanitizeLifts`: remove all `capacityPph` handling (no clamp, no field on output).
  Chair-size fallback already routes non-members of `chairSizes` to the default, so
  legacy `chairSize: 1` saves become Doubles automatically — verify this in a test.

## Step 2 — Save schema: `src/types.ts`

- `ChairSize` → `2 | 3 | 4` (update the comment: double through quad).
- Remove `capacityPph` from `SavedLiftBase`.
- Do **not** bump `schemaVersion`; loading is tolerant — old saves' extra `capacityPph`
  key is simply ignored by `sanitizeLifts`.

## Step 3 — Delete the badges

- Delete `src/app/liftBadge.ts`.
- `src/app/MapView.tsx`:
  - Remove the `syncLiftBadges` / `clearLiftBadges` / `BadgeEntry` import and
    `badgeStoreRef`.
  - Remove the `syncLiftBadges(...)` call inside `reinitAfterStyle`.
  - Remove the badge-reconcile `useEffect` (the one depending on `[lifts]` that calls
    `syncLiftBadges`).
  - Remove `clearLiftBadges(...)` from the map-teardown cleanup.
  - **Keep `selectLiftRef`** — the side panel's lift rows still use it to open the edit
    panel (badge click was only the second entry point).
- `src/app/app.css`: delete the badge rules — `.lift-badge`, `.lift-badge-plaque`,
  `.lift-badge--planning` (both rules), `.lift-badge-cap` (≈ lines 1267–1300). Also
  delete `.lift-slider` (≈ line 1078, orphaned by Step 4). **Keep `.lift-warning`** — the
  elevation-unavailable error still uses it.

## Step 4 — Panel UI: `src/app/LiftControl.tsx`

- `DraftLift`: remove `capacityPph`.
- Replace `ChairCapacityFields` with a chair-size-only field (suggested name
  `ChairSizeField`): the `<select>` over `[2, 3, 4]` with `CHAIR_LABELS`; delete the
  capacity `<input type="range">` block and the `chairSizePatch` helper (no clamping
  needed when nothing is user-set).
- `LiftStatsBlock`: drop the `capacityPph` prop; call
  `fixedGripDerived(chairSize, stats.lengthM)`. Remove the `derived.aggressive` headway
  warning block. Add a read-only stats line (same `readout-line` pattern as Length):
  label `Capacity`, value `fixedGripCapacityPph(chairSize).toLocaleString() + '/hr'`.
- Lift list rows: replace `l.capacityPph.toLocaleString()` with
  `fixedGripCapacityPph(l.chairSize).toLocaleString()`.
- Update both call sites (review panel + edit panel) for the renamed field component and
  the removed props.

## Step 5 — Map owner: `src/app/MapView.tsx`

- Remove `capacityRange` from the `../lifts` import.
- In the draft-creation branch of the lift-drawing `onClick` handler, drop
  `capacityPph: capacityRange(...).default` from the new `DraftLift`.
- In `confirmLift`, drop `capacityPph` from the constructed `SavedLift`.

## Step 6 — Map label styling: `src/app/liftLayers.ts`

In the `lift-labels` layer:

- `layout`: `'text-size': 20`, `'text-font': ['Noto Sans Bold']`.
- `paint`: `'text-color': LIFT_RED` (the existing `#d42027` const), keep
  `'text-halo-color': '#ffffff'`, bump `'text-halo-width': 2`.

**Font risk**: glyphs must exist in the basemap's fontstack for BOTH themes — light
(OpenFreeMap Liberty) and dark (Carto Dark Matter). After the change, open the app in
each theme (Settings toggles it live) with a lift present and watch the console for
glyph 404s / missing-fontstack warnings. If `Noto Sans Bold` is missing on either style,
fall back to `['Noto Sans Regular']` and keep the 20 px red + halo styling — size and
color are the priority, boldness is best-effort.

## Step 7 — Tests: `src/lifts.test.ts`

- Delete the `capacityRange` describe block and its import.
- `fixedGripDerived` block: new signature `(2, 1500)`; headway asserts the constant 6 s;
  `carrierSpacingM` ≈ `6 × 2.286`; `rideTimeS` ≈ `1500 / 2.286`; remove both
  `aggressive` assertions.
- Add a `fixedGripCapacityPph` test: `(2) → 1200`, `(3) → 1800`, `(4) → 2400`.
- `sanitizeLifts` fixture: remove `capacityPph` from the `valid` lift. Replace the
  "clamps out-of-range capacity" test with: `chairSize: 7 → 2`, `chairSize: 1 → 2`
  (legacy Single migrates to Double), and a legacy save carrying `capacityPph: 1200`
  still sanitizes cleanly with no `capacityPph` key on the output.

## Verification (all must pass)

1. `npm test` — vitest green.
2. `npm run build` — `tsc` clean (this is the real check that every `capacityPph` /
   `ChairSize 1` reference was found).
3. Run the app (`run-dev.bat` or `npm run dev`) and confirm:
   - No red plaque badges anywhere; terminal dots still render; clicking a lift row in
     the panel still opens the edit panel.
   - New-lift review panel: name, Chairs (Double/Triple/Quad only), Status, stats with a
     read-only Capacity line — no slider, no Single.
   - Lift name label on the map: 20 px bold red with white halo, in both light and dark
     themes (check console for glyph errors — see Step 6 fallback).
   - Load a pre-existing save containing lifts (if one exists with a Single or a custom
     capacity, it loads as a Double with derived capacity; no crash).
