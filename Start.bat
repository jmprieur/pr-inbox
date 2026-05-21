@echo off
REM Start.bat -- build the PrInbox solution, launch the Web UI, and open it in
REM your default browser. Mirrors the README Quick-Start (Option A) so a fresh
REM clone can be running with one double-click.
REM
REM Run from anywhere -- this script cd's to its own directory.

setlocal

set "PRINBOX_URL=http://localhost:7341"
set "ASPNETCORE_URLS=%PRINBOX_URL%"

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
echo [2/3] Opening splash page (auto-redirects to %PRINBOX_URL% when ready)...
start "" "%~dp0tools\splash.html"

echo.
echo [3/3] Starting PrInbox.Web on %PRINBOX_URL% -- press Ctrl+C to stop.
echo.
dotnet run --project "src\PrInbox.Web" --no-launch-profile --no-build

popd
endlocal
