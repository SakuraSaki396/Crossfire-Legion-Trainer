@echo off
setlocal

rem  Rebuilds the standalone installer exe (single file, needs .NET Framework 4.8 to run).
rem  Usage:  build_setup.bat ["<game dir>"]
rem  Env:    CFL_GAME_DIR  game folder when no argument is given (must already have BepInEx + the trainer deployed)
rem          CFL_DOTNET    portable SDK folder (optional, only if dotnet is not on PATH)

if not "%CFL_DOTNET%"=="" (
  set DOTNET_ROOT=%CFL_DOTNET%
  set PATH=%CFL_DOTNET%;%PATH%
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1

set GAME=%~1
if "%GAME%"=="" set GAME=%CFL_GAME_DIR%
if "%GAME%"=="" set GAME=E:\SteamLibrary\steamapps\common\Crossfire Legion (Demo)

cd /d "%~dp0"

echo [1/3] collecting deployed files...
python pack_payload.py "%GAME%"
if errorlevel 1 goto :err

echo [2/3] building installer...
dotnet build -c Release -v m
if errorlevel 1 goto :err

echo [3/3] exporting single-file exe...
python export_exe.py
if errorlevel 1 goto :err

echo.
echo [OK] built successfully
exit /b 0

:err
echo.
echo [FAIL] Build failed.
exit /b 1
