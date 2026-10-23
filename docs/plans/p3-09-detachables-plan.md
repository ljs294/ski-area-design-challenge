# P3-09 · Detachable lifts: the Sessellift D6 first, gondola-ready code (plan)

**Status:** approved by the owner on 2026-10-23 (PR C of P3-09). **Supersedes** the first draft of this file (the six
questions are answered below). Rulings: LT41 in [phase0-decisions.md](phase0-decisions.md).

## Context
The owner pulled detachables into P3-09 (2026-10-23). The Sessellift D6 six-pack models are in main (PR #96): drive and
return terminals with a station rail (`paths.chairs`), a six-seat detachable chair, and six wide tower heads (s4, s6,
s10, b10, d10, c8). In game today every non-quad id is a placeholder drawn with the fixed-grip quad's parts. Chairs move
round one loop at one speed, and heads stop at 8 sheaves. The owner wants:

- the D6 buildable, with chairs that slow into the stations like a real detachable;
- realistic spacing from research, roughly twice a fixed grip's: defaults now, sliders later;
- the other lifts in the menu but not buildable;
- two lift colours, primary and accent;
- code that gondolas can reuse later. Gondola models are out of scope, and procedural lift names on the models are a
  future feature.

Owner answers (2026-10-23): colours are type defaults now, picker later; station baseline 1.0 / 1.4 m/s with 1.0 m/s²
ramps. Earlier: 1. PR #94 first, detachables as PR C. 2. No gondolas. 3. Others are menu placeholders only. 4. Swing
checked per type. 5. Primary and accent colours. 6. Research-based defaults now, sliders later.

## Spacing and speeds (research, back-calculated)
The rule:
- interval t = 3600 × seats ÷ capacity;
- line spacing = line speed × t;
- station spacing = station speed × t.

Sources:
- A high-speed quad moves about 2,400 an hour at about 5 m/s ([Wikipedia, Chairlift](https://en.wikipedia.org/wiki/Chairlift)).
- Six-packs run 1,600–3,600 an hour, many at 3,000 to 3,200 ([skiresort.info](https://www.skiresort.info/ski-lifts/lift-types/magic-pass/lift-type/6-pers-chairlifts/)).
- Snowriver's six-pack: 1,000 fpm, 53 chairs, 2,800 an hour ([Snow Industry News](https://www.snowindustrynews.com/articles/snowriver-mountain-resort-announces-new-high-speed-six-pack-to-be-installed-for-23-24-season)).
- Leitner stations: boarding about 1 m/s, deboarding about 1.3–1.5 m/s ([Leitner short station](https://www.leitner.com/fileadmin/userdaten/00-home/Ordner-Facelift/PDF_s_Logo_neu/Station_sheets/The_LEITNER_Station_short_station.pdf)).
- Fixed-grip quads load about 10 chairs a minute ([SkiTalk](https://www.skitalk.com/threads/detachable-chairs-can-someone-school-me.32143/page-4)).

| Type | Line | Capacity | Interval | Line spacing | vs fixed |
| --- | --- | --- | --- | --- | --- |
| Fixed-grip quad (today) | 450 fpm | 2,400 | 6.0 s | 45 ft | 1× |
| Detachable quad | 1,000 fpm | 2,400 | 6.0 s | **100 ft** (was 80) | 2.2× |
| **Detachable six (D6)** | 1,000 fpm | 3,000 | 7.2 s | **120 ft** (was 100; 100 ft = 3,600 max, G4) | 2.7× |
| Detachable eight | 1,000 fpm | 3,600 | 8.0 s | **135 ft** (was 120) | 3.0× |

**Stations (D6):**
- Boarding is 1.0 m/s at the bottom (the return), so chairs are 7.2 m apart at loading.
- Deboarding is 1.4 m/s at the top (the drive), so chairs are 10 m apart there.
- Both are well over G4's minimums of 3.5 m and 2.5 m.
- Deceleration and acceleration on the tyre conveyors are 1.0 m/s². That's a game choice, since no source gave it: the
  5.08 → 1.0 m/s ramp takes about 12 m.
- All of these are named constants on the type, so gondolas reuse them with their own values.
- **They scale with line speed (owner):** the type stores its station speeds as fractions of its line speed. Boarding is
  1.0 ÷ 5.08 ≈ 0.20 of line speed, and unloading 1.4 ÷ 5.08 ≈ 0.28. Each is kept within a realistic band (boarding
  0.8–1.2 m/s, unloading 1.1–1.6 m/s), and the ramps keep their 1.0 m/s² rate.
  - The type also stores its **interval** (7.2 s for the D6), not a spacing; line spacing = line speed × interval.
  - So when the player can set the line speed later (with the sliders), the loading speed, the ramps' length and the
    spacing on the line all follow automatically. Capacity stays seats × 3600 ÷ interval.
  - Today line speed is the catalog's; the code takes it as a parameter, so a speed slider needs no rework here.

## Approach

### 1. Detachable types and catalog (Domain)
- **`LiftType` additions**, in `Runtime/Domain/Design/LiftRope.cs`:
  - `Detachable`;
  - `BoardSpeed`, `AlightSpeed` and `RampAccel` (m/s, m/s²);
  - `Buildable`.
- **Per-type figures:**
  - carrier drop, gauge, rope and chair half-width come from the type, not the quad;
  - non-quad types get a real rated tension instead of ∞.
- **`detach-six:sessellift` becomes the D6:**
  - 2 in (50.8 mm) rope, 5.6 m gauge, 3.27 m drop (G3);
  - 120 ft spacing and the real rated tension;
  - ends `sessellift_d6_return` (bottom: tensions, hold-down) and `sessellift_d6_drive` (top);
  - rows and exit ranges measured from the models, as `LiftTypes.Measured` already does;
  - D6 terminals have no rocker pivots, so they pose fixed (LT33's fallback).
- **Spacing defaults** for detach-quad and detach-eight per the table; gondola entries keep theirs.
- **`Buildable`** is true only for the FGQ-4, the Monta FG4 and the D6.
  - `DesignRules.LiftLineProblem` (`Runtime/Domain/Design/DesignRules.cs:115`) rejects non-buildable ids for new lifts.
  - Existing saves still load.
- **New helpers:** `LiftType.IntervalSeconds` (stored for detachables; spacing ÷ speed for fixed grips),
  `BoardSpeed(lineSpeed)` and `AlightSpeed(lineSpeed)` (fractions of line speed within their bands), and a
  `StationTime(pathLength)` used by capacity and ride time.
  - Ride = line ÷ line speed + each station's rail ÷ its speed + the ramps.
  - Test: doubling the line speed scales loading speed and line spacing, and keeps capacity.

### 2. Heads per type: S10, B10, D10 (G6)
- `LiftRope.Head` (`LiftRope.cs:177`) gains `S10, B10, D10`.
- **`TowerForces.Choose`** (`Runtime/Domain/Design/TowerForces.cs:84-94`) takes the type's head set:
  - D6: S4, S6, S10, B10, D10, C8;
  - quads: S4, S6, B8, D8, C8.
  - The caps are per set, not a fixed 8.
- **Users updated:**
  - `LiftView.HeadId` (`Presentation/Lifts/LiftView.cs:350`);
  - `LiftDraftEditor.HeadName`;
  - `LiftLoadGauge`;
  - `LiftProfileStrip`;
  - `LiftLab`.
- **Per-type tower kit (G2):** the hardcoded `_prefix` (`LiftView.cs:68`) becomes the type's kit prefix.
  - Heads come from the type's own kit.
  - Mast, bases and footing fall back to `sessellift_fgq4_` where the kit has none.
- Rope height per head is already read from sockets (G7).

### 3. Stations: chairs slow into the terminals (G2)
- **Import**, in `Editor/LiftImport.cs`:
  - read `paths.chairs` from `lifts.json` (exported by `tools/assets/lifts/build_lifts.py:234`) onto the prefab's rig, as a
    new `LiftRig.Paths` (`World/Lifts/LiftRig.cs`);
  - read the pivots' `follow` field (G8).
- **The loop**, in `LiftView` (rope and carriers):
  - The rope's station run goes through `rope_*_bend` (`StationRope`, `LiftView.cs:450`).
  - Carriers get their own loop: line spans at rope speed, then each station's rail path.
  - A `CarrierTrack` (new, `Presentation/Lifts/CarrierTrack.cs`) holds the distance
    along the track and the speed at each point:
    - line speed on the rope;
    - ramps at `RampAccel`;
    - `AlightSpeed` from the top station's rail start to the unload socket;
    - `BoardSpeed` round the bottom.
  - It precomputes cumulative time, so position(t) is a binary search. No allocation.
- **Carrier positions:** carrier k sits at time-phase `k × interval + t` on the time loop.
  - That spaces them evenly in time, so they bunch in stations and spread on the line, exactly like the real lift.
  - Count = loop time ÷ interval.
  - `PrepareChairs` and `DrawChairs` (`LiftView.cs:570`, `:642`) switch from distance spacing to time spacing.
  - Fixed grips stay a single-speed track, with the same code path.
- **Footings** at `pier_top_1/2` already match (M4 code).
- **G8:** `follow` pivots (the walkway) turn to their train's angle in `TrainPosing`.
- Gondolas later reuse the track: a cabin carrier, its own speeds and its own station path.

### 4. Swing (G5), checked per type
- An EditMode asset test sweeps the D6 chair at ±11.5° against every head of its kit, both sides, and passing chairs.
  - Clearances: 230 mm to the mast and 1.07 m between passing chairs, as the model findings say.
  - Rest ground clearance stays TW-01 v2.
- No in-game wind rule (Phase 4).

### 5. Lift colours: primary and accent
- Each type gets default **primary and accent** colours: the FGQ-4 and Monta red with dark accent, the D6 black with
  white accent.
- **`LiftMaterials`/`LiftView`** take two colours:
  - the livery renderers get the primary;
  - a second accent slot (hood trim, chair, cabin later) gets the accent.
  - The importer marks the accent renderers. Parts for the accent come from the model's material names.
- Per-lift choice is a later slider/picker (question below), so no save change now.
- **Future, noted only:** procedural lift names shown on the models (name boards).

### 6. Menu and tools
- **`LiftTypeMenu`** (`Runtime/UI/Tools/LiftTypeMenu.cs`) shows every type, with non-buildable ones greyed out ("in the
  menu only for now").
- The D6 is selectable as Detachable chair › Six › Sessellift D6.
- New lift, Edit lift, the tower editor, the ghost and the draft all work unchanged; the ghost shows the type's heads.
- **The review** shows:
  - the interval ("a chair every 7.2 s");
  - capacity;
  - ride time with stations;
  - chairs on the line and in the stations.
- `-liftbench`'s Monta third stays, and a third becomes D6.

### 7. Docs and rulings
- Record LT41 (detachables: spacing by interval, station speeds, buildable set, two colours) in
  `docs/plans/phase0-decisions.md` and the P3-09 memory.
- Update `docs/plans/p3-09-detachables-plan.md` with these answers and the research.
- Design rules: LC-03 v2 (capacity from interval), LC-08 v2 (catalog), TW-04 v2 (heads per type).

## Delivery
PR #94 merges first (owner). Then a new branch from main, `feature/p3-09-detachables`, as PR C.

Order:
1. Catalog and spacing;
2. heads and kits;
3. colours;
4. stations and carrier track;
5. swing test;
6. menu, review and demo 68 notes.

Each step keeps the tests green.

## Verification
- **`dotnet test tools/domain-tests/Tests`:**
  - the D6 figures;
  - interval, capacity and ride time;
  - head choice per set (a 9–10 sheave bend is carried on the D6 and too sharp on a quad);
  - non-buildable ids rejected;
  - the fan and Sugarloaf with the D6 (most lines within every rule);
  - fixed-grip goldens unchanged.
- **EditMode (Unity batch):**
  - the import reads `paths`;
  - CarrierTrack gives even time spacing, minimum station gaps ≥ 3.5 m and 2.5 m, speeds at 1.0 and 1.4 m/s, and
    allocation-free position(t);
  - the swing sweep;
  - the menu greys non-buildables;
  - colours reach the renderers.
- **PlayMode (render lane):** a D6 placed by keys and mouse, Break ground, chairs running.
- **Captures (render lane, `-screenshot`, single `-view` each):**
  - D6 bottom station with chairs bunched on the rail;
  - D6 top station;
  - the line with a 10-sheave head;
  - black and white colours.
- **Benchmark (measure lane):** demo 41 plus `-liftbench 40` with D6s, compared against the baseline; chair cost within
  budget; re-pin the lifts baseline.
- `node tools/repo-checks/check.mjs`, `tools/release/compile-check.ps1`, and demo 68 notes updated.
