@echo off
rem Hands-on demo launcher. Double-click it, or run it from a terminal.
rem Each phase adds its demos here; the Unity game gets a "Play" entry once it builds.
setlocal EnableExtensions
chcp 65001 >nul
title Ski Area Design Challenge - demos
set "SPIKE=%~dp0tools\data-spike"
set "OUT=%SPIKE%\results\local"

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
echo     Q  Quit
echo.
set "CHOICE="
set /p "CHOICE=Choose: "
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
if /i "%CHOICE%"=="Q" exit /b 0
goto menu

:tests
dotnet test "%SPIKE%\tests\DataSpike.Tests"
goto done

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
dotnet run --project "%SPIKE%\src\DataSpike.Cli" -- coverage --out "%OUT%\s1m-coverage.json"
goto done

:site
if not exist "%OUT%" mkdir "%OUT%"
set "FILE=%~1"
set "FILE=%FILE: =-%"
echo.
echo Downloading live data for %~1 (%~4 km). Nothing is saved except a small report.
echo.
dotnet run --project "%SPIKE%\src\DataSpike.Cli" -- site --name "%~1" --lat %2 --lon %3 --km %4 --out "%OUT%\%FILE%-%~4km.json"
exit /b 0

:done
echo.
echo Reports are in %OUT%
pause
goto menu
