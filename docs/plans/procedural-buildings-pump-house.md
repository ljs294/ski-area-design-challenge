# Procedural Buildings and Snowmaking Pump House

## Execution handoff

This is a decision-complete implementation plan for another agent or agent team. Use
`gpt-5.6-sol` at medium reasoning as the main composer and integration owner. Use up to
three `gpt-5.6-luna` subagents at xhigh reasoning concurrently for the bounded work
packages below.

Follow `AGENTS.md`, especially the save compatibility, map ordering, construction
ownership, cancellation, and integration-file ownership rules. The Sol integration
owner is the only agent that may edit `src/app/MapView.tsx`, `src/types.ts`, and
`src/app/app.css` during this work. Preserve unrelated user changes.

## Product behavior

- Add a reusable procedural-building domain, initially exposed through the Snowmaking
  dock as **Build pump house**.
- The default pump house is 60 ft long by 40 ft wide with a 16 ft eave height and a
  4:12 gable roof. In canonical units these are 18.288 m, 12.192 m, and 4.8768 m. The
  roof rises 6 ft 8 in (2.032 m), producing a 22 ft 8 in (6.9088 m) ridge height. The
  ridge follows the long axis.
- Use catalog-defined light-gray walls, a charcoal roof, and a concrete foundation in
  v1. Roof pitch, colors, and materials are not user-editable yet.
- Placement proceeds through these phases:
  1. Hovering shows the complete building at the pointer with its current dimensions
     and heading.
  2. The first click fixes the building center.
  3. Pointer movement from the center previews the long-axis bearing.
  4. A second click at least 1 m from the center locks the bearing and opens review.
  5. Review permits editing the name, length, width, eave height, normalized clockwise
     bearing, and foundation mode. Changes update validation and previews.
- **Flatten site** is enabled by default for each new placement. The alternative is a
  level structure on procedural foundation walls over unchanged natural terrain.
- Built buildings can only be renamed or removed. Moving, rotating, resizing, changing
  foundation mode, or changing appearance requires removing and rebuilding.
- Reject positive-area overlap with another player-building footprint. Touching edges
  are allowed. Imported OpenStreetMap buildings, lifts, trails, roads, pipes, water,
  and other non-building infrastructure do not block placement in v1.
- The entire rotated footprint must remain inside prepared terrain and have usable
  elevation data. Flattened placements are also invalid if their complete grade cannot
  daylight inside the terrain package.

## Data model and compatibility

Create dependency-neutral building and economics models under `src/types/` and pure
domain behavior outside `src/app/`.

Add a neutral economics contract:

```ts
export type MaintenanceCadence = 'unspecified' | 'daily' | 'monthly';

export interface AssetEconomics {
  capitalCostUsd: number | null;
  maintenanceCostUsd: number | null;
  maintenanceCadence: MaintenanceCadence;
}
```

Every new building stores:

```ts
{
  capitalCostUsd: null,
  maintenanceCostUsd: null,
  maintenanceCadence: 'unspecified',
}
```

The UI renders null amounts as **TBD**. Costs do not block construction or affect an
economy in this release. Do not retrofit lifts as part of this feature, but keep the
economics type reusable for a later lift migration.

Add a persisted building model with this semantic shape:

```ts
export type BuildingTypeId = 'snowmaking-pump-house';

export interface SavedBuilding {
  id: string;
  name: string;
  buildingTypeId: BuildingTypeId;
  generatorVersion: 1;
  center: [number, number];
  bearingDeg: number;
  dimensions: {
    lengthM: number;
    widthM: number;
    eaveHeightM: number;
  };
  roof: {
    kind: 'gable';
    pitchRise: 4;
    pitchRun: 12;
  };
  foundation: FlattenedBuildingFoundation | SlopeBuildingFoundation;
  connection: {
    kind: 'snowmaking-pump';
    nodeId: string;
  };
  economics: AssetEconomics;
  createdAt: string;
}
```

The exact nested type names may follow repository naming conventions, but retain these
fields and semantics. Persist authored parameters and foundation samples, not generated
footprint or mesh data. The type registry provides defaults and validation, while the
saved resolved parameters keep existing buildings visually stable after later catalog
balancing.

Add optional `buildings?: SavedBuilding[]` to `GameSave` and keep newly written saves at
schema version 15. Hydrate older saves with no building collection to `[]`. Include the
collection in `initialResortDesign`, snapshots, live design comparison, unsaved-change
detection, compatibility fixtures, and the type-only facade. Browser and Electron save
transports already serialize the complete save and need no new IPC endpoint.

Add optional building ownership and pump equipment metadata to `SavedSnowmakingNode`.
For the pump-house center node, persist:

```ts
{
  kind: 'pump',
  ownerBuildingId: building.id,
  pumpRating: {
    horsepowerHp: 1000,
    efficiency: 0.85,
  },
}
```

The building stores the reciprocal node ID. Sanitization accepts only a reciprocal pair
whose node is a pump owned by that building and positioned at its center. Omit malformed
building records with dangling or conflicting ownership; preserve an orphaned network
node as a standalone pump after stripping invalid ownership metadata.

