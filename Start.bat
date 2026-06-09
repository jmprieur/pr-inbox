@echo off
REM Start.bat -- build PrInbox and launch the system-tray app. No console is
REM left running: the app lives as a "PR" icon in the Windows notification area
REM (click the ^ arrow near the clock if you don't see it). The tray starts the
REM web server hidden, opens the dashboard in your browser, and the tray menu
REM lets you Open, Restart, or Stop & Exit.
REM
REM Run from anywhere -- this script cd's to its own directory.
REM
REM Dev tip: to run the web app directly with a live console instead, use
REM Option A in the README (set ASPNETCORE_URLS, then dotnet run).

setlocal

pushd "%~dp0"

set "TRAY_EXE=src\PrInbox.Tray\bin\Debug\net10.0-windows\PrInbox.Tray.exe"

echo.
echo [1/2] Building PrInbox.slnx ...
echo.
dotnet build PrInbox.slnx --nologo
if errorlevel 1 (
    echo.
    echo Build failed. See errors above.
    popd
    endlocal
    exit /b 1
)

if not exist "%TRAY_EXE%" (
    echo.
    echo Could not find %TRAY_EXE% after build.
    popd
    endlocal
    exit /b 1
)

echo.
echo [2/2] Launching PR Inbox tray app ^(look for the PR icon near the clock^)...
start "" "%TRAY_EXE%"

popd
endlocal
