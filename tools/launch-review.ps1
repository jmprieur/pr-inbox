#requires -Version 7.0
<#
.SYNOPSIS
    pr-inbox review launcher.

.DESCRIPTION
    Run inside a new Windows Terminal tab spawned by pr-inbox-web.

    By default (-AutoSend, on) the brief is passed straight to copilot
    via the pass-through `-i "<brief>"` flag, which starts the session
    interactively AND auto-executes the prompt — no Ctrl+V needed. The
    terminal stays open after the agent finishes so you can follow up.

    Pass -NoAutoSend to fall back to the legacy "copy to clipboard +
    paste manually" flow. Useful if you want to eyeball/edit the brief
    before sending it.

    Pass -Yolo to add `--yolo` to copilot's pass-through args (equivalent
    to --allow-all-tools --allow-all-paths --allow-all-urls). Skips every
    permission prompt for the duration of this session.

    The agent writes findings.yaml back into the run directory;
    pr-inbox-web watches for it.

.PARAMETER RunDirectory
    Absolute path to the run directory. Required.

.PARAMETER Agent
    Agent id. Defaults to security-toolkit:dual-model-review.

.PARAMETER Plugin
    --plugin argument. Defaults to
    github:1ES-microsoft/ai-plugins:plugins/security-toolkit
    (fetched once and cached by agency).

.PARAMETER Model
    --model argument. Defaults to claude-opus-4.7-xhigh.

.PARAMETER Mcps
    Comma-separated list of MCP servers to enable. Defaults to
    'workiq,teams'. Each entry becomes one --mcp flag.

.PARAMETER SessionName
    Optional human-readable name for the underlying copilot session.
    Diagnostic-only: agency uses `--resume <uuid>` internally so the
    pass-through `--name` flag is intentionally NOT forwarded (it's
    mutually exclusive with --resume). The wt tab title is the user-
    visible identifier.

.PARAMETER NoAutoSend
    Opt out of the default "send the brief automatically" behaviour.
    When set, the brief is copied to the clipboard and you Ctrl+V it
    into the agency prompt manually.

.PARAMETER Yolo
    Pass `--yolo` to copilot. Auto-approves all tool / path / URL
    permission prompts for the session. Off by default.
#>

param(
    [Parameter(Mandatory = $true)] [string] $RunDirectory,
    [string] $Agent       = $env:PRINBOX_REVIEW_AGENT,
    [string] $Plugin      = $env:PRINBOX_REVIEW_PLUGIN,
    [string] $Model       = $env:PRINBOX_REVIEW_MODEL,
    [string] $Mcps        = $env:PRINBOX_REVIEW_MCPS,
    [string] $SessionName = '',
    [switch] $NoAutoSend,
    [switch] $Yolo
)

if (-not $Agent)  { $Agent  = 'security-toolkit:dual-model-review' }
if (-not $Plugin) { $Plugin = 'github:1ES-microsoft/ai-plugins:plugins/security-toolkit' }
if (-not $Model)  { $Model  = 'claude-opus-4.7-xhigh' }
if (-not $Mcps)   { $Mcps   = 'workiq,teams' }

$ErrorActionPreference = 'Stop'

$briefPath    = Join-Path $RunDirectory 'brief.md'
$findingsPath = Join-Path $RunDirectory 'findings.yaml'

if (-not (Test-Path $briefPath)) {
    Write-Host "brief.md not found at $briefPath" -ForegroundColor Red
    exit 1
}

$brief    = Get-Content -Raw -Path $briefPath
$autoSend = -not $NoAutoSend
# Windows CreateProcess command-line limit is ~32K. If a brief gets near
# that, fall back to clipboard so the launch doesn't fail silently.
$briefBytes  = [System.Text.Encoding]::UTF8.GetByteCount($brief)
$autoTooLong = $briefBytes -gt 30000
if ($autoSend -and $autoTooLong) {
    $autoSend = $false
    $autoForcedOff = $true
} else {
    $autoForcedOff = $false
}

# Only copy-to-clipboard when we're NOT auto-sending; the user needs the
# clipboard for the manual Ctrl+V fallback.
$copied = $false
if (-not $autoSend) {
    try {
        Set-Clipboard -Value $brief
        $copied = $true
    } catch {
        $copied = $false
    }
}

# Expand the MCP list into a flat array of --mcp / <name> tokens
# so agency receives them as repeated flags.
$mcpArgs = @()
foreach ($m in ($Mcps -split ',')) {
    $m = $m.Trim()
    if ($m) {
        $mcpArgs += '--mcp'
        $mcpArgs += $m
    }
}

