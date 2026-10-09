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
set "LIFTLAB=%~dp0Builds\LiftLab\LiftLab.exe"
set "PICKER=%~dp0Builds\PickerLab\PickerLab.exe"
set "PICKED=%LOCALAPPDATA%\SkiAreaDesignChallenge\picked-site.args"
set "SCRATCH=%LOCALAPPDATA%\SkiAreaDesignChallenge-scratch"
set "FORMATS=%LOCALAPPDATA%\SkiAreaDesignChallenge-formats"
set "LIBDEMO=%LOCALAPPDATA%\SkiAreaDesignChallenge-library"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe"

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
echo   Phase 2, task 05: settings (needs a downloaded area; close the Unity editor first)
echo     53 Capture every Settings page at 1920x1080, 2560x1080 and 3440x1440, dark and light, 50-150%% (about 5 minutes)
echo        Then play it: Settings from the title or the in-game menu. Graphics changes show at once; Display asks to keep
echo        a new mode or resolution; Controls - click a key, press the new one; Data - Offline mode, Free space.
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
echo Starting the game with the network off ^(-offline^): Load Area, then open any area. A new download stops with a network error.
start "" "%GAME%" -data "%SCRATCH%" -offline
goto menu

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
"%GAME%" %SCREEN% -site "Crystal Mountain" -benchmark "%~dp0test-results\benchmark\crystal.json" -logFile "%~dp0test-results\benchmark\crystal.log" <nul
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
"%GAME%" %SCREEN% -site "Sugarloaf" -benchmark "%~dp0test-results\benchmark\sugarloaf.json" -logFile "%~dp0test-results\benchmark\sugarloaf.log" <nul
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
"%GAME%" -quality high -lineup "%~dp0test-results\forest-look\l" -lineupset top -logFile "%~dp0test-results\forest-look\lineup.log" <nul
start "" "%~dp0test-results\forest-look"
echo Starting the game above the Jackson Hole forest. Shift+1: snow off and on; wheel: zoom; drag: orbit.
start "" "%GAME%" -quality high -view 350,-150,600,200,80
goto menu

:lineup
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\lineup" mkdir "%~dp0test-results\lineup"
echo Capturing the tree lineup (the game window opens and closes by itself)...
"%GAME%" %SCREEN% -lineup "%~dp0test-results\lineup\lineup" -logFile "%~dp0test-results\lineup\lineup.log" <nul
start "" "%~dp0test-results\lineup"
goto done

:benchmark
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
echo Running the benchmark at High (the game flies the path once to warm up, twice to measure, then closes by itself)...
"%GAME%" %SCREEN% -quality high -benchmark "%BENCH%\bench.json" -logFile "%BENCH%\bench.log" <nul
findstr /l /c:"[Benchmark]" "%BENCH%\bench.log"
start "" "%BENCH%"
goto done

