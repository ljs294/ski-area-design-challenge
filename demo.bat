@echo off
rem Hands-on demo launcher. Double-click it, or run it from a terminal.
rem Each phase adds its demos here; the Unity game gets a "Play" entry once it builds.
setlocal EnableExtensions
rem The tools print UTF-8 (arrows, degree signs); switch the console only while they run, because
rem set /p misreads input under code page 65001.
for /f "tokens=2 delims=:." %%c in ('chcp ^<nul') do set "OLDCP=%%c"
set "OLDCP=%OLDCP: =%"
set "EMPTY=0"
title Ski Area Design Challenge - demos
set "SPIKE=%~dp0tools\data-spike"
set "OUT=%SPIKE%\results\local"
set "PACKAGES=%LOCALAPPDATA%\SkiAreaDesignChallenge\Resorts"
set "GAME=%~dp0Builds\Windows\SkiAreaDesignChallenge.exe"
set "GAMEDEV=%~dp0Builds\WindowsDev\SkiAreaDesignChallenge.exe"
set "BENCH=%~dp0test-results\benchmark"
rem Benchmarks and captures run in a 1920x1080 window for that run only (-benchres); never pass -screen-*,
rem which Unity saves as the player's window mode.
set "SCREEN=-benchres 1920x1080"
rem Benchmarks take the GPU alone (measure lane); screenshot and UI captures share it (render lane, 2 at once).
rem tools\gpu\gpu.ps1 waits for a free slot, so these never collide with a thread's run; Ctrl+C cancels the wait.
set "GPURUN=powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\gpu\gpu.ps1" run -Name owner"
set "MEASURE=%GPURUN% -Lane measure --"
set "RENDER=%GPURUN% -Lane render --"
set "LIFTLAB=%~dp0Builds\LiftLab\LiftLab.exe"
set "PICKER=%~dp0Builds\PickerLab\PickerLab.exe"
set "PICKED=%LOCALAPPDATA%\SkiAreaDesignChallenge\picked-site.args"
set "SCRATCH=%LOCALAPPDATA%\SkiAreaDesignChallenge-scratch"
set "FORMATS=%LOCALAPPDATA%\SkiAreaDesignChallenge-formats"
set "LIBDEMO=%LOCALAPPDATA%\SkiAreaDesignChallenge-library"
set "FIRSTRUN=%LOCALAPPDATA%\SkiAreaDesignChallenge-firstrun"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe"
rem Task P2-10: the company is Alpine Labs; Unity keeps the player's prefs and Player.log under it.
set "PLAYERLOG=%USERPROFILE%\AppData\LocalLow\Alpine Labs\Ski Area Design Challenge\Player.log"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK is not installed. Get the free .NET 10 SDK from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)

