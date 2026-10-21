@echo off
cd /d "%~dp0"

echo === Publishing LfgBot (one self-contained exe) ===
rem Result: publish\LfgBot.exe with .NET and all libraries inside.
rem Settings (appsettings.json) and data (data\) stay in this folder, start-bot.bat runs the exe from here.
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -o publish
if errorlevel 1 (
    echo.
    echo Publish FAILED
    pause
    exit /b 1
)

echo.
echo Done: publish\LfgBot.exe  -- start the bot with start-bot.bat
pause