rem Task 15: the frame times of record come from the release game; the Development game adds exact garbage per
rem frame and graphics memory (counters a release game doesn't record). Each run is compared with the stored baseline.
:benchfull
if exist "%~dp0..\.gpu-lock" (
  echo Another session is using the GPU ^(%~dp0..\.gpu-lock^). Try again when it has finished.
  goto done
)
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%GAMEDEV%" call :builddev
if not exist "%GAMEDEV%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
for %%Q in (high medium) do (
  echo Benchmark at %%Q, the release game ^(about 5 minutes^)...
  "%GAME%" %SCREEN% -quality %%Q -benchmark "%BENCH%\%%Q.json" -logFile "%BENCH%\%%Q.log" <nul
  echo Benchmark at %%Q, the Development game: garbage and memory ^(about 3 minutes^)...
  "%GAMEDEV%" %SCREEN% -quality %%Q -laps 1 -benchmark "%BENCH%\%%Q-dev.json" -logFile "%BENCH%\%%Q-dev.log" <nul
)
echo Benchmark at high with the HUD on, the Development game ^(about 3 minutes^)...
"%GAMEDEV%" %SCREEN% -quality high -laps 1 -withhud -benchmark "%BENCH%\high-dev-hud.json" -logFile "%BENCH%\high-dev-hud.log" <nul
echo.
for %%R in (high high-dev high-dev-hud medium medium-dev) do findstr /l /c:" at 1920x1080" "%BENCH%\%%R.log"
echo.
node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-high.json" "%BENCH%\high.json"
node "%~dp0tools\perf\compare.mjs" "%~dp0docs\perf\jackson-hole-5km-medium.json" "%BENCH%\medium.json"
start "" "%BENCH%"
goto done

:benchpreset
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%BENCH%" mkdir "%BENCH%"
set "QUALITY=high"
set /p "QUALITY=Quality preset (low, medium, high or ultra; Enter for high): "
echo Benchmark at %QUALITY% (about 4 minutes)...
"%GAME%" %SCREEN% -quality %QUALITY% -benchmark "%BENCH%\%QUALITY%.json" -logFile "%BENCH%\%QUALITY%.log" <nul
findstr /l /c:"[Benchmark]" "%BENCH%\%QUALITY%.log"
start "" "%BENCH%"
goto done

:uicapture
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\ui-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
echo Capturing every screen (about 15 minutes; the window shows the title while it works)...
"%GAME%" -uicapture "%UICAP%" -logFile "%~dp0test-results\ui-captures.log" <nul
type "%UICAP%\report.txt"
start "" "%UICAP%"
goto done

:settingscapture
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\settings-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
echo Capturing every Settings page (the window shows the title while it works)...
"%GAME%" -uicapture "%UICAP%" -uionly s8-settings,s8-settings-units,s8-settings-graphics,s8-settings-display,s8-settings-controls,s8-settings-data,s8-settings-game -uisizes 1920x1080,2560x1080,3440x1440 -logFile "%~dp0test-results\settings-captures.log" <nul
type "%UICAP%\report.txt"
start "" "%UICAP%"
goto done

:hudparity
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
set "UICAP=%~dp0test-results\hud-captures"
if exist "%UICAP%" rmdir /s /q "%UICAP%"
set "HUDSTATES=s6-hud,s6-float,s6-menu,s6-layers,s6-slope,s6-exposure,s6-depth,s6-contours,s6-slope-contours,s6-tray-lifts,s6-tray-trails,s6-tray-snow,s6-tray-infra,s6-analysis,s6-analysis-lifts,s6-analysis-weather,s6-analysis-finances,s6-rstats"
echo Capturing the HUD's states at 1920x1080, 2560x1440 and 3440x1440 (the window shows the title while it works)...
"%GAME%" -uicapture "%UICAP%" -uionly %HUDSTATES% -uisizes 1920x1080,2560x1440,3440x1440 -hudopacity 100 -logFile "%~dp0test-results\hud-captures.log" <nul
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
set "PREFS=HKCU\Software\Ski Area Design Challenge\Ski Area Design Challenge"
rem The game and both labs share this key, and each saves its window when it quits.
for %%E in (SkiAreaDesignChallenge.exe PickerLab.exe LiftLab.exe) do (
  tasklist /fi "imagename eq %%E" | find /i "%%E" >nul && (echo   Close %%E first: it saves its window mode when it quits. & goto done)
)
powershell -NoProfile -Command "$k='Registry::%PREFS%'; if (Test-Path $k) { $n=(Get-Item $k).Property | Where-Object { $_ -like 'Screenmanager *' }; $n | ForEach-Object { Remove-ItemProperty -Path $k -Name $_ }; Write-Host ('  Removed ' + @($n).Count + ' saved window values; the game opens borderless full screen next time.') } else { Write-Host '  Nothing saved yet; the game already opens borderless full screen.' }"
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
echo Building the game player (about 2 minutes)...
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