:menu
cls
echo ============================================================
echo   Ski Area Design Challenge - demos
echo ============================================================
echo.
echo   Phase 1, task 01: terrain data spike
echo     1  Offline tests (no internet, about 10 seconds)
echo     2  Jackson Hole, 2 km (test terrain, about 1 minute)
echo     3  Jackson Hole, 5 km (demo mountain, about 2 minutes)
echo     4  Crystal Mountain, 5 km (fallback terrain, about 2 minutes)
echo     5  Any site: enter a name, latitude, longitude and size
echo     6  Count the S1M 1 m lidar tiles available today
echo     7  Open the results folder
echo.
echo   Phase 1, task 02: code modules
echo     8  Engine-free tests in plain .NET (no Unity, a few seconds)
echo     9  Repository checks, including the banned-API guard
echo     10 Unity tests, EditMode and PlayMode (close the Unity editor first; a few minutes)
echo.
echo   Phase 1, tasks 04-05: download a mountain into your library
echo     11 Jackson Hole, 2 km (S1M lidar; about 10 seconds the first time)
echo     12 Jackson Hole, 5 km (the demo mountain; about a minute)
echo     13 Crystal Mountain, 2 km (fallback terrain from 3DEP; about 20 seconds)
echo     14 Any site: enter a name, latitude, longitude and size
echo     15 Open your library folder
echo     16 List your mountains (names, both quality scores, disk use)
echo        Tip: press Ctrl+C during a download, then choose it again - it resumes.
echo.
echo   Phase 1, task 06: the mountain in Unity
echo     17 Fly over your mountain in the game (builds the game the first time: about 2 minutes)
echo     18 Rebuild the game (after pulling new code; close the Unity editor first)
echo        Tip: re-run 12 once to add OpenStreetMap water and roads to an older Jackson Hole download.
echo     19 Rebuild the trees from tools\assets\trees and import them (Blender 5.2; close the Unity editor), then 18
echo.
echo   Phase 1, tree realism review
echo     20 Tree lineup: every species at every LOD, trunks, and a stand from 300 m to 3 km (screenshots, about 15 seconds)
echo     21 Benchmark at High: the fixed camera path over Jackson Hole, frame and GPU times per leg, screenshots (about 4 minutes; needs 12)
echo.
echo   Lift assets: Sessellift FGQ-4 chairlift and SLE snow guns
echo     22 Rebuild the lifts, chairs and snow guns in Blender, import them into Unity and build the Lift Lab (about 4 minutes)
echo     23 Open the Lift Lab: terminals, chair, towers (6), snow guns (7, 8), Monta (9), chairs (0), LODs, snow, benchmark (B)
echo.
echo   Phase 1, task 09: forest at scale
echo     24 Crystal Mountain, 5 km: download it into your library (Cascades species; about 3 minutes)
echo     25 Fly over Crystal Mountain in the game (needs 24)
echo     26 Benchmark on Crystal Mountain: a path through its own views, GPU times, draw calls (about 3 minutes; needs 24)
echo     27 Forest report for every mountain you have: trees, species, treeline (a few seconds each)
echo     28 Species survey: tree species at every US ski area, then the model priority report (about 3 hours; resumes)
echo.
echo   Phase 1, task 09 phase 2: New England tree species
echo     29 Sugarloaf, Maine, 5 km: download it into your library (spruce-fir, krummholz, northern hardwoods; about 3 minutes)
echo     30 Fly over Sugarloaf in the game (needs 29)
echo     31 Benchmark on Sugarloaf: a path through its own views, GPU times, draw calls (about 3 minutes; needs 29)
echo.
echo   Game UI design (mockups, not the game yet)
echo     32 Open the HUD layout mockup in your browser: status bar, Toolbox, Analysis, menu and Settings
echo.
echo   Forest structure (NE8)
echo     33 Stand in a Sugarloaf spruce-fir stand: understory and clumps (needs 29)
echo     34 Add roads to mountains downloaded before roads (one-time map refresh; needs the internet)
echo.
echo   Phase 1, task 13: site picker
echo     35 Pick a site on the map, then download it into your library (builds the Picker Lab the first time)
echo     36 The picker with no network: the offline panel
echo     37 Rebuild the Picker Lab (after pulling new code; close the Unity editor first)
echo.
echo   Phase 1, task 14: title, download, quality card and library (a scratch library, not yours)
echo     38 Play from the title: New Area, download with progress, quality card, Load Area, Manage Areas
echo     39 The same scratch library with the network off: Load Area, then open an area
echo     40 Empty the scratch library
echo        Tip: in 38, close the game mid-download, start 38 again, then Manage Areas - Resume.
echo.
echo   Phase 1, task 15: benchmark and budgets (needs 12; close the Unity editor first)
echo     41 Full benchmark: High and Medium against their budgets, garbage and memory, compared with the baseline (about 25 minutes)
echo     42 Benchmark one quality preset: Low, Medium, High or Ultra (about 4 minutes)
echo     43 Rebuild the Development game (exact garbage and memory counters for 41)
echo.
echo   Phase 2, task 08: formats frozen (a scratch library, not yours)
echo     44 Phase 1 files open as before, beside an area from a newer game: greyed in Load Area, deletable in Manage Areas
echo     45 A library folder from a newer game: Load Area lists nothing and says why
echo     46 Format tests: the Phase 1 fixtures, newer files refused, migrations (a few seconds)
echo.
echo   Phase 2, task 01: UI foundation (needs a downloaded area; close the Unity editor first)
echo     47 Capture every screen at 1920x1080, 2560x1080, 3440x1440 and 5120x1440, dark and light, 50-150%% (about 15 minutes)
echo        Then try it by hand: Settings - Theme (Dark, Light, Auto) and Interface scale; Tab, the arrows, Enter and Esc on every screen.
echo.
echo   Phase 2, task 02: the HUD (needs a downloaded area; close the Unity editor first)
echo     48 The HUD next to the mockup: every HUD state captured and measured against ui-layout.html (about 10 minutes)
echo        Then play it: Space and 1-4 run the sun, T Toolbox, Tab Analysis, the top-right buttons; Esc steps back.
echo.
echo   Phase 2, task 04: library and cache housekeeping (a scratch library, not yours)
echo     49 Manage Areas: Rename (F2), disk use per area and in total, sort, Free space for an older version's cache, Delete
echo.
echo   Fix: the window mode
echo     50 Reset the game's saved window mode to borderless full screen (once, if earlier benchmarks left it windowed)
echo.
echo   Phase 2, task 09: forest look (needs Jackson Hole 5 km, 12; close the Unity editor first)
echo     51 Far trees from straight overhead (LOD0, LOD2, impostors), fir undersides and winter leaves captured, then the game
echo        opens looking down on the forest: Shift+1 turns the snow off to see the forest floor, the wheel zooms out to the ring.
echo.
echo   Phase 2, task 03: the title over the built-in demo (needs 12 once, for the build; close the Unity editor first)
echo     52 A fresh install, offline: no library, the title over Jackson Hole drifting in the afternoon sun, then Open the demo
echo.
echo   Phase 2, task 05: settings (needs a downloaded area; close the Unity editor first)
echo     53 Capture every Settings page at 1920x1080, 2560x1080 and 3440x1440, dark and light, 50-150%% (about 5 minutes)
echo        Then play it: Settings from the title or the in-game menu. Graphics changes show at once; Display asks to keep
echo        a new mode or resolution; Controls - click a key, press the new one; Data - Offline mode, Free space.
echo.
echo   Phase 2, task 06: background downloads, dialogs and offline (needs the network; close the Unity editor first)
echo     54 Kill the game mid-download and start it again: the download resumes by itself from the stage it reached
echo        Then play it with 38: start a download, Minimise, open an area - the pill is in the HUD bar; pull the
echo        network cable - it waits and goes on by itself; Settings - Data - Offline mode on, then New Area.
echo.
echo   Phase 2, task 07: photo mode and credits (needs a downloaded area; close the Unity editor first)
echo     55 Capture the photo bar (Off, Natural, Miniature, golden hour, Postcard 2x) and Credits in both themes, time five
echo        2x saves for the hitch check, then play it: P opens photo mode; Space saves to Pictures; the menu has Credits.
echo.
echo   Phase 2, task 03.1: the loading screen and the brand (needs 12 once, for the build; close the Unity editor first)
echo     56 A fresh install with loading details on: the Twin Peaks logos on the loading box, the stage list ticking off,
echo        then the title's name plate (the game's logo, Alpine Labs below). Details stays on next launch until you hide it.
echo     57 Re-capture the loading screen's picture of the title view (7680x2160) after the title's look changes
echo.
echo   Phase 2, task 10: build and release (close the Unity editor first; needs the Jackson Hole 5 km from 12)
echo     58 Build the release zip (checks, EditMode tests, build, smoke test, zip; about 15 minutes), then start the
echo        unzipped game with a forced error: the version is bottom left, "A log was saved" opens the Logs folder.
echo.
echo   Phase 2, task 12: tester build (the v0.2.0 zip from 58, or D:\skiAreaDesignChallenge\releases)
echo     59 Tester kit: unzip the release and play it as a first-time player (an empty library, your settings kept),
echo        with the tester sheet open; when the game closes, the Logs, Player.log and Pictures folders open to collect.
echo.
echo   Phase 3, task 01: carry-over polish (rebuilds the game if needed; on the desktop, not over remote desktop)
echo     60 The display check (Windowed, Keep, a resolution left to go back after 15 s, Borderless, Go back, Fullscreen;
echo        about a minute, the screen changes), then the game to try by hand: the title's signs, the elevation readout
echo        off the terrain in both units (U), photo mode's Tab and Esc, the title drift and the far trees.
echo.
echo   Phase 3, task 03: the design document, commands and undo (no Unity needed)
echo     61 Run the design document tests, then print a walkthrough: a lift and a trail, split, join, undo, redo,
echo        a stale and a bad command rejected, the derived network and the document bytes.
echo.
echo   Phase 3, task 04: the save file (rebuilds the game if needed; a scratch library, the network off)
echo     62 The save tests, then the game: F1 Add sample design, Ctrl+S names a save, Exit asks to save, Continue
echo        reopens it, Load Area lists the saves; then a second run kills the game mid-save to show the last good save.
echo   Phase 3, task 06: the Toolbox and placement (rebuilds the game, about 2 minutes; close the Unity editor first)
echo     64 The game with the Toolbox's New lift and New trail: T, N on Lifts or Trails, click the mountain, the
echo        tool window's steps and review, Break ground, Ctrl+Z / Ctrl+Y, and every key in Settings ^> Controls.
echo.
echo   Phase 3, task 05: terrain edits (rebuilds the game if needed; on the desktop, not over remote desktop)
echo     63 The game with the test edit keys: point at a slope, F6 previews a 300 m (1,000 ft) bench down the fall line
echo        in yellow, F7 breaks ground (graded, trees cleared, snow fills in), F8 cancels; Ctrl+Z / Ctrl+Y undo, redo.
echo     63b The edit timing run: 50 benches at High (preview, commit, undo p95 against 100 / 250 ms), then the
echo        Development game for idle garbage; prints both reports.
echo.
echo   Phase 3, task 07: the style tile, one trail and one lift (rebuilds the game if needed; on the desktop)
echo     65 The game with the style tile: a graded, groomed trail and a Sessellift FGQ-4 on Jackson Hole, built by command;
echo        Space runs the clock (the chairs move), Shift+1 snow off, R/F tilt, C free-fly.
echo     65s The same in summer: the snow off, a mid-July sun, a mown bench and earth faces.
echo     65c The style tile's captures: the benchmark and close views, summer and winter, day and night (render lane).
echo     65b The benchmark with the style tile shown (High and Medium), compared with the baselines (measure lane).
echo.
echo   Phase 3, task 08: trails (rebuilds the game, about 2 minutes; close the Unity editor first; on the desktop)
echo     67 The game with the trail tools: T, Trails tab. New (N): paint, Shift-drag erases, [ ] brush; Enter; click
echo        the head and the tail; Natural or Graded (yellow contours, red too steep, the profile strip); Break ground.
echo        Edit (M) and Remove (X): click a trail. Shift+4 the Trails layer; Tab, Trails: the list, its filters and cards.
echo     67b The trail timing run: 1,000 ft and 1 km trails at 10 spots (preview, commit, edit, undo against 100 / 250 ms).
echo     67c The trail captures on Jackson Hole 2 km: a sidehill and a ridge, each step of the tool (render lane).
echo   Phase 3, task 09: lifts (rebuilds the game; on the desktop)
echo     68 Lifts in the game: New (type menu, terminals, towers by hand), hover glow and map lines, Edit and Remove
echo.
echo     Q  Quit
echo.
set "CHOICE="
set /p "CHOICE=Choose: "
if defined CHOICE goto chosen
rem Three empty answers in a row (or the end of redirected input) quits instead of looping forever.
set /a EMPTY+=1
if %EMPTY% geq 3 exit /b 0
goto menu
:chosen
set "EMPTY=0"
if /i "%CHOICE%"=="1" goto tests
if /i "%CHOICE%"=="2" call :site "Jackson Hole" 43.593 -110.848 2 & goto done
if /i "%CHOICE%"=="3" call :site "Jackson Hole" 43.593 -110.848 5 & goto done
if /i "%CHOICE%"=="4" call :site "Crystal Mountain" 46.93 -121.49 5 & goto done
if /i "%CHOICE%"=="5" goto custom
if /i "%CHOICE%"=="6" goto coverage
if /i "%CHOICE%"=="7" (
  if not exist "%OUT%" mkdir "%OUT%"
  start "" "%OUT%"
  goto menu
)
if /i "%CHOICE%"=="8" goto coretests
if /i "%CHOICE%"=="9" goto repochecks
if /i "%CHOICE%"=="10" goto unitytests
if /i "%CHOICE%"=="11" call :acquire "Jackson Hole" 43.593 -110.848 2 & goto done
if /i "%CHOICE%"=="12" call :acquire "Jackson Hole" 43.593 -110.848 5 & goto done
if /i "%CHOICE%"=="13" call :acquire "Crystal Mountain" 46.93 -121.49 2 & goto done
if /i "%CHOICE%"=="14" goto acquirecustom
if /i "%CHOICE%"=="16" goto library
if /i "%CHOICE%"=="17" goto play
if /i "%CHOICE%"=="18" goto buildgame
if /i "%CHOICE%"=="19" goto trees
if /i "%CHOICE%"=="20" goto lineup
if /i "%CHOICE%"=="21" goto benchmark
if /i "%CHOICE%"=="22" goto lifts
if /i "%CHOICE%"=="23" goto liftlab
if /i "%CHOICE%"=="24" call :acquire "Crystal Mountain" 46.93 -121.49 5 & goto done
if /i "%CHOICE%"=="25" goto playcrystal
if /i "%CHOICE%"=="26" goto benchcrystal
if /i "%CHOICE%"=="27" goto forestinfo
if /i "%CHOICE%"=="28" goto survey
if /i "%CHOICE%"=="29" call :acquire "Sugarloaf" 45.047 -70.316 5 & goto done
if /i "%CHOICE%"=="30" goto playsugarloaf
if /i "%CHOICE%"=="31" goto benchsugarloaf
if /i "%CHOICE%"=="33" goto standsugarloaf
if /i "%CHOICE%"=="34" goto refreshroads
if /i "%CHOICE%"=="32" (
  start "" "%~dp0docs\plans\prototypes\ui-layout.html"
  goto menu
)
if /i "%CHOICE%"=="35" goto picker
if /i "%CHOICE%"=="36" goto pickeroffline
if /i "%CHOICE%"=="37" goto rebuildpicker
if /i "%CHOICE%"=="38" goto flow
if /i "%CHOICE%"=="39" goto flowoffline
if /i "%CHOICE%"=="40" goto flowclean
if /i "%CHOICE%"=="41" goto benchfull
if /i "%CHOICE%"=="42" goto benchpreset
if /i "%CHOICE%"=="43" call :builddev & goto done
if /i "%CHOICE%"=="44" goto formats
if /i "%CHOICE%"=="45" goto formatsnewer
if /i "%CHOICE%"=="46" goto formattests
if /i "%CHOICE%"=="47" goto uicapture
if /i "%CHOICE%"=="53" goto settingscapture
if /i "%CHOICE%"=="48" goto hudparity
if /i "%CHOICE%"=="49" goto housekeeping
if /i "%CHOICE%"=="50" goto windowreset
if /i "%CHOICE%"=="51" goto forestlook
if /i "%CHOICE%"=="52" goto firstrun
if /i "%CHOICE%"=="54" goto killresume
if /i "%CHOICE%"=="55" goto photocredits
if /i "%CHOICE%"=="56" goto loadingdetails
if /i "%CHOICE%"=="57" goto titlestill
if /i "%CHOICE%"=="58" goto release
if /i "%CHOICE%"=="59" goto testerkit
if /i "%CHOICE%"=="60" goto carryover
if /i "%CHOICE%"=="61" goto designdoc
if /i "%CHOICE%"=="62" goto savefile
if /i "%CHOICE%"=="63" goto terrainedits
if /i "%CHOICE%"=="63b" goto editbench
if /i "%CHOICE%"=="64" goto toolbox
if /i "%CHOICE%"=="68" goto placelift
if /i "%CHOICE%"=="65" goto styletile
if /i "%CHOICE%"=="65s" goto styletilesummer
if /i "%CHOICE%"=="67" goto trails
if /i "%CHOICE%"=="67b" goto trailbench
if /i "%CHOICE%"=="67c" goto trailcaptures
if /i "%CHOICE%"=="65c" goto styletilecaptures
if /i "%CHOICE%"=="65b" goto styletilebench
if /i "%CHOICE%"=="15" (
  if not exist "%PACKAGES%" mkdir "%PACKAGES%"
  start "" "%PACKAGES%"
  goto menu
)
if /i "%CHOICE%"=="Q" exit /b 0
goto menu

