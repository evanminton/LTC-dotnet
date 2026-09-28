@echo off
rem Double-click: publish standalone LTC Studio (Release, win-x64) to artifacts\. Extra args pass through, e.g. publish.cmd -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if errorlevel 1 pause
