@echo off
setlocal
cd /d "%~dp0"
if not exist node_modules\vite\bin\vite.js (
  echo Installing pinned project dependencies...
  call npm ci
  if errorlevel 1 exit /b %errorlevel%
)
echo Starting the Phase 2 Three.js version...
call npm run dev:three -- --open /three.html
