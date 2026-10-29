@echo off
title Greatness Bot
rem Always run from the project folder: settings = appsettings.json here, data = data\ here
cd /d "%~dp0"

if exist publish\LfgBot.exe (
    publish\LfgBot.exe
) else (
    dotnet run -c Release
)

echo.
echo Bot stopped (exit code %errorlevel%)
pause
