@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title Ski Area Design Challenge

echo ============================================
echo   Ski Area Design Challenge - Play (release)
echo ============================================
echo   Production build. For hot-reload development
echo   with dev tooling, use run-dev.bat instead.
echo.

REM --- Make sure Node.js is available -------------------------------------
where node >nul 2>nul
if errorlevel 1 (
  echo Node.js was not found on PATH. Checking common install locations...
  if exist "%ProgramFiles%\nodejs\node.exe" (
    set "PATH=%ProgramFiles%\nodejs;!PATH!"
  ) else if exist "%ProgramFiles(x86)%\nodejs\node.exe" (
    set "PATH=%ProgramFiles(x86)%\nodejs;!PATH!"
  ) else if exist "%LOCALAPPDATA%\Programs\nodejs\node.exe" (
    set "PATH=%LOCALAPPDATA%\Programs\nodejs;!PATH!"
  )
)

where node >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: Node.js is not installed or could not be found.
  echo Install it from https://nodejs.org/ ^(LTS^) and run this again.
  echo.
  pause
  exit /b 1
)

for /f "delims=" %%v in ('node --version') do echo Using Node.js %%v
echo.

REM --- Install dependencies on first run ----------------------------------
if not exist "node_modules" (
  echo Installing dependencies ^(first run only, this can take a few minutes^)...
  call npm install
  if errorlevel 1 (
    echo.
    echo ERROR: npm install failed. See messages above.
    pause
    exit /b 1
  )
  echo.
)

REM --- Build the production bundle ----------------------------------------
REM Dev mode (npm run dev) runs React StrictMode, which double-invokes every
REM render and effect, and ships unminified modules over the Vite dev server.
REM That is a large, across-the-board slowdown in a map app this size. This
REM launcher builds first so the game runs at full speed.
REM
REM Pass "nobuild" to skip the rebuild and relaunch the last build:
REM   launch-game.bat nobuild
set "DOBUILD=1"
if /i "%~1"=="nobuild" set "DOBUILD="
if not exist "dist\index.html" set "DOBUILD=1"

if defined DOBUILD (
  echo Building the production bundle ^(type-check + bundle^)...
  echo This takes longer to start than dev mode, but the game runs much faster.
  echo.
  call npm run build
  if errorlevel 1 (
    echo.
    echo ERROR: Build failed. See messages above.
    pause
    exit /b 1
  )
  echo.
) else (
  echo Skipping rebuild ^(nobuild^) - launching the existing build.
  echo.
)

REM --- Launch the game ----------------------------------------------------
echo Starting the game... an Electron window will open shortly.
echo Close that window ^(or press Ctrl+C here^) to stop.
echo.
call npx electron .

if errorlevel 1 (
  echo.
  echo The game exited with an error. Press any key to close this window.
  pause >nul
)
endlocal
