@echo off
rem Builds the building assets and opens the Gate 1 renders. Needs Blender 5.2 LTS.
setlocal
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
set "HERE=%~dp0"
if not exist "%BLENDER%" (
  echo Blender 5.2 was not found at "%BLENDER%".
  pause
  exit /b 1
)
echo Building the buildings and rendering their Gate 1 pictures (about 30 seconds)...
"%BLENDER%" -b --factory-startup --python-exit-code 1 --python "%HERE%build_buildings.py" -- --out "%HERE%out" --render "%HERE%out\renders" <nul | findstr /r /c:"tris per" /c:"Checks" /c:"PROBLEMS" /c:"Error" /c:"Traceback"
start "" "%HERE%out\renders"
pause
