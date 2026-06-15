@echo off
REM Start.bat -- build PrInbox and launch the system-tray app. No console is
REM left running: the app lives as a "PR" icon in the Windows notification area
REM (click the ^ arrow near the clock if you don't see it). The tray starts the
REM web server hidden, opens the dashboard in your browser, and the tray menu
REM lets you Open, Restart, or Stop & Exit.
REM
REM Both the tray and the web server run from a staged copy under .run\ (not from
REM src\...\bin), and the tray is pointed at the staged web via PRINBOX_WEB_EXE.
REM That keeps the source build output writable, so `dotnet build` / `dotnet
REM test` never fail with "file in use by pr-inbox-web" while PR Inbox is up.
REM
REM Run from anywhere -- this script cd's to its own directory.
REM
REM Dev tip: to run the web app directly with a live console instead, set
REM ASPNETCORE_URLS and `dotnet run --project src\PrInbox.Web` (see README).

setlocal

set "RUN_WEB=.run\web"
set "RUN_TRAY=.run\tray"
set "TRAY_EXE=%RUN_TRAY%\PrInbox.Tray.exe"

pushd "%~dp0"

echo.
echo [1/3] Building PrInbox.slnx ...
echo.
dotnet build PrInbox.slnx --nologo
if errorlevel 1 (
    echo.
    echo Build failed. See errors above.
    popd
    endlocal
    exit /b 1
)

echo.
echo [2/3] Staging tray + web to .run\ ^(keeps src\...\bin free for rebuilds^)...
echo.
dotnet publish "src\PrInbox.Web" -c Debug -o "%RUN_WEB%" --no-build --nologo
if errorlevel 1 goto stage_failed
dotnet publish "src\PrInbox.Tray" -c Debug -o "%RUN_TRAY%" --no-build --nologo
if errorlevel 1 goto stage_failed

if not exist "%TRAY_EXE%" (
    echo.
    echo Could not find %TRAY_EXE% after staging.
    popd
    endlocal
    exit /b 1
)

REM Point the tray at the staged web exe so the running web never locks
REM src\PrInbox.Web\bin. Absolute path -- the tray's working dir differs.
set "PRINBOX_WEB_EXE=%~dp0%RUN_WEB%\pr-inbox-web.exe"

echo.
echo [3/3] Launching PR Inbox tray app ^(look for the PR icon near the clock^)...
start "" "%TRAY_EXE%"

popd
endlocal
exit /b 0

:stage_failed
echo.
echo Staging to .run\ failed -- a PR Inbox instance may already be running and
echo is locking the staged copy. Use the tray's "Stop ^& Exit" menu (or close
echo it), then re-run Start.bat.
popd
endlocal
exit /b 1
