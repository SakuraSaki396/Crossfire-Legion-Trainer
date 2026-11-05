@echo off
setlocal

rem  Builds the plugin and deploys dll + lua into the game folder.
rem  Usage:  build.bat ["<game dir>"]
rem  Env:    CFL_GAME_DIR  game folder when no argument is given
rem          CFL_DOTNET    portable SDK folder (optional, only if dotnet is not on PATH)

if not "%CFL_DOTNET%"=="" (
  set DOTNET_ROOT=%CFL_DOTNET%
  set PATH=%CFL_DOTNET%;%PATH%
)

set GAME=%~1
if "%GAME%"=="" set GAME=%CFL_GAME_DIR%
if "%GAME%"=="" set GAME=E:\SteamLibrary\steamapps\common\Crossfire Legion (Demo)

cd /d "%~dp0"

if not exist "%GAME%\Crossfire_Legion.exe" (
  echo.
  echo [FAIL] Game folder not found: "%GAME%"
  echo        Pass it as an argument:  build.bat "D:\Steam\steamapps\common\Crossfire Legion (Demo)"
  echo        or set CFL_GAME_DIR.
  exit /b 1
)

python stamp_version.py || goto :err
dotnet build -c Release -v m -p:GameDir="%GAME%" || goto :err

copy /y "bin\Release\CrossfireTrainer.dll" "%GAME%\BepInEx\plugins\" >nul
copy /y "zzz_trainer.lua" "%GAME%\Data\Scripts\Source\Extensions\" >nul

echo.
echo [OK] Deployed to "%GAME%"
exit /b 0

:err
echo.
echo [FAIL] Build failed.
exit /b 1
