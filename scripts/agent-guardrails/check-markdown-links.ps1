[CmdletBinding()]
param(
    [string[]]$Roots,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-MarkdownFindings {
    param([string[]]$Files)
    $findings = @()
    foreach ($file in $Files) {
        $fenced = $false
        $lines = @(Get-Content -LiteralPath $file -Encoding UTF8)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '^\s*(```|~~~)') { $fenced = -not $fenced; continue }
            if ($fenced) { continue }
            foreach ($match in [regex]::Matches($line, '(?<!!)\[[^\]]*\]\((?<target>[^)]+)\)')) {
                $target = $match.Groups['target'].Value.Trim()
                if ($target.StartsWith('<') -and $target.EndsWith('>')) { $target = $target.Trim('<', '>') }
                $target = ($target -split '\s+')[0]
                if ($target -match '^(#|[a-z][a-z0-9+.-]*:|/)') { continue }
                $path = (($target -split '[#?]')[0] -replace '/', [IO.Path]::DirectorySeparatorChar)
                if ([string]::IsNullOrEmpty($path)) { continue }
                $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $file) $path))
                if (-not (Test-Path -LiteralPath $resolved)) { $findings += [pscustomobject]@{ Path = $file; Line = $i + 1; Target = $target } }
            }
        }
    }
    return $findings
}

if ($SelfTest) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('quiver-links-' + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Path $root | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'ok.md') -Encoding UTF8 -Value '[ok](target.md)'
        Set-Content -LiteralPath (Join-Path $root 'target.md') -Encoding UTF8 -Value 'ok'
        Set-Content -LiteralPath (Join-Path $root 'bad.md') -Encoding UTF8 -Value '[bad](missing.md)'
        if (@(Get-MarkdownFindings @((Join-Path $root 'ok.md'))).Count -ne 0 -or @(Get-MarkdownFindings @((Join-Path $root 'bad.md'))).Count -ne 1) { throw 'self-test failed' }
    } finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host 'OK: markdown-link self-test passed.'
    exit 0
}

if (-not $Roots) { throw 'Specify -Roots <paths...>.' }
$files = @()
foreach ($root in $Roots) {
    if (Test-Path -LiteralPath $root -PathType Leaf) { $files += (Resolve-Path -LiteralPath $root).Path }
    elseif (Test-Path -LiteralPath $root -PathType Container) { $files += @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.md' | ForEach-Object FullName) }
}
$findings = @(Get-MarkdownFindings $files)
foreach ($finding in $findings) { Write-Host ("{0}:L{1}: missing {2}" -f $finding.Path, $finding.Line, $finding.Target) }
if ($findings.Count -gt 0) { exit 1 }
Write-Host 'OK: Markdown relative links resolve.'
