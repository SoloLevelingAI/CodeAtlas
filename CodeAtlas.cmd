@echo off
if not exist "%~dp0artifacts\desktop\cli\CodeAtlas.Cli.exe" (
  echo Run scripts\build-desktop.ps1 first.
  exit /b 1
)
"%~dp0artifacts\desktop\cli\CodeAtlas.Cli.exe" %*
exit /b %errorlevel%
