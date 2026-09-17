@echo off
setlocal
cd /d "%~dp0.."
if not exist node_modules\vite\bin\vite.js (
  echo Installing pinned project dependencies...
  call npm ci
  if errorlevel 1 exit /b %errorlevel%
)
cd /d "%~dp0"
echo Starting the standalone Three.js game in Electron...
call npm run dev
