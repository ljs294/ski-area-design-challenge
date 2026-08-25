@echo off
setlocal

cd /d "%~dp0.."

if not exist "package.json" (
  echo Could not find the Mountain Planner package.json.
  echo Expected it at: %CD%\package.json
  pause
  exit /b 1
)

if not exist "node_modules\tsx\dist\cli.mjs" (
  echo Project dependencies are not installed.
  echo Run "npm install" from:
  echo %CD%
  pause
  exit /b 1
)

call npm run sim:weather

if errorlevel 1 (
  echo.
  echo The weather engine exited with an error.
  pause
)
