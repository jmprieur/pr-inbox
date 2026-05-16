#requires -Version 7.0
<#
.SYNOPSIS
    pr-inbox review launcher.

.DESCRIPTION
    Run inside a new Windows Terminal tab spawned by pr-inbox-web.

    1. Reads brief.md from the run directory.
    2. Copies it to the clipboard.
    3. Launches the agency CLI with the dual-model-review agent.

    You paste the brief (Ctrl+V), review/edit if you want, and press
    Enter to send. The agent writes findings.yaml back into the run
    directory; pr-inbox-web watches for it.

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
#>

param(
    [Parameter(Mandatory = $true)] [string] $RunDirectory,
    [string] $Agent  = $env:PRINBOX_REVIEW_AGENT,
    [string] $Plugin = $env:PRINBOX_REVIEW_PLUGIN,
    [string] $Model  = $env:PRINBOX_REVIEW_MODEL,
    [string] $Mcps   = $env:PRINBOX_REVIEW_MCPS
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

$brief = Get-Content -Raw -Path $briefPath
try {
    Set-Clipboard -Value $brief
    $copied = $true
} catch {
    $copied = $false
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
if ($copied) {
    Write-Host (' Brief copied to clipboard (' + $brief.Length + ' chars).') -ForegroundColor Cyan
    Write-Host ' Ctrl+V into agency. Edit if needed. Press Enter to send.' -ForegroundColor Cyan
} else {
    Write-Host (' Brief at: ' + $briefPath) -ForegroundColor Yellow
    Write-Host ' (Clipboard copy failed -- open the file manually.)' -ForegroundColor Yellow
}
Write-Host (' Agent:    ' + $Agent)  -ForegroundColor DarkGray
Write-Host (' Plugin:   ' + $Plugin) -ForegroundColor DarkGray
Write-Host (' Model:    ' + $Model)  -ForegroundColor DarkGray
Write-Host (' MCPs:     ' + $Mcps)   -ForegroundColor DarkGray
Write-Host (' Findings: ' + $findingsPath) -ForegroundColor DarkGray
Write-Host '------------------------------------------------------------' -ForegroundColor DarkGray
Write-Host ''

& agency copilot @mcpArgs `
    --plugin $Plugin `
    --model $Model `
    --agent $Agent
