@echo off
REM Start-internal.bat -- Microsoft-internal launch.
REM
REM Applies the Microsoft profile (profiles\microsoft.json): the EMU / Proxima
REM identity classes and the `agency copilot` review launch command, via
REM `pr-inbox config import`. Then starts PR Inbox normally (Start.bat).
REM
REM Public users run Start.bat instead -- the shipped defaults carry nothing
REM Microsoft-specific.

setlocal
pushd "%~dp0"

echo.
echo Applying Microsoft profile (profiles\microsoft.json) ...
dotnet run --project "src\PrInbox.Cli" -- config import "%~dp0profiles\microsoft.json" --yes
if errorlevel 1 (
    echo.
    echo Profile import failed. See errors above.
    popd
    endlocal
    exit /b 1
)

popd
endlocal

REM Hand off to the normal launcher (build, stage, start tray).
call "%~dp0Start.bat"
