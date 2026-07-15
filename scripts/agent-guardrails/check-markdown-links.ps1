[CmdletBinding()]
param(
    [string[]]$Roots,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalRoots,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-MarkdownTarget {
    param(
        [string]$File,
        [int]$Line,
        [string]$Target,
        [System.Collections.Generic.List[object]]$Findings
    )

    $target = $Target.Trim()
    if ($target.StartsWith('<') -and $target.EndsWith('>')) { $target = $target.Trim('<', '>') }
    $target = ($target -split '\s+')[0]
    if ($target -match '^(#|[a-z][a-z0-9+.-]*:|/)') { return }
    $path = (($target -split '[#?]')[0] -replace '/', [IO.Path]::DirectorySeparatorChar)
    if ([string]::IsNullOrEmpty($path)) { return }
    $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $File) $path))
    if (-not (Test-Path -LiteralPath $resolved)) { $Findings.Add([pscustomobject]@{ Path = $File; Line = $Line; Target = $target }) }
}

function Get-MarkdownFindings {
    param([string[]]$Files)

    $findings = [System.Collections.Generic.List[object]]::new()
    foreach ($file in $Files) {
        $fenced = $false
        $inComment = $false
        $lines = @(Get-Content -LiteralPath $file -Encoding UTF8)
        $visibleLines = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '^\s*(```|~~~)') { $fenced = -not $fenced; $visibleLines += ''; continue }
            if ($fenced) { $visibleLines += ''; continue }

            $visible = $line
            while ($true) {
                if ($inComment) {
                    $commentEnd = $visible.IndexOf('-->')
                    if ($commentEnd -lt 0) { $visible = ''; break }
                    $visible = $visible.Substring($commentEnd + 3)
                    $inComment = $false
                    continue
                }
                $commentStart = $visible.IndexOf('<!--')
                if ($commentStart -lt 0) { break }
                $commentEnd = $visible.IndexOf('-->', $commentStart + 4)
                if ($commentEnd -lt 0) { $visible = $visible.Substring(0, $commentStart); $inComment = $true; break }
                $visible = $visible.Substring(0, $commentStart) + $visible.Substring($commentEnd + 3)
            }
            $visibleLines += [regex]::Replace($visible, '`[^`]*`', '')
        }

        $definitions = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
        for ($i = 0; $i -lt $visibleLines.Count; $i++) {
            $definition = [regex]::Match($visibleLines[$i], '^\s*\[(?<label>[^\]]+)\]:\s*(?<target><[^>]+>|\S+)')
            if ($definition.Success) { $definitions[$definition.Groups['label'].Value] = $definition.Groups['target'].Value }
        }

        for ($i = 0; $i -lt $visibleLines.Count; $i++) {
            $line = $visibleLines[$i]
            foreach ($match in [regex]::Matches($line, '!?\[[^\]]*\]\((?<target>[^)]+)\)')) {
                Test-MarkdownTarget $file ($i + 1) $match.Groups['target'].Value $findings
            }
            foreach ($match in [regex]::Matches($line, '!?\[(?<text>[^\]]+)\]\[(?<label>[^\]]*)\]')) {
                $label = $match.Groups['label'].Value
                if ([string]::IsNullOrWhiteSpace($label)) { $label = $match.Groups['text'].Value }
                if ($definitions.ContainsKey($label)) {
                    Test-MarkdownTarget $file ($i + 1) $definitions[$label] $findings
                } else {
                    $findings.Add([pscustomobject]@{ Path = $file; Line = $i + 1; Target = "reference '$label' (definition missing)" })
                }
            }
        }
    }
    return $findings.ToArray()
}

if ($SelfTest) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('quiver-links-' + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Path $root | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'ok.md') -Encoding UTF8 -Value @('[ok](target.md)', '![image](target.md)', '[reference][target]', '[target]: target.md', '`[ignored](missing.md)`', '<!-- [comment](missing.md) -->')
        Set-Content -LiteralPath (Join-Path $root 'target.md') -Encoding UTF8 -Value 'ok'
        Set-Content -LiteralPath (Join-Path $root 'bad.md') -Encoding UTF8 -Value @('![bad](missing-image.md)', '[bad][missing]', '[missing]: missing-reference.md')
        if (@(Get-MarkdownFindings @((Join-Path $root 'ok.md'))).Count -ne 0 -or @(Get-MarkdownFindings @((Join-Path $root 'bad.md'))).Count -ne 2) { throw 'self-test failed' }
    } finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host 'OK: markdown-link self-test passed.'
    exit 0
}

$Roots = @($Roots) + @($AdditionalRoots)
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
