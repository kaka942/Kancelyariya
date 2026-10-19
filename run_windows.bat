@echo off
chcp 65001 > nul
cd /d "%~dp0"

if exist "publish\Kancelyariya.exe" (
    start "" "publish\Kancelyariya.exe" %*
) else (
    dotnet run --project Kancelyariya.csproj %*
)
