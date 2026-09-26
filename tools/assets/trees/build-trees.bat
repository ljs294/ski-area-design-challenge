@echo off
rem Builds the tree species library and opens the review renders. Needs Blender 5.2 LTS.
setlocal
set "BLENDER=C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
set "HERE=%~dp0"
if not exist "%BLENDER%" (
  echo Blender 5.2 was not found at "%BLENDER%".
  pause
  exit /b 1
)
echo Building 3 species x 3 variants x 3 LODs and rendering previews (about a minute)...
"%BLENDER%" -b --factory-startup --python "%HERE%build_trees.py" -- --out "%HERE%out" --render "%HERE%out\renders" <nul | findstr /r /c:"^Subalpine" /c:"^Mountain" /c:"^Quaking" /c:"rendered" /c:"Error" /c:"Traceback"
if errorlevel 1 echo (no build output - check that Blender ran)
start "" "%HERE%out\renders"
start "" "%HERE%out"
pause