## Procedural geometry and map rendering

- Create a building-type registry and versioned rectangular-gable mesh generator.
  Generate the oriented footprint, walls, two roof planes, gable-end triangles,
  foundation faces, material groups, and collision geometry from saved parameters.
- Render all player buildings through one batched native MapLibre
  `CustomLayerInterface` with `renderingMode: '3d'`; do not add Three.js or another 3D
  dependency. Convert geographic anchors with `MercatorCoordinate.fromLngLat()` and
  `meterInMercatorCoordinateUnits()`.
- Add a generic `building` map family immediately above `lift` and below `snowmaking`.
  Add `building` immediately below `snowmaking` in hit priority. This preserves all
  existing relative ordering while keeping the reusable building system independent of
  the Snowmaking UI that first exposes it.
- Use ordinary GeoJSON layers for the footprint, selected outline, transparent hit
  polygon, foundation/apron plan overlay, and placement drafts because custom WebGL
  layers are not queryable through `queryRenderedFeatures`.
- Keep the real snowmaking node and pipe layers above the building. A center click must
  select the owned pump node; a click elsewhere on the footprint selects the building.
- Expose **Player buildings** in the Structures layer section. Standard visibility
  descriptors control the GeoJSON layers; `visibilityChanged` separately enables or
  disables the custom renderer and triggers repaint.
- Rebuild custom-layer GPU resources and restore current data, selection, ordering, and
  visibility after a style reload. Rebuild the batched mesh only when building data
  changes, not on camera frames.
- During capture, keep committed buildings visible and hide only the placement mesh,
  draft footprint, and grade preview. Restore each transient exactly once.
- The custom layer must explicitly configure and restore the WebGL state it uses. Its
  removal path deletes shaders, programs, buffers, and vertex arrays and clears retained
  references.
- Add a fixed pump-house fixture to Graphics Lab using the production mesh generator so
  overhead, pitched terrain, theme, and quality behavior can be inspected without game
  state.

## Foundation and terrain behavior

Create a pure building-site analysis plus a cancellable worker protocol/adapter. Capture
the building geometry key, terrain revision, and elevation checksum for every request.
Changing placement, dimensions, bearing, or foundation mode invalidates the prior
request; late or cancelled results cannot reach review or confirmation.

For flattened placement:

- Form a level pad by expanding the building footprint 6 ft (1.8288 m) on every side.
- Choose the median of valid pad terrain samples as the deterministic cut/fill-balanced
  finished datum.
- Use the shared earthwork conventions: 1 horizontal to 1 vertical for cut and 2
  horizontal to 1 vertical for fill, daylit to natural terrain within the existing
  maximum earthwork reach.
- Return a terrain patch, edited contours, disturbance polygons, and cut/fill/balance
  estimate. Reject truncated or out-of-bounds grades.
- Persist the finished-floor elevation, `terrainGraded: true`, and earthwork estimate.
  Use the disturbance polygons for best-effort cover clearing.

For slope-foundation placement:

- Sample the four corners and four edge midpoints in a fixed clockwise order and persist
  those ground elevations.
- Set the finished floor to the highest sample plus 6 in (0.1524 m).
- Keep the superstructure level and generate concrete foundation faces down to the
  persisted perimeter samples. Do not mutate terrain or mark elevation dirty.
- Clear cover best-effort only beneath the building footprint.

Both modes place the owned pump node at the exact building center with elevation equal to
the finished floor.

## Controller, documents, and Snowmaking integration

- Add `'building'` to `ToolId`, map it to the Snowmaking dock, allow Layers alongside
  it, and add `'building'` to `ConstructionActivity` and the construction-status label.
- Implement an `idle -> armed -> centered -> review` reducer and a controller that owns
  map interaction leases, draft synchronization, worker identity, confirmation,
  cancellation, selection, rename, and remove.
- Tool switch, Escape, dock switch, selection transition, unmount, or cancellation must
  synchronously release interactions and invalidate pending site work.
- Add a revisioned `BuildingDocument`. Extend `SnowmakingNetworkDocument` transactions
  with prepare/apply/publish operations equivalent to the terrain and topology document
  pattern.
- Add a composite commit coordinator for optional terrain, buildings, and snowmaking.
  Validate every participating revision first, apply all authoritative snapshots before
  invoking observers, then publish each result. There must never be a saved or rendered
  building without its owned node, nor an owned node without its building.
- Construction confirmation runs inside `TerrainDocument.runConstruction('building',
  ...)`, rejects same-tick double confirmation, and retains review plus an actionable
  error on stale terrain, building, or snowmaking state.
- Successful confirmation atomically adds the building, creates the center pump node,
  and advances the snowmaking pump-number counter. Cover editing follows the committed
  operation and remains best effort.
- Renaming a built pump house atomically renames its owned node. An owned pump inspector
  shows the owning building and does not offer independent rename or removal.
