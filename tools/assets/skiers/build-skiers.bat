@echo off
rem Builds the game's copy of the skier and the snowboarder (out\skiers.bytes) and checks it. Needs Blender 5.2 LTS.
rem Then Unity: Mountain Planner > Import Skiers (or demo.bat 105, which also rebuilds the game).
setlocal
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
set "HERE=%~dp0"
if not exist "%BLENDER%" (
  echo Blender 5.2 was not found at "%BLENDER%".
  pause
  exit /b 1
)
echo Baking the skier and the snowboarder (about 15 seconds)...
"%BLENDER%" -b --factory-startup --python-exit-code 1 --python "%HERE%build_skiers.py" -- --out "%HERE%out" <nul | findstr /r /c:"tris" /c:"Blender check" /c:"Checks" /c:"PROBLEMS" /c:"  " /c:"Error" /c:"Traceback"
if errorlevel 1 (echo   The skier build failed. & pause & exit /b 1)
pause
