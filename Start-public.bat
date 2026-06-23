@echo off
REM Start-public.bat -- clean / public launch.
REM
REM Applies the public profile (profiles\public.json): resets the identity
REM taxonomy to just "Public" on github.com, the review command to the public
REM GitHub Copilot CLI, and the model to the current default -- via
REM `pr-inbox config import`. Then starts PR Inbox normally (Start.bat).
REM
REM Your sources / ADO references are NOT touched (a profile only ever carries
REM the identity classes, launch command, and model). This is the symmetric
REM counterpart to Start-internal.bat.

setlocal
pushd "%~dp0"

echo.
echo Applying public profile (profiles\public.json) ...
dotnet run --project "src\PrInbox.Cli" -- config import "%~dp0profiles\public.json"
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
