@echo off
rem Task P2-10: the one-command Windows release - checks, EditMode tests, the build with the Jackson Hole demo, a smoke
rem test of the shipped exe, then Builds\Release\SkiAreaDesignChallenge-<version>-win64.zip. A commit tagged vX.Y.Z
rem builds release X.Y.Z; anything else is a preview. Arguments go to tools\release\release.ps1:
rem   -AllowDirty  build with uncommitted changes (a preview)     -SkipTests  compile check only
rem   -NoSmoke     skip the smoke test                            -DemoPackage <folder>  the demo to bundle
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\release.ps1" %*
set "CODE=%ERRORLEVEL%"
rem Double-clicked: keep the window open to read the result.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %CODE%
