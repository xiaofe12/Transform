@echo off
setlocal
REM ============================================================
REM  Thanks.Transform 一键构建 + 部署
REM  （本脚本曾在 2026-08-24 仓库重建时丢失，2026-08-25 重建）
REM ============================================================
cd /d "%~dp0"

set "PEAKRoot=D:\Steam\steamapps\common\PEAK"
set "DLL=bin\Release\Thanks.Transform.dll"

echo [1/3] Building Release...
dotnet build Transform.csproj -c Release -v minimal
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

echo [2/3] Deploying to Steam path...
copy /Y "%DLL%" "%PEAKRoot%\BepInEx\plugins\Thanks.Transform.dll" >nul

echo [3/3] Deploying to Thunderstore profile...
copy /Y "%DLL%" "%USERPROFILE%\AppData\Roaming\Thunderstore Mod Manager\DataFolder\PEAK\profiles\Default\BepInEx\plugins\Thanks.Transform.dll" >nul

echo Done. 0 warnings / 0 errors expected above.
endlocal
