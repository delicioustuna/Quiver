[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AgentsRoot,
    [Parameter(Mandatory = $true)][string]$ClaudeRoot,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Directive = [regex]'^<!-- quiver-historical-skill-redirect: (?<target>[^ ]+) -->$'

function Get-RedirectFindings {
    param([string]$AgentRoot, [string]$ClaudeSkillRoot)
    $findings = @()
    foreach ($file in Get-ChildItem -LiteralPath $ClaudeSkillRoot -Recurse -File -Filter 'SKILL.md') {
        $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
        $first = if ($lines.Count -gt 0) { $lines[0] } else { '' }
        $match = $Directive.Match($first)
        $hasDirective = $match.Success
        if ($hasDirective -and $lines.Count -ne 1) { $findings += "$( $file.FullName ): redirect must be the only line"; continue }
        if (-not $hasDirective) { continue }
        $target = $match.Groups['target'].Value
        if ($target -notmatch '^[^\\:/]+(?:/[^\\:/]+)*$' -or $target -notmatch '^\.\./') { $findings += "$( $file.FullName ): redirect must be relative forward-slash path"; continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName ($target -replace '/', [IO.Path]::DirectorySeparatorChar)))
        $agentFull = [IO.Path]::GetFullPath($AgentRoot)
        if (-not $resolved.StartsWith($agentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $resolved.EndsWith('/SKILL.md'.Replace('/', [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $resolved -PathType Leaf)) { $findings += "$( $file.FullName ): target is not an existing AgentsRoot leaf SKILL.md" }
    }
    return $findings
}

if ($SelfTest) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('quiver-redirect-' + [guid]::NewGuid())
    try {
        $agents = Join-Path $root '.agents/skills/quiver-implement/comlpeted'
        $claude = Join-Path $root '.claude/skills/quiver-implement/comlpeted'
        New-Item -ItemType Directory -Force -Path $agents, $claude | Out-Null
        Set-Content -LiteralPath (Join-Path $agents 'SKILL.md') -Encoding UTF8 -Value 'historical body'
        Set-Content -LiteralPath (Join-Path $claude 'SKILL.md') -Encoding UTF8 -Value '<!-- quiver-historical-skill-redirect: ../../../../.agents/skills/quiver-implement/comlpeted/SKILL.md -->'
        $normalFindings = @(Get-RedirectFindings (Join-Path $root '.agents') (Join-Path $root '.claude'))
        if ($normalFindings.Count -ne 0) { throw ('normal self-test failed: ' + ($normalFindings -join '; ')) }
        Set-Content -LiteralPath (Join-Path $claude 'SKILL.md') -Encoding UTF8 -Value @('<!-- quiver-historical-skill-redirect: ../../../../.agents/skills/missing/SKILL.md -->', 'body')
        if (@(Get-RedirectFindings (Join-Path $root '.agents') (Join-Path $root '.claude')).Count -eq 0) { throw 'violation self-test failed' }
    } finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host 'OK: skill-redirect self-test passed.'
    exit 0
}

$findings = @(Get-RedirectFindings $AgentsRoot $ClaudeRoot)
foreach ($finding in $findings) { Write-Host $finding }
if ($findings.Count -gt 0) { exit 1 }
Write-Host 'OK: historical skill redirects resolve and are non-cyclic.'
