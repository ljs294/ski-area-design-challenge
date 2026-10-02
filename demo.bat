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
set "LIFTLAB=%~dp0Builds\LiftLab\LiftLab.exe"
set "PICKER=%~dp0Builds\PickerLab\PickerLab.exe"
set "PICKED=%LOCALAPPDATA%\SkiAreaDesignChallenge\picked-site.args"
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
echo     21 Forest benchmark: 8 fixed views of Jackson Hole, GPU times and screenshots (about 2 minutes; needs 12)
echo.
echo   Lift assets: Sessellift FGQ-4 chairlift and SLE snow guns
echo     22 Rebuild the lifts, chairs and snow guns in Blender, import them into Unity and build the Lift Lab (about 4 minutes)
echo     23 Open the Lift Lab: terminals, chair, towers (6), snow guns (7, 8), Monta (9), chairs (0), LODs, snow, benchmark (B)
echo.
echo   Phase 1, task 09: forest at scale
echo     24 Crystal Mountain, 5 km: download it into your library (Cascades species; about 3 minutes)
echo     25 Fly over Crystal Mountain in the game (needs 24)
echo     26 Forest benchmark on Crystal Mountain: its own views, GPU times, draw calls (about 2 minutes; needs 24)
echo     27 Forest report for every mountain you have: trees, species, treeline (a few seconds each)
echo     28 Species survey: tree species at every US ski area, then the model priority report (about 3 hours; resumes)
echo.
echo   Phase 1, task 09 phase 2: New England tree species
echo     29 Sugarloaf, Maine, 5 km: download it into your library (spruce-fir, krummholz, northern hardwoods; about 3 minutes)
echo     30 Fly over Sugarloaf in the game (needs 29)
echo     31 Forest benchmark on Sugarloaf: its own views, GPU times, draw calls (about 2 minutes; needs 29)
echo.
echo   Game UI design (mockups, not the game yet)
echo     32 Open the HUD layout mockup in your browser: status bar, Toolbox, Analysis, menu and Settings
echo.
echo   Forest structure (NE8)
echo     33 Stand in a Sugarloaf spruce-fir stand: understory and clumps (needs 29)
echo.
echo   Phase 1, task 13: site picker
echo     34 Pick a site on the map, then download it into your library (builds the Picker Lab the first time)
echo     35 The picker with no network: the offline panel
echo     36 Rebuild the Picker Lab (after pulling new code; close the Unity editor first)
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
if /i "%CHOICE%"=="32" (
  start "" "%~dp0docs\plans\prototypes\ui-layout.html"
  goto menu
)
if /i "%CHOICE%"=="34" goto picker
if /i "%CHOICE%"=="35" goto pickeroffline
if /i "%CHOICE%"=="36" goto rebuildpicker
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
echo Starting the game. WASD or arrows pan, Q/E rotate, R/F tilt, wheel or +/- zoom, Home resets the view, C free-fly (PgUp/PgDn rise and sink), Shift+1 snow, Shift+3 forest, Shift+4 cover map, H hide the UI, P photo mode (F12 saves a picture), F1 every key plus the developer panel (wind, tree snow, light and time of day, haze, lakes, distant shadows, Corbet's), Esc menu (Quit is there).
start "" "%GAME%"
goto menu

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
"%GAME%" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -site "Crystal Mountain" -benchmark "%~dp0test-results\benchmark\crystal.json" -logFile "%~dp0test-results\benchmark\crystal.log" <nul
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

:benchsugarloaf
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\benchmark" mkdir "%~dp0test-results\benchmark"
echo Running the forest benchmark on Sugarloaf (the game flies its views, then closes by itself)...
"%GAME%" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -site "Sugarloaf" -benchmark "%~dp0test-results\benchmark\sugarloaf.json" -logFile "%~dp0test-results\benchmark\sugarloaf.log" <nul
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

:lineup
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\lineup" mkdir "%~dp0test-results\lineup"
echo Capturing the tree lineup (the game window opens and closes by itself)...
"%GAME%" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -lineup "%~dp0test-results\lineup\lineup" -logFile "%~dp0test-results\lineup\lineup.log" <nul
start "" "%~dp0test-results\lineup"
goto done

:benchmark
if not exist "%GAME%" call :buildplayer
if not exist "%GAME%" goto done
if not exist "%~dp0test-results\benchmark" mkdir "%~dp0test-results\benchmark"
echo Running the forest benchmark (the game flies 8 views, then closes by itself)...
"%GAME%" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -benchmark "%~dp0test-results\benchmark\bench.json" -logFile "%~dp0test-results\benchmark\bench.log" <nul
findstr /l /c:"[Benchmark]" "%~dp0test-results\benchmark\bench.log"
start "" "%~dp0test-results\benchmark"
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
start "" /wait "%PICKER%" -screen-fullscreen 0 -screen-width 1600 -screen-height 900
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
start "" /wait "%PICKER%" -offline -screen-fullscreen 0 -screen-width 1600 -screen-height 900
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
