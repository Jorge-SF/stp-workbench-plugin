@echo off
rem Double-click to build the STP UG plugin (Debug). Extra arguments go to tools\Build-Plugin.ps1,
rem e.g.  Build-Plugin.cmd -Configuration Release -Test
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Plugin.ps1" %*
