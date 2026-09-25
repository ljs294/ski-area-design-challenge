# Mountain Planner (Unity)

A ski resort tycoon built on real-world terrain. This branch is a from-scratch rebuild in **Unity 6.3 LTS** (Universal Render Pipeline), Windows desktop first.

- **Roadmap:** [docs/plans/unity-rebuild-roadmap.md](docs/plans/unity-rebuild-roadmap.md)
- **The previous MapLibre/TypeScript game** is frozen on branch `archive/maplibre` and tag `maplibre-final`. It is reference material only; see [docs/reference/maplibre-archive.md](docs/reference/maplibre-archive.md). To run it: `git checkout archive/maplibre` and follow that README.

## Requirements

- Unity Hub with **Unity 6.3 LTS** and Windows Build Support. The exact editor version is pinned in `ProjectSettings/ProjectVersion.txt`; use that version.
- Git with **Git LFS** (binary assets such as textures, models, audio and fonts are stored in LFS).
- Node.js 22 or newer, only for the repository checks.

## One-time setup per clone

1. Install the LFS hooks and fetch LFS content:

   ```sh
   git lfs install
   git lfs pull
   ```

2. Register Unity's YAML merge tool so scenes, prefabs and assets merge semantically (`.gitattributes` already routes those files to it). Add this to `.git/config`, replacing `<Editor>` with the installed editor folder, for example `C:/Program Files/Unity/Hub/Editor/<version>/Editor`:

   ```ini
   [merge "unityyamlmerge"]
       name = Unity SmartMerge (UnityYAMLMerge)
       driver = '<Editor>/Data/Tools/UnityYAMLMerge.exe' merge -h -p --force %O %B %A %A
       recursive = binary
   ```

   Check the command against the Smart Merge page of the installed editor's manual.

3. Open the repository folder in Unity Hub (**Add → Add project from disk**).

## Checks and tests

- Repository checks (agent docs, `.meta` integrity): `node tools/repo-checks/check.mjs`. Self-tests: `node --test tools/repo-checks/check.test.mjs`.
- Unity tests, with the editor closed:

  ```sh
  "<Editor>/Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -testResults test-results/editmode.xml
  ```

  Use `-testPlatform PlayMode` for play-mode tests.

GitHub Actions runs the repository checks on every pull request. Unity tests run locally until a GameCI workflow (which needs Unity license secrets) is added.

## Branches

- `main`: the current Unity version. Topic branches (`feature/*`, `fix/*`, `docs/*`) merge by pull request.
- `archive/unity`: fast-forwarded to `main` only at green milestones, each tagged `unity-mN`.
- `archive/maplibre` / `maplibre-final`: the frozen MapLibre game. `threejs-edition-final`: the earlier three.js experiment.
