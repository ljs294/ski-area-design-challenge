# Ski Area Design Challenge

**by Alpine Labs**

Design a ski area on a real mountain. Every slope, cliff and forest comes from U.S. Geological Survey lidar, mapped to about a metre.

## Download and play

**[Download the latest release](https://github.com/ljs294/ski-area-design-challenge/releases/latest)**: under **Assets**, click the `.zip` file (about 370 MB).

You don't need to clone this repository, Unity or Git to play. Please don't clone it just to play: the repository's large files have a monthly download limit, and the release zip doesn't count toward it.

1. **Before unzipping**, right-click the zip, choose **Properties**, tick **Unblock** at the bottom and click **OK**.
   The game isn't signed yet, and without this step Windows may refuse to start it.
2. Unzip it anywhere you like (not inside Program Files), for example your Desktop.
3. Double-click **SkiAreaDesignChallenge.exe**.

If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.

To uninstall, delete the folder. Downloaded mountains and logs stay in `%LOCALAPPDATA%\SkiAreaDesignChallenge` until you delete that folder too.

### What you need

- Windows 10 or 11, 64-bit.
- A graphics card that supports DirectX 11 or 12.
- About 600 MB of disk space, plus about 200 MB for each mountain you download.
- An internet connection only for downloading new mountains.

The game is tested on Windows 11 with a GeForce RTX 3060 Ti. The minimum hardware hasn't been measured yet. If it runs slowly, lower **Settings › Graphics › Quality preset**.

## First run

The game opens on its title screen over **Jackson Hole**, which is built in and works offline. Choose it to fly around the mountain.

**New Area** downloads another U.S. mountain from public map data. That needs the internet and takes a few minutes. Afterwards, the mountain works offline too.

### Things to try

- Fly around, then press **4** to run the clock fast and watch the light move across the mountain until night.
- Press **P** for photo mode. Drag the time slider, change the date and lens, then press **Space** to save a picture to `Pictures\Ski Area Design Challenge`.
- Turn on **Slope angle** (**Shift+7**) to see which slopes are steep enough to ski, and **Contours** (**Shift+6**).
- Download a mountain you know, and check whether its trees and cliffs look right.

Each release's notes say what's new in that version.

### Controls

| Action | Keys |
|---|---|
| Move | W A S D or the arrows; right-drag drags the ground |
| Rotate | Q / E; middle-drag rotates and tilts |
| Tilt | R / F |
| Zoom | Mouse wheel, + / − |
| Faster | Hold Shift |
| Free-fly camera on/off | C |
| Reset the view | Home |
| Photo mode | P |
| Hide all the on-screen panels | H |
| Pause, game speed | Space, 1–4 |
| Analysis | Tab |
| Feet or metres | U |
| Menu, or back one step | Esc |

**Settings › Controls** changes any key.

## Feedback and problems

We'd love to hear what you think, especially what was confusing, slow or broken.

- Tell the person who sent you the link, or [open an issue](https://github.com/ljs294/ski-area-design-challenge/issues) on GitHub.
- When something goes wrong, the game saves an error log and shows "A log was saved". To send it, open **Settings › Data › Logs**, click **Copy report** and paste the report into your message. The logs are in `%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs`.
- Each report starts with the game's version and your computer's details (Windows, processor, graphics card, screen and settings). It holds nothing else about you.
- Please include the version, shown at the bottom left of the title screen, and what you were doing.

## Credits

Made by Alpine Labs with Unity 6 and the Universal Render Pipeline.

- **Elevation and lidar:** U.S. Geological Survey, 3D Elevation Program (public domain).
- **Base map:** U.S. Geological Survey, The National Map (public domain).
- **Land cover:** ESA WorldCover (CC BY 4.0) and USDA NAIP imagery (public domain).
- **Roads and place search:** © OpenStreetMap contributors (ODbL).
- **Ground textures:** Poly Haven (CC0).
- **Fonts:** Overpass and Overpass Mono (SIL Open Font License 1.1).

Each mountain's own sources are listed in the game under **Credits**. The `NOTICES.txt` in the zip has the full licence texts.

Copyright Alpine Labs. All rights reserved.

---

## For developers

This repository is the game's source: a from-scratch rebuild in **Unity 6.3 LTS** (Universal Render Pipeline), Windows desktop first. Building it needs Unity and Git LFS. Agents and contributors start with [AGENTS.md](AGENTS.md).

- **Roadmap:** [docs/plans/unity-rebuild-roadmap.md](docs/plans/unity-rebuild-roadmap.md)
- **The previous MapLibre/TypeScript game** is frozen on branch `archive/maplibre` and tag `maplibre-final`. It is reference material only; see [docs/reference/maplibre-archive.md](docs/reference/maplibre-archive.md).

> **LFS bandwidth:** a clone with `git lfs pull` downloads several hundred MB of textures, models and data. GitHub's free plan meters LFS downloads every month, so clone only to work on the game. To play, use the release zip.

### Requirements

- Unity Hub with **Unity 6.3 LTS** and Windows Build Support. The exact editor version is pinned in `ProjectSettings/ProjectVersion.txt`; use that version.
- Git with **Git LFS**. Binary assets such as textures, models, audio and fonts are stored in LFS.
- Node.js 22 or newer for the repository checks, and the .NET SDK for the engine-free tests.

### One-time setup per clone

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

### Checks, tests and demos

- **Repository checks** (agent docs, `.meta` integrity, banned APIs): `node tools/repo-checks/check.mjs`. Self-tests: `node --test tools/repo-checks/check.test.mjs`.
- **Engine-free tests, without Unity:** `dotnet test tools/domain-tests/Tests`.
- **Unity tests**, with the editor closed:

  ```sh
  "<Editor>/Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -testResults test-results/editmode.xml
  ```

  Use `-testPlatform PlayMode` for play-mode tests.

- **Hands-on demos:** double-click `demo.bat` for a menu of builds, captures and tools.

GitHub Actions runs the repository checks and the engine-free tests on every pull request. Unity tests run locally.

### Releases

`release.bat` (or `demo.bat` 58) runs the checks and EditMode tests, builds the game with the bundled demo, smoke-tests the exe and zips it. A clean commit tagged `vX.Y.Z` builds release X.Y.Z; see [tools/release/README.md](tools/release/README.md). Releases are published as GitHub Releases with the zip attached, so players never need LFS.

### Branches

- `main`: the current Unity version. Topic branches (`feature/*`, `fix/*`, `docs/*`) merge by pull request.
- `archive/unity`: fast-forwarded to `main` only at green milestones, each tagged `unity-mN`.
- `archive/maplibre` / `maplibre-final`: the frozen MapLibre game. `threejs-edition-final`: the earlier three.js experiment.
