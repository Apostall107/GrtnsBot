@echo off
cd /d "%~dp0"

rem Usage:  publish-linux.bat          -> x64 server (Intel/AMD, "uname -m" = x86_64)
rem         publish-linux.bat arm64    -> ARM server ("uname -m" = aarch64)
set RID=linux-x64
if /i "%~1"=="arm64" set RID=linux-arm64
set OUT=deploy-linux

echo === Building Greatness Bot for %RID% ===
if exist "%OUT%" rmdir /s /q "%OUT%"

rem Self-contained single file: .NET is inside, nothing to install on the server except system libraries.
rem No IncludeNativeLibrariesForSelfExtract on Linux: native .so files stay next to the binary,
rem so the service user does not need a writable home folder for self-extraction.
dotnet publish -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -o "%OUT%\app"
if errorlevel 1 (
    echo.
    echo Publish FAILED
    pause
    exit /b 1
)

copy /y appsettings.json "%OUT%\app\" >nul
xcopy /e /i /y /q deploy\linux "%OUT%" >nul
if exist data xcopy /e /i /y /q data "%OUT%\data" >nul

echo.
echo Done: folder %OUT% is ready.
echo Upload the WHOLE folder %OUT% to the server and follow %OUT%\README-DEBIAN.md
pause
