@echo off
rem Simosa BRM dev shell: opens a PowerShell window whose caches and temp files are redirected
rem into this project folder (current session only; nothing is written to user-level settings).
rem Double-click this file, or run it from cmd. See docs\ops (local data locations).
cd /d "%~dp0.."
powershell -NoLogo -NoExit -ExecutionPolicy Bypass -Command "& '%~dp0setup-windows.ps1' -SessionOnly"