Write-Host ''
Write-Host '------------------------------------------------------------' -ForegroundColor DarkGray
if ($autoSend) {
    Write-Host (' Brief auto-send ON (' + $brief.Length + ' chars).') -ForegroundColor Green
    Write-Host ' Brief will be passed inline via copilot -i; session stays interactive.' -ForegroundColor DarkGray
} elseif ($autoForcedOff) {
    Write-Host (' Brief too long for auto-send (' + $briefBytes + ' bytes) — falling back to clipboard.') -ForegroundColor Yellow
    if ($copied) {
        Write-Host (' Brief copied to clipboard. Ctrl+V into agency, press Enter to send.') -ForegroundColor Yellow
    } else {
        Write-Host (' Brief at: ' + $briefPath) -ForegroundColor Yellow
    }
} else {
    if ($copied) {
        Write-Host (' Brief copied to clipboard (' + $brief.Length + ' chars).') -ForegroundColor Cyan
        Write-Host ' Ctrl+V into agency. Edit if needed. Press Enter to send.' -ForegroundColor Cyan
    } else {
        Write-Host (' Brief at: ' + $briefPath) -ForegroundColor Yellow
        Write-Host ' (Clipboard copy failed -- open the file manually.)' -ForegroundColor Yellow
    }
}
Write-Host (' Agent:    ' + $Agent)  -ForegroundColor DarkGray
Write-Host (' Plugin:   ' + $Plugin) -ForegroundColor DarkGray
Write-Host (' Model:    ' + $Model)  -ForegroundColor DarkGray
Write-Host (' MCPs:     ' + $Mcps)   -ForegroundColor DarkGray
if ($SessionName) {
    Write-Host (' Session:  ' + $SessionName) -ForegroundColor DarkGray
}
if ($Yolo) {
    Write-Host ' Yolo:     ON (--yolo — all permission prompts auto-approved)' -ForegroundColor Yellow
}
Write-Host (' Findings: ' + $findingsPath) -ForegroundColor DarkGray
Write-Host '------------------------------------------------------------' -ForegroundColor DarkGray
Write-Host ''

# Clear session-tagging env vars inherited from the parent copilot/agency
# process tree (pr-inbox-web → wt → pwsh → agency copilot). Defensive:
# stops the new agency from confusing itself with the parent session. Not
# strictly required for correctness today (agency generates its own fresh
# session ID per invocation), but keeps the child shell hygienic.
$inherited = @(
    'AGENCY_SESSION_ID', 'COPILOT_AGENT_SESSION_ID',
    'AGENCY_OPERATION_ID', 'AGENCY_LOG_SESSION_DIR', 'AGENCY_PLUGIN_DIR',
    'COPILOT_CUSTOM_INSTRUCTIONS_DIRS', 'COPILOT_LOADER_PID',
    'COPILOT_RUN_APP', 'COPILOT_CLI'
)
foreach ($name in $inherited) {
    Remove-Item -Path "env:$name" -ErrorAction SilentlyContinue
}

# Build the agency invocation.
#
# About session names: `agency copilot` *always* drives the underlying
# copilot CLI with `--resume <fresh-uuid>` — that's how agency manages
# its on-disk session directory at `.copilot/session-state/<uuid>/`.
# Copilot's own `--name` flag is mutually exclusive with `--resume`,
# so passing `--name` as a pass-through arg ends with:
#
#     error: option '-n, --name <name>' cannot be used with option
#            '--resume[=value]'
#
# There is no workaround inside the agency flow. The user-visible
# identifier for each review is the wt tab title (set by ReviewLauncher),
# not the copilot session name. SessionName is still accepted as a
# parameter and surfaced in the banner above for diagnostics, but it
# is intentionally NOT forwarded to agency.
$agencyArgs = @('copilot') + $mcpArgs + @(
    '--plugin', $Plugin,
    '--model',  $Model,
    '--agent',  $Agent
)

# Pass-through args go after `--`. -i keeps the session interactive but
# auto-executes the brief as the first turn (unlike -p which exits
# after completion — we DON'T want that, the user needs to follow up).
$passThrough = @()
if ($autoSend -or $Yolo) {
    $passThrough += '--'
    if ($autoSend) {
        $passThrough += '-i'
        $passThrough += $brief
    }
    if ($Yolo) {
        $passThrough += '--yolo'
    }
}

& agency @agencyArgs @passThrough

