@echo off
rem Builds the lift assets and opens the review photos. Needs Blender 5.2 LTS.
setlocal
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
set "HERE=%~dp0"
if not exist "%BLENDER%" (
  echo Blender 5.2 was not found at "%BLENDER%".
  pause
  exit /b 1
)
echo Building the Sessellift FGQ-4 terminals and chair and rendering the photo set (about 6 minutes)...
"%BLENDER%" -b --factory-startup --python-exit-code 1 --python "%HERE%build_lifts.py" -- --out "%HERE%out" --render "%HERE%out\renders" --shots photos <nul | findstr /r /c:"tris per" /c:"Checks" /c:"PROBLEMS" /c:"Error" /c:"Traceback"
start "" "%HERE%out\renders"
pause
