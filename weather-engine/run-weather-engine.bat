@echo off
setlocal

cd /d "%~dp0.."

if not exist "package.json" (
  echo Could not find the Mountain Planner package.json.
  echo Expected it at: %CD%
  pause
  exit /b 1
)

call npm run sim:weather

if errorlevel 1 (
  echo.
  echo The weather engine exited with an error.
  pause
)
