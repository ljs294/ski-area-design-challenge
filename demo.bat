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
echo Starting the game. WASD move, Q/E rotate, R/F tilt, wheel zoom, N snow on/off, T tree snow, V cover map, C Corbet's Couloir, Esc quit.
start "" "%GAME%"
goto menu

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
