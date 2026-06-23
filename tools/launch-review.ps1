#requires -Version 7.0
<#
.SYNOPSIS
    pr-inbox review launcher.

.DESCRIPTION
    Run inside a new Windows Terminal tab spawned by pr-inbox-web.

    By default (-AutoSend, on) a tiny bootstrap message is passed to
    copilot via the pass-through `-i "<message>"` flag — "Read brief.md
    and proceed." — and the agent reads brief.md from its current
    directory itself. The terminal stays open after the agent finishes
    so you can follow up. (`-i` keeps the session interactive; `-p`
    would exit on completion, which we explicitly don't want.)

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

.PARAMETER LaunchCommand
    The full command that runs the review, with {plugin} / {model} /
    {agent} placeholders. Defaults to the public GitHub Copilot CLI:
    `copilot --plugin {plugin} --model {model} --agent {agent}`.
    Microsoft users set PRINBOX_REVIEW_COMMAND (or the Settings field)
    to e.g. `agency copilot --mcp workiq --mcp teams --plugin {plugin}
    --model {model} --agent {agent}`.

.PARAMETER Agent
    Value substituted into the {agent} placeholder. Defaults to
    dual-review:dual-model-review.

.PARAMETER Plugin
    Value substituted into the {plugin} placeholder. Defaults to
    market:dual-review@jmprieur/pr-inbox.

.PARAMETER Model
    Value substituted into the {model} placeholder. Defaults to
    claude-opus-4.8.

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
    [string] $LaunchCommand = $env:PRINBOX_REVIEW_COMMAND,
    [string] $SessionName = '',
    [switch] $NoAutoSend,
    [switch] $Yolo
)

if (-not $Agent)  { $Agent  = 'dual-review:dual-model-review' }
if (-not $Plugin) { $Plugin = 'market:dual-review@jmprieur/pr-inbox' }
if (-not $Model)  { $Model  = 'claude-opus-4.8' }
# Default launch command targets the public GitHub Copilot CLI. Microsoft
# users set PRINBOX_REVIEW_COMMAND (or the Settings field) to e.g.
# 'agency copilot --mcp workiq --mcp teams --plugin {plugin} --model {model} --agent {agent}'.
if (-not $LaunchCommand) {
    $LaunchCommand = 'copilot --plugin {plugin} --model {model} --agent {agent}'
}

# Resolve placeholders. Idempotent: when the web launcher already substituted
# them, no placeholders remain and these replacements are no-ops.
$resolvedCommand = $LaunchCommand.
    Replace('{plugin}', $Plugin).
    Replace('{model}',  $Model).
    Replace('{agent}',  $Agent)

$ErrorActionPreference = 'Stop'

$briefPath    = Join-Path $RunDirectory 'brief.md'
$findingsPath = Join-Path $RunDirectory 'findings.yaml'

if (-not (Test-Path $briefPath)) {
    Write-Host "brief.md not found at $briefPath" -ForegroundColor Red
    exit 1
}

$brief    = Get-Content -Raw -Path $briefPath
$autoSend = -not $NoAutoSend

# Only copy-to-clipboard when we're NOT auto-sending; the user needs the
# clipboard for the manual Ctrl+V fallback. When auto-sending we hand the
# agent a tiny "Read brief.md and proceed." bootstrap and let it read the
# brief off disk itself — keeps the command line short and lets the
# brief stay editable up until the agent reads it.
$copied = $false
if (-not $autoSend) {
    try {
        Set-Clipboard -Value $brief
        $copied = $true
    } catch {
        $copied = $false
    }
}

Write-Host ''
Write-Host '------------------------------------------------------------' -ForegroundColor DarkGray
if ($autoSend) {
    Write-Host (' Brief auto-send ON (agent reads brief.md from disk; ' + $brief.Length + ' chars on disk).') -ForegroundColor Green
} else {
    if ($copied) {
        Write-Host (' Brief copied to clipboard (' + $brief.Length + ' chars).') -ForegroundColor Cyan
        Write-Host ' Ctrl+V into the review CLI. Edit if needed. Press Enter to send.' -ForegroundColor Cyan
    } else {
        Write-Host (' Brief at: ' + $briefPath) -ForegroundColor Yellow
        Write-Host ' (Clipboard copy failed -- open the file manually.)' -ForegroundColor Yellow
    }
}
Write-Host (' Command:  ' + $resolvedCommand) -ForegroundColor DarkGray
Write-Host (' Agent:    ' + $Agent)  -ForegroundColor DarkGray
Write-Host (' Plugin:   ' + $Plugin) -ForegroundColor DarkGray
Write-Host (' Model:    ' + $Model)  -ForegroundColor DarkGray
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

# Build the CLI invocation.
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
# is intentionally NOT forwarded.

# Tokenize the resolved launch command and invoke it. Pass-through flags
# (-i bootstrap, --yolo) are appended after.
$cmdTokens = @($resolvedCommand -split '\s+' | Where-Object { $_ })
$cmdExe    = $cmdTokens[0]
$cmdArgs   = if ($cmdTokens.Count -gt 1) { $cmdTokens[1..($cmdTokens.Count - 1)] } else { @() }

# Forward unknown flags (-i, --yolo) straight to copilot. Empirical:
# agency's clap config treats unknown flags as pass-through to the
# engine. Importantly, do NOT use the `--` separator: when present,
# agency includes it in the args it spawns copilot with and copilot
# parses everything past `--` as positional, which it doesn't accept
# (error: too many arguments, expected 0, got N).
#
# -i (not -p) keeps the session interactive but auto-executes the
# bootstrap message as the first turn. The agent's cwd is the run
# directory (set by wt's `-d "<runDir>"` on the parent terminal), so
# `brief.md` is a bare-filename Read away.
$passThrough = @()
if ($autoSend) {
    $passThrough += '-i'
    $passThrough += 'Read brief.md and proceed.'
}
if ($Yolo) {
    $passThrough += '--yolo'
}

& $cmdExe @cmdArgs @passThrough