:tests
chcp 65001 >nul
dotnet test "%SPIKE%\tests\DataSpike.Tests" <nul
goto done

:coretests
chcp 65001 >nul
dotnet test "%~dp0tools\domain-tests\Tests" <nul
goto done

:repochecks
where node >nul 2>nul
if errorlevel 1 (
  echo Node.js is not installed. Get the free LTS from https://nodejs.org
  goto done
)
chcp 65001 >nul
node "%~dp0tools\repo-checks\check.mjs" "%~dp0." <nul
goto done

:unitytests
if not exist "%UNITY%" (
  echo Unity 6000.3.25f1 was not found at "%UNITY%".
  goto done
)
tasklist /fi "imagename eq Unity.exe" | find /i "Unity.exe" >nul
if not errorlevel 1 (
  echo The Unity editor is open. Close it first, then try again.
  goto done
)
if not exist "%~dp0test-results" mkdir "%~dp0test-results"
call :unityrun EditMode
call :unityrun PlayMode
goto done

:unityrun
echo Running %1 tests...
"%UNITY%" -batchmode -projectPath "%~dp0." -runTests -testPlatform %1 -testResults "%~dp0test-results\%1.xml" -logFile "%~dp0test-results\%1.log" <nul
if errorlevel 1 (
  echo   %1: FAILED - see test-results\%1.xml
) else (
  echo   %1: passed
)
exit /b 0

