@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title Ski Area Design Challenge

echo ============================================
echo   Ski Area Design Challenge - Local Launcher
echo ============================================
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

REM --- Launch the game ----------------------------------------------------
echo Starting the game... an Electron window will open shortly.
echo Close that window ^(or press Ctrl+C here^) to stop.
echo.
call npm run dev

if errorlevel 1 (
  echo.
  echo The game exited with an error. Press any key to close this window.
  pause >nul
)
endlocal
