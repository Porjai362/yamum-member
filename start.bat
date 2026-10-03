@echo off
cd /d "%~dp0"
if not exist YaMumMember.exe call build.bat
start "" http://localhost:8088/
YaMumMember.exe
