@echo off
rem usage: build.bat [output.exe]   (default: YaMumMember.exe next to this file)
cd /d "%~dp0"
set OUT=%~1
if "%OUT%"=="" set OUT=YaMumMember.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize /platform:anycpu ^
  /target:winexe /win32icon:app.ico ^
  /out:"%OUT%" /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.dll ^
  /resource:wwwroot\index.html,www.index.html ^
  /resource:wwwroot\app.js,www.app.js ^
  /resource:wwwroot\app.css,www.app.css ^
  /resource:wwwroot\check.html,www.check.html ^
  server.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Build OK: %OUT%
