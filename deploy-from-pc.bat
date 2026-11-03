@echo off
setlocal
cd /d "%~dp0"

rem Build the bot on THIS PC and update the server over SSH (fast: the VPS only copies files and restarts).
rem Usage:  deploy-from-pc.bat                 -> server below
rem         deploy-from-pc.bat user@host [port] -> another server
rem Needs: .NET 8 SDK on this PC, SSH key login to the server (no password), sudoers rule for update.sh.
set HOST=greatnessadmin@greatness-bot.duckdns.org
rem SSH port of the server (22 is closed from outside; sshd also listens on 2222)
set PORT=2222
if not "%~1"=="" set HOST=%~1
if not "%~2"=="" set PORT=%~2
set RID=linux-x64
set OUT=deploy-linux
set REMOTE=/opt/GrtnsBot/deploy-linux
set PACK=%TEMP%\greatnessbot-deploy.tgz

echo === git pull ===
git pull --ff-only
if errorlevel 1 (
    echo.
    echo git pull FAILED - commit or stash local changes first.
    pause
    exit /b 1
)

echo.
echo === Building for %RID% (ReadyToRun) ===
if exist "%OUT%" rmdir /s /q "%OUT%"
rem Same build as deploy/linux/server-build.sh: single file without compression, precompiled code.
dotnet publish -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:DebugType=None ^
  -o "%OUT%\app"
if errorlevel 1 goto failed

copy /y appsettings.json "%OUT%\app\" >nul
xcopy /i /y /q deploy\linux\*.sh "%OUT%" >nul
xcopy /i /y /q deploy\linux\*.service "%OUT%" >nul
xcopy /i /y /q deploy\linux\*.md "%OUT%" >nul

echo.
echo === Packing ===
if exist "%PACK%" del /q "%PACK%"
tar -czf "%PACK%" -C "%OUT%" .
if errorlevel 1 goto failed

echo.
echo === Uploading to %HOST% ===
scp -P %PORT% "%PACK%" "%HOST%:/tmp/greatnessbot-deploy.tgz"
if errorlevel 1 goto failed

echo.
echo === Updating the server ===
ssh -p %PORT% -t %HOST% "set -e; rm -rf %REMOTE%; mkdir -p %REMOTE%; tar -xzf /tmp/greatnessbot-deploy.tgz -C %REMOTE%; rm -f /tmp/greatnessbot-deploy.tgz; sed -i 's/\r$//' %REMOTE%/*.sh %REMOTE%/*.service; sudo /usr/bin/bash %REMOTE%/update.sh"
if errorlevel 1 goto failed

del /q "%PACK%" 2>nul
echo.
echo Done. The bot is updated and restarted.
pause
exit /b 0

:failed
echo.
echo Deploy FAILED - see the messages above. The bot on the server keeps running the previous build.
pause
exit /b 1
