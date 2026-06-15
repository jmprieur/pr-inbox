@echo off
REM Start.bat -- build the PrInbox Web app, stage it to a run directory, launch
REM it, and open it in your default browser. Mirrors the README Quick-Start
REM (Option A) so a fresh clone can be running with one double-click.
REM
REM Why publish to .run\web instead of `dotnet run` from src\...\bin:
REM the running app locks its own DLLs. Running from a staged copy keeps the
REM source bin\ writable, so `dotnet build` / `dotnet test` never fail with
REM "file in use by pr-inbox-web" while the app is up.
REM
REM Run from anywhere -- this script cd's to its own directory.

setlocal

set "PRINBOX_URL=http://localhost:7341"
set "ASPNETCORE_URLS=%PRINBOX_URL%"
set "RUN_DIR=.run\web"

pushd "%~dp0"

echo.
echo [1/3] Building and staging PrInbox.Web to %RUN_DIR% ...
echo.
dotnet publish "src\PrInbox.Web" -c Debug -o "%RUN_DIR%" --nologo
if errorlevel 1 (
    echo.
    echo Build failed. See errors above.
    echo (If publish failed copying into %RUN_DIR%, an instance may already be
    echo  running -- stop it first, then re-run Start.bat.^)
    popd
    endlocal
    exit /b 1
)

echo.
echo [2/3] Opening splash page (auto-redirects to %PRINBOX_URL% when ready)...
start "" "%~dp0tools\splash.html"

echo.
echo [3/3] Starting PrInbox.Web on %PRINBOX_URL% -- press Ctrl+C to stop.
echo       (Running from %RUN_DIR%; src\PrInbox.Web\bin stays free for rebuilds.^)
echo.
dotnet "%RUN_DIR%\pr-inbox-web.dll"

popd
endlocal

