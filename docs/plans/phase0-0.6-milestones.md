# Phase 0 · 0.6 Milestone plan, Phases 1–4

**Audience:** the project owner and coding agents. **Status:** draft for review (2026-09-25). Built on the approved [0.2](phase0-0.2-game-design.md), [0.3](phase0-0.3-technical-architecture.md), [0.4](phase0-0.4-ui-ux.md), [0.5](phase0-0.5-art-direction.md) and the [decision record](phase0-decisions.md). This replaces the phase table in [roadmap §13](unity-rebuild-roadmap.md#13-phases-and-planning-approach). Questions are in §7.

## 1. How the phases map to iterations

| Phase | Iteration | Outcome in one line |
|---|---|---|
| **1** | 1: just the mountain | **Vertical slice:** one real mountain, downloaded and rendered at target quality and performance, end to end |
| **2** | 1: just the mountain | **Iteration 1 complete:** every screen, polish, minimum spec verified, a shareable build |
| **3** | 2: drawing on the mountain | Trails, lifts and paths drawn on the terrain, saved and reloaded |
| **4** | 3: simulation foundations | Time, weather, snow and a first guest simulation on the drawn resort |
| Later | 4+ | More infrastructure (roads, ponds, dams, snowmaking, buildings), tycoon economy, release |

**Planning rule** (unchanged from roadmap §13): each phase's detailed plan is the last deliverable of the phase before it, approved by you before that phase starts. Phase 1's detailed plan is [0.7](phase0-0.7-phase1-plan.md).

## 2. Phase 1: mountain vertical slice

**Goal:** prove the whole iteration-1 pipeline and the look on real data, with just enough UI to drive it.

**Scope:**
- **Data spike first:** S1M block reads, the fallback service, canopy, WorldCover and NLCD access, and the coverage-index format (T4, T6, T10).
- **Assemblies migrated** to the T1 layout.
- **Acquisition command-line tool:**
  - builds the **2 km test terrain** (committed to the repo) and **Crystal Mountain** (5 km, fallback path)
  - produces package, cache and library entries (T5/T11)
  - produces the quality score and one-liner (T18)
- **World:**
  - Unity Terrain tiles, 1 m core and 2 m ring, seamless (T4)
  - non-blocky ground cover (T6)
  - forest with five archetypes (T7, 0.5 §3)
  - flat 12 in snow with bare rock over about 55° (T8, A2)
  - frozen lakes (T8)
  - the diorama-base edge (A1)
- **Presentation:** sun from the time and date scrubber, including moonlit night (T9, A4); colour grading; orbit and free-fly camera.
- **The style tile** (0.5 §7), reviewed with you **before** forest and terrain scale to full sites.
- **Functional UI**, basic styling:
  - picker pop-up (search, slider, click, name, quality preview) (T19)
  - download progress and the quality card
  - the HUD with layers (T17) and the time bar
  - a minimal main menu and library
- **Benchmark scene and budgets** (T13); determinism golden tests (T12); the `dotnet test` CI job (T14).

**Exit criteria:**
1. The picker downloads a new 2–5 km site from live providers, including a fallback area, and shows an honest quality score.
2. A downloaded mountain opens in ≤10 s **with the network disabled**.
3. The style-tile review passes: no blocky cover, no tile seams, no data seams.
4. Reference PC (RTX 3060 Ti): frame p95 ≤20 ms at 1080p High on Crystal Mountain.
5. Minimum-spec proxy: 30 FPS or better at Medium. It is verified on a real RTX 2060 in Phase 2 (question M1).
6. Golden tests are stable across repeated runs. Engine-free tests run in CI.
7. Velocity versus estimate is recorded, and the Phase 2 detailed plan is written.

**Estimate:** 12–14 weeks (revised in [0.7](phase0-0.7-phase1-plan.md) from the roadmap's 8–12).

**Top risks:**
- S1M or fallback surprises (the spike comes first to catch them).
- The Unity Terrain look at 1 m (fallback: a custom mesh behind `ITerrainSurface`).
- Forest cost.

## 3. Phase 2: iteration 1 complete

**Goal:** turn the slice into a finished, shareable "just the mountain" game.

**Scope:**
- **All screens from 0.4 at final quality, in both themes:**
  - the main menu over the live Crystal Mountain scene (G2)
  - the full library (rename, delete, resume, disk use)
  - settings with the Unity-native graphics menu (G8, U3), display, interface, controls and data
  - credits generated from manifests
  - photo mode (G3)
  - dialogs and the offline states
- **Background downloads** with resume (G5).
- **The satellite imagery layer** from NAIP (G7, nice to have; cut first if time is short).
- **The Crystal Mountain demo bundled** into the build (G2).
- **Performance on real minimum-spec hardware** (RTX 2060) at Medium; a 1-hour soak with no memory growth beyond 10%.
- **Build and release:** a Windows build pipeline and installer (or zip), a version number, crash logging, and a first build to share with testers.
- **Formats frozen:** from this build on, the package format has migrations (T11).

**Exit criteria:**
1. Every flow in 0.4 §3 works with keyboard only, at 50–150% UI scale.
2. Minimum spec at 30 FPS or better at 1080p Medium, verified on real hardware.
3. A clean install on a second PC opens the demo mountain offline.
4. A tester can download, explore and photograph a mountain without help.

**Estimate:** 2–3 months.

## 4. Phase 3: drawing on the mountain (iteration 2)

**Goal:** the player designs on the real terrain.

**Scope:**
- **The design document**, commands and undo (T15).
- **Save files** holding sparse terrain and clearing deltas over the package.
- **Tools:**
  - trail painting with difficulty, grading (level bench, 45° faces, never leaving the painted footprint) and tree clearing
  - lifts: two-point placement, type catalog with generic names (A2 in 0.1), towers, catenary rope, moving chairs
  - connector paths and junctions
  - the network connectivity check
- **Rules** from 0.1 §6 and §9 as the starting reference, re-specified in the Phase 3 detailed plan.

**Exit criteria:**
- A resort with trails and lifts can be drawn, graded, saved, reloaded and undone.
- Grading preview ≤100 ms; commit ≤250 ms.
- Performance budgets still met.

**Estimate:** 3–5 months.

## 5. Phase 4: simulation foundations (iteration 3)

**Goal:** the resort comes alive. This is where the roadmap's original guest-simulation slice moves (roadmap §6).

**Scope:**
- A real clock replacing the placeholder, on its own thread; snapshots out, commands in (roadmap §5, §7).
- **Weather** from an offline Daymet/NASA POWER package.
- **A snow model** writing the snow-depth texture.
- **Lakes that freeze and thaw.**
- **Guest simulation v1:** 3,000 guests riding lifts and skiing trails.
- A basic simulation bar and one dashboard.

**Exit criteria** (from roadmap §6, carried forward):
- Simulation tick within budget; golden trajectories stable.
- Frame budgets met with guests visible.
- Pause and selection p95 ≤100 ms.

**Estimate:** 4–6 months.

## 6. Dependencies and cross-phase risks

- **Dependencies:**
  - Phase 2 depends on the Phase 1 look and pipeline.
  - Phase 3 depends on the float32 source heights, delta saves and clearing mask (T15).
  - Phase 4 depends on Phase 3's network graph and the snow-depth texture seam.
- **Cross-phase risks:**
  - USGS or provider changes.
  - Unity pricing or policy.
  - Scope creep back toward the old game's full feature set (mitigated by this phase order).
  - Solo velocity. Each phase ends with a velocity review, and the next phase's estimate is revised.

## 7. Questions for you

Write your answer after each **Comment:**; "OK" accepts the recommendation.

**M1 · Minimum-spec hardware for testing.** Do you have access to an RTX 2060 (or similar) PC for Phase 2 verification? If not, Phase 1 uses a proxy (the reference PC at reduced settings), and a real test is arranged in Phase 2.
**Comment:**

**M2 · Phase order.** OK with drawing (Phase 3) before simulation (Phase 4)?
**Comment:**

**M3 · First shareable build.** Recommendation: at the end of Phase 2, to a few testers you choose, not public.
**Comment:**