- Removing a building atomically removes its node and uses the existing network helper
  to detach connected pipe ends. It does not restore previously graded terrain or cover,
  matching existing structure-deletion behavior.
- The center node is a valid free-standing pump. Later pipe construction can snap to it;
  users assign suction/discharge roles through the existing pump-port editor.
- Snowmaking analysis keeps Pump On/Off transient and defaults the owned pump off. When
  on, its horsepower and efficiency are fixed, read-only 1,000 hp and 85% values. Manual
  pumps retain their existing scenario-editable horsepower and efficiency fields.

## UI requirements

- Add **Build pump house** to the Snowmaking overview and list existing pump houses in a
  Buildings subsection.
- Before the first click, show instructions and the full hover preview. After the first
  click, explain that the pointer controls the long-axis direction and show the live
  heading.
- Review displays name, dimensions in current user units, numeric heading, fixed 4:12
  roof and calculated ridge height, foundation-mode toggle, site validity, and earthwork
  estimates while flattening is selected.
- Review and built detail display pump equipment as **1,000 hp / 85% efficiency** and
  economics as **Capital cost: TBD** and **Maintenance: TBD**.
- Disable confirmation while site analysis is pending or invalid, the footprint is out
  of bounds, another player building overlaps, or the relevant revision has gone stale.
- Built detail permits rename and remove only and displays the locked dimensions,
  bearing, roof, foundation mode, economics, and owned pump identity.
- Removal warns when the owned pump has connected pipes and states that pipe geometry
  remains but its ends will be detached.

## Agent execution sequence

The Sol-medium main composer establishes the shared interfaces and then runs two Luna
xhigh waves. Do not let two agents edit the same integration file.

### Wave one: independent foundations

1. **Domain Luna:** economics/building types, archetype registry, sanitization,
   canonical conversions, footprint/collision helpers, building document, and unit
   tests. Do not edit Sol-owned integration files.
2. **Terrain Luna:** building-site analysis, flattened-pad earthwork, slope sampling,
   worker protocol/adapter, stale-token behavior, and deterministic tests.
3. **Renderer Luna:** gabled mesh, custom WebGL layer, GeoJSON support layers,
   selection/visibility/capture lifecycle, Graphics Lab fixture, and tests.

### Wave two: feature behavior

1. **Controller Luna:** placement reducer/controller, map interaction ownership,
   previews, confirmation state, rename/remove commands, and tests.
2. **Snowmaking Luna:** owned pump creation/removal/detachment, document preparation
   support, fixed rating projection into analysis, inspector behavior, and tests.
3. **UI/E2E Luna:** new building review/detail components and deterministic Playwright
   coverage. Avoid Sol-owned CSS and integration wiring; report required selectors and
   styles to the composer.

### Sol integration and verification

The Sol owner integrates `MapView`, `MapGameDock`, Snowmaking composition, save/load,
selection, tool coordination, canonical map order, shared CSS, and compatibility facade.
It resolves the subagent interfaces, reviews all cross-domain invariants, and updates
`docs/architecture.md` only after the implementation has landed.

## Test and acceptance gates

Add deterministic coverage for:

- exact 60 ft by 40 ft by 16 ft conversions, 4:12 ridge rise and orientation, mesh
  winding/normals, rotation, stable generation, and catalog overrides;
- oriented-rectangle overlap rejection, allowed edge touching, full-bounds validation,
  and imported-building non-collision;
- economics placeholders and UI formatting;
- legacy schema hydration to `buildings: []`, current save round-trip, sanitizer behavior,
  and unsaved detection after add, rename, and remove;
- median flat datum, 6 ft pad apron, cut/fill daylight slopes, earthwork totals,
  out-of-bounds failure, slope mode's eight samples, 6 in clearance, and no terrain edit;
- stale terrain/network/building revisions, cancellation, worker termination, retained
  review state, and same-tick double confirmation;
- atomic building/node creation, pump numbering, reciprocal ownership, synchronized
  rename, independent-node edit prevention, pipe detachment, and terrain preservation on
  removal;
- fixed 1,000 hp / 85% analyzer behavior, explicit On/Off state, pressure contribution
  when connected, and unchanged manual-pump behavior;
- map and hit order, center-pump versus off-center-building selection, visibility,
  style-reload restoration, capture hide/restore, GPU cleanup, and render updates;
- the complete Playwright flow: both placement clicks, review edits, both foundation
  modes, overlap and bounds errors, confirmation, save/reload, selection, rename,
  analysis, connected removal, and style reload.

Before handoff or any benchmark commit, run:

```text
npm run check
npm run test:e2e
```

Do not treat legacy `scripts/verify*.mjs` scripts as release gates.

## Explicitly deferred

- Lodging and other building archetypes.
- User-editable roof pitch, materials, colors, doors, or windows.
- Capital spending, recurring maintenance simulation, or choosing daily versus monthly
  maintenance cadence.
- Post-build relocation, resizing, rotation, or regrading.
- Collision rules against imported buildings or non-building resort infrastructure.
- Reverting terrain or restoring vegetation when a building is removed.
