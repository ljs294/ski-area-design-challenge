@echo off
cd /d "%~dp0"
title Ski Area Design Challenge (Dev)
call npm run dev
if errorlevel 1 (
  echo.
  echo Dev server exited with an error. Press any key to close this window.
  pause >nul
)