:play
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Starting the game on the title screen ^(your own library^). In a mountain: WASD or arrows pan, Q/E rotate, R/F tilt, wheel or +/- zoom, Home resets the view, C free-fly (PgUp/PgDn rise and sink), map layers Shift+1 snow and Shift+3 trees, info layers Shift+6 contours, Shift+7 slope angle, Shift+8 slope exposure, Shift+9 snow depth (Shift+0 snow conditions comes later), U feet or metres, H hide the UI, P photo mode (F12 saves a picture), F1 every key plus the developer panel (wind, tree snow, cover map, light and time of day, haze, lakes, distant shadows, Corbet's), Esc menu (Quit is there).
start "" "%GAME%"
goto menu

:flow
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
call :seedscratch
echo Starting the game on the title screen with the scratch library %SCRATCH%.
echo   New Area opens the site picker. Crystal Mountain (search it; 2 km) uses 3DEP fallback terrain, which the
echo   quality card reports. Minimise (Esc or -) keeps the download going as a pill.
echo   Load Area: Enter opens. Manage Areas: Delete removes, Resume continues a paused download. Esc backs out one step.
start "" "%GAME%" -data "%SCRATCH%"
goto menu

:flowoffline
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
call :seedscratch
echo Starting the game with the network off ^(-offline^): Load Area, then open any area. New Area says why it can't go on.
start "" "%GAME%" -data "%SCRATCH%" -offline
goto menu

:firstrun
rem A build without the bundled demo (older, or built before 12 was downloaded) is rebuilt first.
if not exist "%~dp0Builds\Windows\SkiAreaDesignChallenge_Data\StreamingAssets\Demo" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0Builds\Windows\SkiAreaDesignChallenge_Data\StreamingAssets\Demo" (echo   This build has no demo: download 12 first, then run 52 again. & goto done)
if exist "%FIRSTRUN%" rmdir /s /q "%FIRSTRUN%"
echo Starting the game as a fresh install: an empty library %FIRSTRUN%, with the network off.
echo   The title opens over Jackson Hole, built into the game, at 3:30 pm; the camera drifts through four views.
echo   "Open the demo" is focused: Enter opens it. Load Area lists it as Built in; Manage Areas has no Rename or Delete for it.
echo   Quit the game to see how long the title took to come in.
start "" /wait "%GAME%" -data "%FIRSTRUN%" -offline
findstr /c:"[AppFlow] The title's mountain is in" "%PLAYERLOG%"
goto done

:loadingdetails
rem Task P2-03.1: the fresh install of 52 with the Details list shown (and remembered, so it stays on until hidden).
if not exist "%~dp0Builds\Windows\SkiAreaDesignChallenge_Data\StreamingAssets\Demo" call :buildplayer
if not exist "%GAME%" goto done
if exist "%FIRSTRUN%" rmdir /s /q "%FIRSTRUN%"
echo Starting the game as a fresh install, offline, with loading details on.
echo   The loading box shows the game's logo, "Opening Jackson Hole", the bar, each stage as it finishes, and Alpine Labs.
echo   On the title, the name plate carries the game's logo and name with the studio's name below.
start "" "%GAME%" -data "%FIRSTRUN%" -offline -loadingdetails on
goto menu

:titlestill
rem Task P2-03.1: the loading screen's background is a picture of the title view; any change to the title's look
rem (lighting, snow, forest, the camera's first view) makes it stale. This writes a new one into the game's resources.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if exist "%FIRSTRUN%" rmdir /s /q "%FIRSTRUN%"
%RENDER% "%GAME%" -data "%FIRSTRUN%" -offline -quality high -titlestill "%~dp0Assets\MountainPlanner\Art\UI\Flow\Resources\MountainPlannerFlow\TitleStill.jpg"
echo   Wrote Assets\MountainPlanner\Art\UI\Flow\Resources\MountainPlannerFlow\TitleStill.jpg: rebuild (11) to see it, and commit it.
goto done

:killresume
rem Task P2-06: a scratch library of its own, emptied first; the second game stays open to watch the pill (the render
rem slot ends with the script, once that game has started).
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
%RENDER% powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\demo\kill-resume.ps1" -Game "%GAME%" -Data "%LOCALAPPDATA%\SkiAreaDesignChallenge-killresume" -Seed "%~dp0TestData\jackson-hole-2km" -LogFolder "%~dp0test-results\p2-06"
goto done

:flowclean
if exist "%SCRATCH%" rmdir /s /q "%SCRATCH%"
echo Emptied %SCRATCH%.
goto done

:formats
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
call :seedformats
if exist "%FORMATS%\library.json" del /q "%FORMATS%\library.json"
echo Starting the game with the scratch library %FORMATS%.
echo   Continue reads a Phase 1 recent.json: Jackson Hole, last opened Oct 3. Open it: the Phase 1 package opens as before.
echo   Load Area: Crystal Mountain is greyed, "Made by a newer version of Mountain Planner", and won't open.
echo   Manage Areas: Delete removes it. Run 44 again to bring it back.
start "" "%GAME%" -data "%FORMATS%"
goto menu

:formatsnewer
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
call :seedformats
copy /y "%~dp0TestData\formats\demo\library.json" "%FORMATS%\library.json" >nul
echo Starting the game with the same scratch library, marked as laid out by a newer game ^(library.json version 99^).
echo   The title briefly shows "This library was saved by a newer version...". Load Area lists nothing and says the same. 44 puts it back.
start "" "%GAME%" -data "%FORMATS%"
goto menu

:housekeeping
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
rem The 2 km test terrain, plus a 120 MB cache "left by an older version" (cache-v1) for Free space to find.
if not exist "%LIBDEMO%\Resorts\jackson-hole-2km-test\roads.json" robocopy "%~dp0TestData\jackson-hole-2km" "%LIBDEMO%\Resorts\jackson-hole-2km-test" /e /njh /njs /nfl /ndl >nul
if not exist "%LIBDEMO%\Resorts\jackson-hole-2km-test\cache-v1" mkdir "%LIBDEMO%\Resorts\jackson-hole-2km-test\cache-v1"
if not exist "%LIBDEMO%\Resorts\jackson-hole-2km-test\cache-v1\t0_0.h16" fsutil file createnew "%LIBDEMO%\Resorts\jackson-hole-2km-test\cache-v1\t0_0.h16" 120000000 >nul
echo Starting the game with the scratch library %LIBDEMO%.
echo   Manage Areas: each row's disk use (hover it for the parts), the total at the top, and "Free 120 MB".
echo   F2 or Rename: type a new name, Enter. Quit and start 49 again: the new name, and the sort you chose, are kept.
echo   Free 120 MB: asks first, then removes the older cache; the area still opens at once.
echo   Delete: asks, naming the space it frees. Run 49 again to bring the area back.
start "" "%GAME%" -data "%LIBDEMO%"
goto menu

:designdoc
chcp 65001 >nul
dotnet test "%~dp0tools\domain-tests\Tests" --filter "FullyQualifiedName~MountainPlanner.Tests.Design&FullyQualifiedName!~DesignDemoTests" <nul
if errorlevel 1 goto done
dotnet test "%~dp0tools\domain-tests\Tests" --no-build --filter "FullyQualifiedName~DesignDemoTests" --logger "console;verbosity=normal" <nul | findstr /v /c:"Test run for" /c:"A total" /c:"NUnit" /c:"Running selected"
goto done

:savefile
rem Task P3-04: the engine-free save tests, then the game on its own scratch library (saves in SAVES\Saves).
chcp 65001 >nul
dotnet test "%~dp0tools\domain-tests\Tests" --filter "FullyQualifiedName~SaveFileTests|FullyQualifiedName~OpenDesignTests" <nul
chcp %OLDCP% >nul
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "SAVES=%LOCALAPPDATA%\SkiAreaDesignChallenge-saves"
if exist "%SAVES%" rmdir /s /q "%SAVES%"
echo.
echo First run: open the demo (Enter). F1 shows the developer panel: Add sample design adds a lift and a planned trail;
echo the bar's save symbol turns white. Ctrl+S asks for a name; type one and press Enter ("Saved ..." at the top).
echo Add another sample, then the menu's Exit to title: "Save before leaving?" - Save and leave. Continue now names the
echo save; Enter reopens it with both lifts. Back on the title, Load Area: the area says "1 save"; the right arrow lists it
echo with New design; F2 renames it, Delete asks before removing it. Settings ^> Gameplay has Autosave (every 10 min).
echo Quit when done.
start "" /wait "%GAME%" -data "%SAVES%" -offline
echo.
echo Second run, the crash: Continue, F1 Add sample design, then Ctrl+S. The game is killed part way into the save.
start "" /wait "%GAME%" -data "%SAVES%" -offline -crashsave
echo Third run: Continue opens the last good save (without the lift added before the crash), and Load Area still lists it.
start "" /wait "%GAME%" -data "%SAVES%" -offline
dir /b /s "%SAVES%\Saves"
goto done

:formattests
chcp 65001 >nul
dotnet test "%~dp0tools\domain-tests\Tests" --filter "FullyQualifiedName~FormatFreezeTests|FullyQualifiedName~VersionedJsonTests" <nul
goto done

:seedformats
rem The Phase 1 test terrain (opens as before), the Phase 1 recently opened list, and an area from a future format.
if not exist "%FORMATS%\Resorts\jackson-hole-2km-test\roads.json" robocopy "%~dp0TestData\jackson-hole-2km" "%FORMATS%\Resorts\jackson-hole-2km-test" /e /njh /njs /nfl /ndl >nul
if not exist "%FORMATS%\Resorts\newer-area" mkdir "%FORMATS%\Resorts\newer-area"
copy /y "%~dp0TestData\formats\demo\newer-area\manifest.json" "%FORMATS%\Resorts\newer-area\manifest.json" >nul
copy /y "%~dp0TestData\formats\v1-library\recent.json" "%FORMATS%\recent.json" >nul
exit /b 0

:seedscratch
rem The title needs a mountain behind it: seed the scratch library with the committed 2 km test terrain once.
if exist "%SCRATCH%\Resorts\jackson-hole-2km-test\roads.json" exit /b 0
robocopy "%~dp0TestData\jackson-hole-2km" "%SCRATCH%\Resorts\jackson-hole-2km-test" /e /njh /njs /nfl /ndl >nul
exit /b 0

:playcrystal
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Starting the game on Crystal Mountain (download it first with 24). The keys are the same as in 17.
start "" "%GAME%" -site "Crystal Mountain"
goto menu

:benchcrystal
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\benchmark" mkdir "%~dp0test-results\benchmark"
echo Running the forest benchmark on Crystal Mountain (the game flies 5 views, then closes by itself)...
%MEASURE% "%GAME%" %SCREEN% -site "Crystal Mountain" -benchmark "%~dp0test-results\benchmark\crystal.json" -logFile "%~dp0test-results\benchmark\crystal.log" <nul
findstr /l /c:"[Benchmark]" "%~dp0test-results\benchmark\crystal.log"
start "" "%~dp0test-results\benchmark"
goto done

:standsugarloaf
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Starting the game in a dense spruce-fir stand on Sugarloaf (download it first with 29): mixed heights, small trees under big ones, clumps and gaps. The keys are the same as in 17.
start "" "%GAME%" -site "Sugarloaf" -view 1500,-1550,250,200,25
goto menu

:playsugarloaf
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Starting the game on Sugarloaf (download it first with 29). The keys are the same as in 17.
start "" "%GAME%" -site "Sugarloaf"
goto menu

:refreshroads
echo Adding roads (paved and unpaved, from OpenStreetMap) to every downloaded mountain that has none yet, then preparing each again...
dotnet run --project "%~dp0toolscquire" -- refresh-osm <nul
goto done

:benchsugarloaf
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\benchmark" mkdir "%~dp0test-results\benchmark"
echo Running the forest benchmark on Sugarloaf (the game flies its views, then closes by itself)...
%MEASURE% "%GAME%" %SCREEN% -site "Sugarloaf" -benchmark "%~dp0test-results\benchmark\sugarloaf.json" -logFile "%~dp0test-results\benchmark\sugarloaf.log" <nul
findstr /l /c:"[Benchmark]" "%~dp0test-results\benchmark\sugarloaf.log"
start "" "%~dp0test-results\benchmark"
goto done

:forestinfo
chcp 65001 >nul
dotnet run --project "%~dp0tools\acquire" -- forest-info <nul
goto done

:survey
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OUT%\ski_areas.geojson" (
  echo Downloading the ski-area list from OpenSkiMap: about 5 MB, data from OpenStreetMap contributors ^(ODbL^)...
  curl -s -o "%OUT%\ski_areas.geojson.gz" https://tiles.openskimap.org/geojson/ski_areas.geojson.gz
  powershell -NoProfile -Command "$i=[IO.File]::OpenRead('%OUT%\ski_areas.geojson.gz'); $o=[IO.File]::Create('%OUT%\ski_areas.geojson'); $g=New-Object IO.Compression.GZipStream($i,[IO.Compression.CompressionMode]::Decompress); $g.CopyTo($o); $g.Close(); $o.Close(); $i.Close()"
)
chcp 65001 >nul
echo Surveying tree species at every operating US ski area. Ctrl+C stops it; choose 28 again to resume.
dotnet run --project "%~dp0tools\acquire" -- species-survey --areas "%OUT%\ski_areas.geojson" --out "%OUT%\species-survey.jsonl"
dotnet run --project "%~dp0tools\acquire" -- species-report --survey "%OUT%\species-survey.jsonl" --out "%OUT%\species-priority.md" <nul
goto done

:trees
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
if not exist "%BLENDER%" (echo Blender 5.2 was not found at "%BLENDER%". & goto done)
echo Building the tree library in Blender (about 10 seconds)...
"%BLENDER%" -b --factory-startup --python "%~dp0tools\assets\trees\build_trees.py" -- --out "%~dp0tools\assets\trees\out" <nul
if errorlevel 1 (echo   The tree build failed. & goto done)
echo Importing the trees into Unity (about 2 minutes)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.TreeImport.Import -quit -logFile "%~dp0test-results\trees.log" <nul
if errorlevel 1 (echo   The import failed - see test-results\trees.log) else (echo   Trees imported. Choose 18 to rebuild the game.)
goto done

rem Task P2-09: the far impostors from overhead next to the meshes they replace, then the game over the forest. No -screen-*
rem flags: the game keeps your window mode.
:forestlook
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\forest-look" mkdir "%~dp0test-results\forest-look"
echo Capturing the overhead lineup (the game window opens and closes by itself, about a minute)...
%RENDER% "%GAME%" -quality high -lineup "%~dp0test-results\forest-look\l" -lineupset top -logFile "%~dp0test-results\forest-look\lineup.log" <nul
start "" "%~dp0test-results\forest-look"
echo Starting the game above the Jackson Hole forest. Shift+1: snow off and on; wheel: zoom; drag: orbit.
start "" "%GAME%" -quality high -view 350,-150,600,200,80
goto menu

:lineup
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\lineup" mkdir "%~dp0test-results\lineup"
echo Capturing the tree lineup (the game window opens and closes by itself)...
%RENDER% "%GAME%" %SCREEN% -lineup "%~dp0test-results\lineup\lineup" -logFile "%~dp0test-results\lineup\lineup.log" <nul
start "" "%~dp0test-results\lineup"
goto done

:benchmark
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
echo Running the benchmark at High (the game flies the path once to warm up, twice to measure, then closes by itself)...
%MEASURE% "%GAME%" %SCREEN% -quality high -benchmark "%BENCH%\bench.json" -logFile "%BENCH%\bench.log" <nul
findstr /l /c:"[Benchmark]" "%BENCH%\bench.log"
start "" "%BENCH%"
goto done

rem Task 15: the frame times of record come from the release game; the Development game adds exact garbage per
rem frame and graphics memory (counters a release game doesn't record). Each run is compared with the stored baseline.
:benchfull
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%GAMEDEV%" call :builddev
if not exist "%GAMEDEV%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
for %%Q in (high medium) do (
  echo Benchmark at %%Q, the release game ^(about 5 minutes^)...
  %MEASURE% "%GAME%" %SCREEN% -quality %%Q -benchmark "%BENCH%\%%Q.json" -logFile "%BENCH%\%%Q.log" <nul
  echo Benchmark at %%Q, the Development game: garbage and memory ^(about 3 minutes^)...
  %MEASURE% "%GAMEDEV%" %SCREEN% -quality %%Q -laps 1 -benchmark "%BENCH%\%%Q-dev.json" -logFile "%BENCH%\%%Q-dev.log" <nul
)
echo Benchmark at high with the HUD on, the Development game ^(about 3 minutes^)...
%MEASURE% "%GAMEDEV%" %SCREEN% -quality high -laps 1 -withhud -benchmark "%BENCH%\high-dev-hud.json" -logFile "%BENCH%\high-dev-hud.log" <nul
echo.
for %%R in (high high-dev high-dev-hud medium medium-dev) do findstr /l /c:" at 1920x1080" "%BENCH%\%%R.log"
echo.
node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-high.json" "%BENCH%\high.json"
node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-medium.json" "%BENCH%\medium.json"
for %%R in (high-dev medium-dev high-dev-hud) do node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-%%R.json" "%BENCH%\%%R.json"
start "" "%BENCH%"
goto done

:benchpreset
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
set "QUALITY=high"
set /p "QUALITY=Quality preset (low, medium, high or ultra; Enter for high): "
echo Benchmark at %QUALITY% (about 4 minutes)...
%MEASURE% "%GAME%" %SCREEN% -quality %QUALITY% -benchmark "%BENCH%\%QUALITY%.json" -logFile "%BENCH%\%QUALITY%.log" <nul
findstr /l /c:"[Benchmark]" "%BENCH%\%QUALITY%.log"
start "" "%BENCH%"
goto done

:uicapture
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\ui-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
echo Capturing every screen (about 15 minutes; the window shows the title while it works)...
%RENDER% "%GAME%" -uicapture "%UICAP%" -logFile "%~dp0test-results\ui-captures.log" <nul
type "%UICAP%\report.txt"
start "" "%UICAP%"
goto done

:settingscapture
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\settings-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
echo Capturing every Settings page (the window shows the title while it works)...
%RENDER% "%GAME%" -uicapture "%UICAP%" -uionly s8-settings,s8-settings-units,s8-settings-graphics,s8-settings-display,s8-settings-controls,s8-settings-data,s8-settings-game -uisizes 1920x1080,2560x1080,3440x1440 -logFile "%~dp0test-results\settings-captures.log" <nul
type "%UICAP%\report.txt"
start "" "%UICAP%"
goto done

:photocredits
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\photo-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
echo Capturing photo mode and Credits at 1920x1080 and 2560x1080, dark and light, 85%% (the window shows the title while it works)...
%RENDER% "%GAME%" -uicapture "%UICAP%" -uionly s9-credits,s9-credits-licences,s10-photo,s10-photo-natural,s10-photo-mini,s10-photo-mini-golden,s10-photo-postcard-2x -uisizes 1920x1080,2560x1080 -uibase 85 -logFile "%~dp0test-results\photo-captures.log" <nul
type "%UICAP%\report.txt"
echo Saving five 2x photos of Jackson Hole into test-results\photo-hitch to time the hitch...
if exist "%~dp0test-results\photo-hitch" rmdir /s /q "%~dp0test-results\photo-hitch"
%MEASURE% "%GAME%" -site "Jackson Hole" -photo off -photoscale 2 -photoshot 5 -photofolder "%~dp0test-results\photo-hitch" -logFile "%~dp0test-results\photo-hitch.log" <nul
findstr /c:"Photo run" /c:"Photo hitch" /c:"Photo saved" "%~dp0test-results\photo-hitch.log"
start "" "%UICAP%"
echo Starting the game: open an area, press P. Drag Time, step Date, try Focus and Size, Space saves; Esc puts it all back.
echo   Menu ^> Credits lists every area's sources; Licence texts shows the OFL.
start "" "%GAME%"
goto done

:release
rem Task P2-10: the one-command release (tools\release\release.ps1, also release.bat); a preview when HEAD isn't tagged vX.Y.Z.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\release.ps1" -AllowDirty <nul
if errorlevel 1 goto done
set "RELZIP="
for %%Z in ("%~dp0Builds\Release\SkiAreaDesignChallenge-*-win64.zip") do set "RELZIP=%%~fZ"
if not defined RELZIP goto done
set "RELTRY=%~dp0test-results\release\unzipped"
if exist "%RELTRY%" rmdir /s /q "%RELTRY%"
mkdir "%RELTRY%"
"%SystemRoot%\System32\tar.exe" -x -f "%RELZIP%" -C "%RELTRY%"
for /d %%D in ("%RELTRY%\SkiAreaDesignChallenge-*") do set "RELGAME=%%~fD\SkiAreaDesignChallenge.exe"
echo Starting the unzipped game with a forced error (-crashtest), as a tester would see it:
echo   the title shows the version bottom left; once it's in, "Something went wrong. A log was saved" appears - Open folder
echo   shows %LOCALAPPDATA%\SkiAreaDesignChallenge\Logs. Settings - Data - Logs has Open and Copy report; Credits shows the version.
%RENDER% "%RELGAME%" -crashtest <nul
start "" "%~dp0Builds\Release"
goto done

:testerkit
rem Task P2-12: the tester kit. Unzips the newest release zip into test-results\tester and starts it on an empty data
rem folder (-data), so the library, recent list and download cache start empty like a fresh install. Settings and the
rem error logs stay in their usual places. The tester sheet is docs\plans\p2-12-tester-sheet.md.
set "TKZIP="
for %%Z in ("%~dp0..\releases\SkiAreaDesignChallenge-*-win64.zip") do set "TKZIP=%%~fZ"
for %%Z in ("%~dp0Builds\Release\SkiAreaDesignChallenge-*-win64.zip") do set "TKZIP=%%~fZ"
if not defined TKZIP (
  echo No release zip found in Builds\Release or ..\releases. Build one with 58 first.
  goto done
)
set "TK=%~dp0test-results\tester"
if exist "%TK%" rmdir /s /q "%TK%"
mkdir "%TK%\game" "%TK%\data"
echo Unzipping %TKZIP%...
"%SystemRoot%\System32\tar.exe" -x -f "%TKZIP%" -C "%TK%\game"
set "TKGAME="
for /d %%D in ("%TK%\game\SkiAreaDesignChallenge-*") do set "TKGAME=%%~fD\SkiAreaDesignChallenge.exe"
if not defined TKGAME goto done
start "" "%~dp0docs\plans\p2-12-tester-sheet.md"
echo Playing as a first-time player. Follow the tester sheet; close the game when you are done.
"%TKGAME%" -data "%TK%\data" <nul
if not exist "%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs" mkdir "%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs"
start "" "%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs"
start "" "%USERPROFILE%\AppData\LocalLow\Alpine Labs\Ski Area Design Challenge"
if exist "%USERPROFILE%\Pictures\Ski Area Design Challenge" start "" "%USERPROFILE%\Pictures\Ski Area Design Challenge"
goto done

:carryover
rem Task P3-01: -displaycheck works Settings > Display's own buttons in the real game and writes report.txt (PASS or
rem FAIL per check), then puts the window back as it started. Then the game, for the hands-on checks.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "DCHECK=%~dp0test-results\display-check"
if exist "%DCHECK%" rmdir /s /q "%DCHECK%"
echo The display check: the window switches modes and sizes by itself for about a minute. Leave it alone...
%RENDER% "%GAME%" -displaycheck "%DCHECK%" -logFile "%DCHECK%.log" <nul
type "%DCHECK%\report.txt"
echo.
echo Now the game. Try: hover each of the title's signs (Tab or the arrows show the focus ring instead of a slide);
echo watch the title's drift (no bob) and the far trees (no blinking); in a mountain, point at the sky: "- - - ft",
echo then U for "- - - m"; P for photo mode, Tab, then the arrows move in the bar while the camera stays; Esc gives
echo the keys back, and Esc again leaves photo mode.
"%GAME%" <nul
goto done

:placelift
rem Task P3-09: placing, editing and removing lifts. Always rebuilt, so the lift tools on this branch are the ones you try.
call :buildplayer
if not exist "%GAME%" goto done
echo.
echo Open an area, then T for the Toolbox, the Lifts tab, N for New lift:
echo   Pick the type in the menu (Type, Chair or Cabin, Model), Towers Automatic or By hand, Plumb or Leaned.
echo   Click the name in the window's title (or F2) to name the lift.
echo   1 The bottom terminal follows the pointer: Shift+wheel or , . turn it, Page Up/Down its level; click to place.
echo     It stands where you put it; its pad is levelled with cut and fill (no automatic raise any more).
echo   2 The top terminal follows the pointer up the hill, with its level; click to fix the line.
echo   3 The line is drawn in 3D as it grows. Automatic: the towers appear; Edit towers opens them by hand.
echo     By hand: the tower in hand is its own see-through model on the line (the game picks its head), green
echo     where it fits; , and . slide it, Page Up/Down its height; click or Enter places it. Click a placed tower to
echo     change it (Page Up/Down, , ., drag, Delete); click the line or press Insert to add one between others;
echo     Fill the rest finishes the line, Revert to auto places them all again. Enter (or Review) for the review.
echo   4 Review: if it says "raise it N ft", click that terminal and press Page Up N times. Every tower stays
echo     editable. Break ground (Ctrl+Enter): towers, pads, trench and clearing; the chairs run. Ctrl+Z takes it back.
echo   Try a Monta FG4 too: no extra hold-down tower after its drive, and its sheaves sit on the rope.
echo   Hover a built lift: it glows; click it for its card (rename by clicking its name, Edit, Remove).
echo   Zoom far out: lifts show as red trail-map lines. Shift+2 hides the lifts.
echo   M for Edit lift: click a lift; its draft shows in 3D in its place. Click a tower or terminal (or [ and ]);
echo     the same tower keys, Insert adds one, drag a terminal, change the type or the name, Revert to auto;
echo     Ctrl+Z undoes inside the tool; Apply (Ctrl+Enter) or Cancel.
echo   X for Remove lift: click a lift, click it again (or Enter) to remove it with its pads and clearing.
echo   Tab for Analysis, Lifts tab: the list; hover a row to light its lift, click it for its card.
start "" "%GAME%"
goto menu

:styletile
rem Task P3-07: the style tile by command (-styletile): the trail graded and cleared, the lift with its towers, rope and chairs.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Once the mountain is in, the trail and the lift are built (a few seconds). Space runs the clock and the chairs.
"%GAME%" -notitle -styletile <nul
goto done

:styletilesummer
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
"%GAME%" -notitle -styletile -season summer <nul
goto done

:styletilecaptures
rem Task P3-07: every view in four conditions, one player per picture (render lane), into test-results\style-tile.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\style-tile\captures.ps1" -Game "%GAME%" -Out "%~dp0test-results\style-tile" -Owner owner
start "" "%~dp0test-results\style-tile"
goto done

:styletilebench
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
for %%Q in (high medium) do (
  echo Benchmark at %%Q with the style tile ^(about 5 minutes^)...
  %MEASURE% "%GAME%" %SCREEN% -quality %%Q -styletile -benchmark "%BENCH%\styletile-%%Q.json" -logFile "%BENCH%\styletile-%%Q.log" <nul
  node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-%%Q.json" "%BENCH%\styletile-%%Q.json"
)
start "" "%BENCH%"
goto done

:terrainedits
rem Task P3-05: the test edit by keys (-testedit). The HUD elevation reads the edited heights; Shift+6 shows contours.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
echo Once the mountain is in, point at a slope and press F6: the yellow lines are the bench's new contours.
echo F7 builds it (the ground levels, its trees go and the snow fills in); Ctrl+Z puts everything back; Ctrl+Y again.
"%GAME%" -testedit <nul
goto done

:editbench
rem Task P3-05 (CF-09): -editbench grades the 300 m test bench at 10 spots, 5 rounds, timing preview, commit and undo,
rem and checks undo leaves byte-identical heights; the Development game adds the idle-garbage check.
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "EDITS=%~dp0test-results\edit-bench"
if not exist "%EDITS%" mkdir "%EDITS%"
%MEASURE% "%GAME%" %SCREEN% -quality high -editbench "%EDITS%\high.json" -logFile "%EDITS%\high.log" <nul
if exist "%GAMEDEV%" %MEASURE% "%GAMEDEV%" %SCREEN% -quality high -editbench "%EDITS%\high-dev.json" -logFile "%EDITS%\high-dev.log" <nul
findstr /c:"[EditBenchmark]" "%EDITS%\high.log"
if exist "%EDITS%\high-dev.log" findstr /c:"[EditBenchmark]" "%EDITS%\high-dev.log"
goto done

:trails
rem Task P3-08: the trail tools in the real game. Always rebuilt, so the tools on this branch are the ones you try.
call :buildplayer
if not exist "%GAME%" goto done
"%GAME%" <nul
goto done

:trailbench
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\p3-08" mkdir "%~dp0test-results\p3-08"
%MEASURE% "%GAME%" -notitle -quality high -trailbench "%~dp0test-results\p3-08\trailbench.json" -logFile "%~dp0test-results\p3-08\trailbench.log" <nul
findstr /c:"[TrailBench]" "%~dp0test-results\p3-08\trailbench.log"
goto done

:trailcaptures
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "JH2=%LOCALAPPDATA%\SkiAreaDesignChallenge-p308\jh2km"
if not exist "%JH2%\manifest.json" robocopy "%~dp0TestData\jackson-hole-2km" "%JH2%" /e /njh /njs /nfl /ndl >nul
%RENDER% "%GAME%" -package "%JH2%" -notitle -view 230,-545,420,250,42 -trailshots "%~dp0test-results\p3-08\sidehill" -trailspot sidehill <nul
%RENDER% "%GAME%" -package "%JH2%" -notitle -view 280,720,330,200,45 -trailshots "%~dp0test-results\p3-08\ridge" -trailspot ridge <nul
start "" "%~dp0test-results\p3-08"
goto done

:toolbox
rem Task P3-06: the Toolbox's tools in the real game. Always rebuilt, so the tools on this branch are the ones you try.
call :buildplayer
if not exist "%GAME%" goto done
echo.
echo The Toolbox (T opens it; while it's open the letters are the tools', so the camera keeps the arrows, + and -,
echo Home and the mouse):
echo   New lift: Lifts tab, N. The bottom terminal follows the pointer, green where it can sit. Click it, then click
echo     higher up the mountain (300 ft away at least). Towers: Automatic or By hand (By hand: Page Up / Page Down
echo     change the tower in hand; the camera's zoom waits). Enter for the review, then Break ground (Ctrl+Enter) or
echo     Save plan (orange on the map).
echo   New trail: Trails tab, N. Drag to paint, Shift-drag erases, [ and ] for the brush, Ctrl+Z undoes a stroke; a
echo     separate patch blocks Next. Enter, then click the head and the tail (near a lift's end they snap), Enter, Break ground.
echo   Esc backs out one step; at the first step it asks before throwing work away. Another tool, T or Analysis cancels
echo   the tool with a note. Tab gives the tool window the keyboard (arrows, Enter); Esc gives it back.
echo   With no tool armed, Ctrl+Z / Ctrl+Y undo and redo. Settings ^> Controls has every key under Toolbox and Building.
echo   Try it at 50%% and 150%% interface scale too (Settings ^> Interface).
"%GAME%" <nul
goto done

:hudparity
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\hud-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
set "HUDSTATES=s6-hud,s6-float,s6-menu,s6-layers,s6-slope,s6-exposure,s6-depth,s6-contours,s6-slope-contours,s6-tray-lifts,s6-tray-trails,s6-tray-snow,s6-tray-infra,s6-tool-lift,s6-tool-trail,s6-tool-lift-review,s6-tool-trail-review,s6-analysis,s6-analysis-lifts,s6-analysis-weather,s6-analysis-finances,s6-rstats"
echo Capturing the HUD's states at 1920x1080, 2560x1440 and 3440x1440 (the window shows the title while it works)...
%RENDER% "%GAME%" -uicapture "%UICAP%" -uionly %HUDSTATES% -uisizes 1920x1080,2560x1440,3440x1440 -hudopacity 100 -logFile "%~dp0test-results\hud-captures.log" <nul
type "%UICAP%\report.txt"
echo Measuring the game against the mockup in headless Edge...
chcp 65001 >nul
node "%~dp0tools\ui-parity\parity.mjs" --unity "%UICAP%" --out "%~dp0test-results\ui-parity"
chcp %OLDCP% >nul
start "" "%~dp0test-results\ui-parity\sheets"
goto done

:windowreset
rem Older benchmark entries passed -screen-*, which Unity saved as the player's window mode. Deleting the saved
rem Screenmanager values (this product's key only) brings back the build's default: borderless full screen.
rem Both keys: the old company's (builds before task P2-10) and Alpine Labs'.
rem The game and both labs share this key, and each saves its window when it quits.
for %%E in (SkiAreaDesignChallenge.exe PickerLab.exe LiftLab.exe) do (
  tasklist /fi "imagename eq %%E" | find /i "%%E" >nul && (echo   Close %%E first: it saves its window mode when it quits. & goto done)
)
powershell -NoProfile -Command "$c=0; foreach ($co in 'Ski Area Design Challenge','Alpine Labs') { $k='Registry::HKCU\Software\'+$co+'\Ski Area Design Challenge'; if (Test-Path $k) { $n=(Get-Item $k).Property | Where-Object { $_ -like 'Screenmanager *' }; $n | ForEach-Object { Remove-ItemProperty -Path $k -Name $_ }; $c+=@($n).Count } }; Write-Host ('  Removed ' + $c + ' saved window values; the game opens borderless full screen next time.')"
goto done

:lifts
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
if not exist "%BLENDER%" (echo Blender 5.2 was not found at "%BLENDER%". & goto done)
call :projectfree || goto done
if not exist "%~dp0test-results" mkdir "%~dp0test-results"
echo Building the lift models in Blender (about 15 seconds)...
"%BLENDER%" -b --factory-startup --python-exit-code 1 --python "%~dp0tools\assets\lifts\build_lifts.py" -- --out "%~dp0tools\assets\lifts\out" <nul
if errorlevel 1 (echo   The lift build failed. & goto done)
echo Importing the lifts into Unity (about 30 seconds)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.LiftImport.Import -quit -logFile "%~dp0test-results\lifts.log" <nul
if errorlevel 1 (echo   The import failed - see test-results\lifts.log & goto done)
call :buildliftlab
goto done

:liftlab
if not exist "%LIFTLAB%" call :projectfree && call :buildliftlab
if not exist "%LIFTLAB%" goto done
echo Starting the Lift Lab. 1-8 drive/return/chair/line-up/stress/towers/snow guns/gun field, Tab next, L LOD, N snow, C colour, T turntable, B benchmark, H help, Esc quit.
start "" "%LIFTLAB%"
goto menu

:picker
if not exist "%PICKER%" call :projectfree && call :buildpicker
if not exist "%PICKER%" goto done
if exist "%PICKED%" del "%PICKED%"
echo Opening the site picker: search (Enter), click the map to place the square, set the size and name, then Download.
start "" /wait "%PICKER%"
if not exist "%PICKED%" (echo   No site chosen. & goto done)
set /p ARGS=<"%PICKED%"
echo.
echo Downloading the chosen site into your library: %ARGS%
echo.
chcp 65001 >nul
rem No "<nul" here, so Ctrl+C reaches the tool and it stops cleanly (then resumes next time).
dotnet run --project "%~dp0tools\acquire" -- %ARGS%
goto done

:pickeroffline
if not exist "%PICKER%" call :projectfree && call :buildpicker
if not exist "%PICKER%" goto done
echo Opening the site picker with the network switched off; close it with Esc.
start "" /wait "%PICKER%" -offline
goto menu

:rebuildpicker
call :projectfree && call :buildpicker
goto done

:buildpicker
if not exist "%~dp0test-results" mkdir "%~dp0test-results"
echo Building the Picker Lab (about a minute)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.PickerLabSetup.BuildPlayer -quit -logFile "%~dp0test-results\picker-build.log" <nul
if errorlevel 1 (echo   The build failed - see test-results\picker-build.log) else (echo   Built %PICKER%)
exit /b 0

:buildliftlab
if not exist "%~dp0test-results" mkdir "%~dp0test-results"
echo Building the Lift Lab (about a minute)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.LiftLabSetup.BuildPlayer -quit -logFile "%~dp0test-results\liftlab-build.log" <nul
if errorlevel 1 (echo   The build failed - see test-results\liftlab-build.log) else (echo   Built %LIFTLAB%)
exit /b 0

:projectfree
rem Only an editor with this project open gets in the way (it holds Temp\UnityLockfile open);
rem editors on other projects are fine.
if not exist "%UNITY%" (echo Unity 6000.3.25f1 was not found at "%UNITY%". & exit /b 1)
if exist "%~dp0Temp\UnityLockfile" (
  2>nul (>>"%~dp0Temp\UnityLockfile" (call )) || (echo The Unity editor has this project open. Close it first, then try again. & exit /b 1)
)
exit /b 0

:buildgame
call :buildplayer
goto done

:builddev
if not exist "%UNITY%" (
  echo Unity 6000.3.25f1 was not found at "%UNITY%".
  exit /b 1
)
tasklist /fi "imagename eq Unity.exe" | find /i "Unity.exe" >nul
if not errorlevel 1 (
  echo The Unity editor is open. Close it first, then try again.
  exit /b 1
)
if not exist "%~dp0test-results" mkdir "%~dp0test-results"
echo Building the Development game (about 2 minutes)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.ViewerSetup.BuildWindowsDev -logFile "%~dp0test-results\build-dev.log" <nul
if errorlevel 1 (echo   The build failed - see test-results\build-dev.log) else (echo   Built %GAMEDEV%)
exit /b 0

:buildplayer
if not exist "%UNITY%" (
  echo Unity 6000.3.25f1 was not found at "%UNITY%".
  exit /b 1
)
tasklist /fi "imagename eq Unity.exe" | find /i "Unity.exe" >nul
if not errorlevel 1 (
  echo The Unity editor is open. Close it first, then try again.
  exit /b 1
)
echo Building the game player (about 2 minutes; it also bundles the Jackson Hole demo from 12)...
"%UNITY%" -batchmode -projectPath "%~dp0." -executeMethod MountainPlanner.Editor.ViewerSetup.BuildWindows -logFile "%~dp0test-results\build.log" <nul
if errorlevel 1 (echo   The build failed - see test-results\build.log) else (echo   Built %GAME%)
exit /b 0

:library
chcp 65001 >nul
dotnet run --project "%~dp0tools\acquire" -- library <nul
goto done

:acquirecustom
set "NAME=My site"
set /p "NAME=Site name: "
set /p "LAT=Latitude (for example 43.593): "
set /p "LON=Longitude (for example -110.848, west is negative): "
set "KM=2"
set /p "KM=Size in km, 2 to 5 in 0.1 steps [2]: "
call :acquire "%NAME%" %LAT% %LON% %KM%
goto done

:acquire
set "FILE=%~1"
set "FILE=%FILE: =-%"
echo.
echo Downloading %~1 (%~4 km) into your library (%PACKAGES%)
echo.
chcp 65001 >nul
rem No "<nul" here, so Ctrl+C reaches the tool and it stops cleanly (then resumes next time).
dotnet run --project "%~dp0tools\acquire" -- --name "%~1" --lat %2 --lon %3 --km %4
exit /b 0

:custom
set "NAME=My site"
set /p "NAME=Site name: "
set /p "LAT=Latitude (for example 43.593): "
set /p "LON=Longitude (for example -110.848, west is negative): "
set "KM=2"
set /p "KM=Size in km, 2 to 5 [2]: "
call :site "%NAME%" %LAT% %LON% %KM%
goto done

:coverage
if not exist "%OUT%" mkdir "%OUT%"
chcp 65001 >nul
dotnet run --project "%SPIKE%\src\DataSpike.Cli" -- coverage --out "%OUT%\s1m-coverage.json" <nul
goto done

:site
if not exist "%OUT%" mkdir "%OUT%"
set "FILE=%~1"
set "FILE=%FILE: =-%"
echo.
echo Downloading live data for %~1 (%~4 km). Nothing is saved except a small report.
echo.
chcp 65001 >nul
dotnet run --project "%SPIKE%\src\DataSpike.Cli" -- site --name "%~1" --lat %2 --lon %3 --km %4 --out "%OUT%\%FILE%-%~4km.json" <nul
exit /b 0

:done
chcp %OLDCP% >nul
echo.
echo Reports are in %OUT%
pause
goto menu
