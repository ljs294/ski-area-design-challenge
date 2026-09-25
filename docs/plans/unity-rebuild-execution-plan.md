# Unity rebuild execution plan: preserve the MapLibre game, split the repo, begin the from-scratch Unity rebuild

## Context

The MapLibre/TypeScript game stutters as guest counts grow. An audit found the causes (Document 1). You then chose to leave that codebase and **rebuild the game completely from scratch in Unity**, not port it. Unity is the chosen engine. The rebuild stays in this repository.

The work covers three things, and nothing else:
- **Preservation:** keep the current game intact, together with both planning documents.
- **Repo split:** `main` becomes the Unity version you're working on, with archives for the MapLibre version and a rolling archive for Unity.
- **The Unity path:** Phase 0 of the rebuild.

The MapLibre Phase A optimizations are **not** executed. Document 1 is preserved as a record.

## Answers to your review comments

- **Is this a new repository, and is `origin/main` overwritten?**
  - No new repository.
  - **`main`'s files are completely replaced.** After the bootstrap merges, `main` contains only the Unity project, docs and tools; no MapLibre files remain on it.
  - **`main`'s history is kept (your decision).** The replacement arrives as one ordinary new commit merged by PR, so nothing is force-pushed. `git log main` still lists every MapLibre commit before the replacement commit.
  - Either way, the complete MapLibre game stays one checkout away: `git checkout archive/maplibre`, or tag `maplibre-final` on GitHub.
- **`main` should match the current version, with archives for MapLibre and a rolling archive for Unity.** Agreed, and adopted. See Document 2 §15:
  - `main` = the Unity project.
  - `archive/maplibre` and tag `maplibre-final` = frozen MapLibre game.
  - `archive/unity` = a rolling branch fast-forwarded to `main` at each green milestone, plus immutable `unity-mN` tags.
- **UI tech stack:** **Unity UI Toolkit** (Unity 6.3 LTS). It has:
  - USS stylesheets with CSS-like variables, so today's design tokens and look port over directly.
  - Sharp signed-distance-field (SDF) text and native SVG vector icons.
  - Runtime data binding for dashboards, and world-space panels for in-world labels.
  - `Painter2D` for charts.

  Game-flow improvements are designed in Phase 0 (deliverable 0.4) before any UI is built. Details are in Document 2 §11.
- **When are the phase plans written?**
  - Phase 0 writes the milestone-level plan for **every** phase (scope, exit criteria, dependencies, risks) and the **detailed** plan for Phase 1.
  - Each later phase's detailed implementation plan is the final deliverable of the phase before it (rolling-wave planning), because what we learn in each phase changes the next. You approve each detailed plan before its phase starts. See Document 2 §13.
- **Rebuild, not port:** stated as the defining constraint in Document 2 §1. No code is carried over, embedded or transpiled. The archive is reference material only.

## What happens when you approve

Approval authorizes the local commits, tags and branches below. **Every action on GitHub waits for your explicit confirmation at that step:** pushes, PR creation and merges, and branch protection.

1. **Final MapLibre commit on `simulation`, the preserved tree.**
   - Add Document 1 → `docs/plans/guest-performance-optimization.md` and Document 2 → `docs/plans/unity-rebuild-roadmap.md`, verbatim.
   - Add a one-line status to `AGENTS.md`: "Archived: this is the final MapLibre version (`archive/maplibre`, tag `maplibre-final`); active development is the Unity rebuild on `main`." It stays well under the 8,000-character limit.
   - Add a matching note in `README.md`.
   - Run `npm run check:agent-docs` and `npm run check`. If `npm run check` fails for pre-existing reasons, I'll record the exact result in the README note rather than fix product code. The archive preserves the game as it is.
2. **Fast-forward `main` to `simulation`** (`main` is currently 6 commits behind and 0 ahead). With your confirmation, run `git push origin simulation:main`. This fails safely if it isn't a fast-forward, and it keeps the commit SHAs identical.
3. **Preserve:**
   - Annotated tag `maplibre-final` at that commit.
   - Branch `archive/maplibre` at the same commit.
   - Annotated tag `threejs-edition-final` at `eb14b0c`, the earlier three.js experiment. Its branch stays too.
   - Push the tags and the archive branch (with confirmation). Then either you lock `archive/maplibre` in GitHub branch settings, or I do it with `gh api` if you confirm.
4. **Bootstrap branch `feature/unity-bootstrap` from `main`**, merged by one PR once the Unity project opens cleanly:
   - **Commit A, clean slate:**
     - `git rm -r` the MapLibre tree.
     - Add the new root `README.md`, a new Unity-focused `AGENTS.md` (draft in Document 2 §15) and `CLAUDE.md` (`@AGENTS.md`).
     - Add the Unity `.gitignore` and a `.gitattributes` for Git LFS and Unity YAML merge.
     - Carry over `docs/plans/unity-rebuild-roadmap.md`.
     - Add `docs/reference/maplibre-archive.md`: an index of GitHub links at `maplibre-final` into the archived code and docs, including Document 1.
     - Add `tools/repo-checks/`: zero-dependency Node scripts that port the agent-docs checks and add a `.meta` integrity check.
     - Add `.github/workflows/repo-checks.yml`. The old web CI and Pages workflows leave `main`; the `web-demo` Pages site is unaffected.
   - **Commit B, Unity project.** Requires Unity 6.3 LTS, which isn't installed on this PC yet (UE 5.8 is).
     - Create the project from Unity Hub's Universal 3D (URP) template in a temporary folder, then move `Assets/`, `Packages/` and `ProjectSettings/` to the repo root. Unity Hub won't create a project in a non-empty folder.
     - Pin the version and confirm Force Text serialization and Visible Meta Files.
     - Add the assembly skeleton (`Simulation`, `World`, `Presentation`, `UI`, plus tests) with one EditMode smoke test.
   - **After you confirm the merge:** tag `unity-m0` and create the rolling `archive/unity` branch at it.
5. **Phase 0 planning deliverables** (Document 2 §13), written as docs on `main` through `docs/*` PRs in the order 0.1 → 0.7. Each is reviewed by you before anything depends on it.

## Verification

- **Step 1:** `npm run check:agent-docs` passes, and the `npm run check` result is recorded.
- **Steps 2–3:**
  - `git rev-parse maplibre-final^{commit}` equals `archive/maplibre` and the pre-bootstrap `main`.
  - `git ls-remote origin` shows the tags and branches.
- **Proof the archive is intact:** `git worktree add ../mp-archive archive/maplibre`, then `npm ci` and `npm run build:desktop` in that worktree. Optionally launch it with `launch-game.bat`.
- **Step 4:**
  - `node tools/repo-checks/check.mjs` passes, including agent-docs pairing, size and links, and `.meta` integrity.
  - `git lfs ls-files` lists the binaries.
  - The project opens in Unity 6.3 LTS without errors.
  - A batchmode EditMode run passes: `Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode`.
  - The `repo-checks` workflow is green on the PR.


The two documents referenced above are saved as [guest-performance-optimization.md](guest-performance-optimization.md) and [unity-rebuild-roadmap.md](unity-rebuild-roadmap.md).
