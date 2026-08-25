@echo off
setlocal
cd /d "%~dp0"
rem Atlas dist publisher - flat product layout: exes + web\ at dist\ top level.
rem   publish.cmd          framework-dependent (small; needs the .NET 10 runtime)
rem   publish.cmd -self    self-contained (bigger; no .NET install required)
set SC=--self-contained false
if /i "%~1"=="-self" set SC=--self-contained true
set OUT=%~dp0dist
if exist "%OUT%" rd /s /q "%OUT%"
dotnet publish src\atlas        -c Release -r win-x64 %SC% -o "%OUT%" || exit /b 1
dotnet publish src\Atlas.Server -c Release -r win-x64 %SC% -o "%OUT%" || exit /b 1
dotnet publish src\Atlas.App    -c Release -r win-x64 %SC% -o "%OUT%" || exit /b 1
robocopy web "%OUT%\web" /e /njh /njs /ndl /nc /ns >nul
if %errorlevel% geq 8 exit /b 1
echo.
echo dist ready: %OUT%
echo   Atlas.App.exe  - desktop app (first run asks for the game folder)
echo   atlas.exe      - CLI; atlas gui opens the browser GUI
exit /b 0
