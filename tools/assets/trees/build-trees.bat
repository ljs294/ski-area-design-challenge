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
echo Building 14 species and krummholz x 3 variants x 3 LODs and rendering the review set (about 8 minutes)...
"%BLENDER%" -b --factory-startup --python-exit-code 1 --python "%HERE%build_trees.py" -- --out "%HERE%out" --render "%HERE%out\renders" <nul | findstr /r /c:"tris" /c:"Budget" /c:"rendered" /c:"Error" /c:"Traceback"
findstr /c:"tris >" "%HERE%out\trees.json" >nul 2>nul && echo WARNING: some trees are over their performance budget - see out\trees.json
start "" "%HERE%out\renders"
start "" "%HERE%out"
pause
