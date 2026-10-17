@echo off
rem One-click launcher for the portable CNM build.
rem All user-facing text lives in start.ps1 (that file is UTF-8 with BOM, so Chinese shows fine).
rem This .cmd is kept ASCII-only on purpose: batch files get read in the OEM code page, and
rem non-ASCII bytes here would get mis-parsed on some systems.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
