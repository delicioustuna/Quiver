[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AgentsRoot,
    [Parameter(Mandatory = $true)][string]$ClaudeRoot,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Directive = [regex]'^<!-- quiver-historical-skill-redirect: (?<target>[^ ]+) -->$'

if ($null -eq ('QuiverFinalPathNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class QuiverFinalPathNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string name,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint capacity,
        uint flags);
}
'@
}

function Get-FinalPath {
    param([string]$Path, [switch]$Directory)

    $backupSemantics = if ($Directory) { [uint32]0x02000000 } else { [uint32]0 }
    $handle = [QuiverFinalPathNative]::CreateFile($Path, [uint32]0x80, [uint32]0x7, [IntPtr]::Zero, [uint32]3, $backupSemantics, [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $handle.Dispose()
        throw "CreateFile failed for $Path (Win32 error $errorCode)"
    }

    try {
        [uint32]$capacity = 512
        while ($true) {
            $buffer = New-Object System.Text.StringBuilder ([int]$capacity)
            [uint32]$length = [QuiverFinalPathNative]::GetFinalPathNameByHandle($handle, $buffer, $capacity, [uint32]0)
            if ($length -eq 0) {
                $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                throw "GetFinalPathNameByHandle failed for $Path (Win32 error $errorCode)"
            }
            if ($length -lt $capacity) { break }
            $capacity = $length + 1
        }
        $final = $buffer.ToString()
        if ($final.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { return '\\' + $final.Substring(8) }
        if ($final.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) { return $final.Substring(4) }
        return $final
    } finally {
        $handle.Dispose()
    }
}

function Get-RedirectFindings {
    param([string]$AgentRoot, [string]$ClaudeSkillRoot)

    $findings = @()
    $agentReal = Get-FinalPath $AgentRoot -Directory
    $agentPrefix = $agentReal.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $ClaudeSkillRoot -Recurse -File -Filter 'SKILL.md') {
        $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
        $first = if ($lines.Count -gt 0) { $lines[0] } else { '' }
        $match = $Directive.Match($first)
        if ($match.Success -and $lines.Count -ne 1) { $findings += "$( $file.FullName ): redirect must be the only line"; continue }
        if (-not $match.Success) { continue }

        $target = $match.Groups['target'].Value
        if ($target -notmatch '^[^\\:/]+(?:/[^\\:/]+)*$' -or $target -notmatch '^\.\./') { $findings += "$( $file.FullName ): redirect must be relative forward-slash path"; continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName ($target -replace '/', [IO.Path]::DirectorySeparatorChar)))
        $agentFull = [IO.Path]::GetFullPath($AgentRoot)
        if (-not $resolved.StartsWith($agentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Split-Path -Leaf $resolved).Equals('SKILL.md', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $resolved -PathType Leaf)) { $findings += "$( $file.FullName ): target is not an existing AgentsRoot leaf SKILL.md"; continue }

        # Use a handle-based final path. Resolve-Path keeps junction and symlink spelling.
        $targetReal = Get-FinalPath $resolved
        if (-not $targetReal.StartsWith($agentPrefix, [StringComparison]::OrdinalIgnoreCase)) { $findings += "$( $file.FullName ): target escapes AgentsRoot after resolving reparse points"; continue }

        # The target must be the canonical body. This also forbids multi-hop redirects and cycles.
        $targetFirst = @(Get-Content -LiteralPath $targetReal -Encoding UTF8 -TotalCount 1)[0]
        if ($Directive.IsMatch($targetFirst)) { $findings += "$( $file.FullName ): multi-hop redirect is forbidden" }
    }
    return $findings
}

if ($SelfTest) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('quiver-redirect-' + [guid]::NewGuid())
    try {
        $agents = Join-Path $root '.agents/skills/quiver-implement/comlpeted'
        $claude = Join-Path $root '.claude/skills/quiver-implement/comlpeted'
        $ordinary = Join-Path $root '.claude/skills/ordinary'
        $outside = Join-Path $root 'outside'
        New-Item -ItemType Directory -Force -Path $agents, $claude, $ordinary, $outside | Out-Null
        Set-Content -LiteralPath (Join-Path $agents 'SKILL.md') -Encoding UTF8 -Value 'historical body'
        Set-Content -LiteralPath (Join-Path $claude 'SKILL.md') -Encoding UTF8 -Value '<!-- quiver-historical-skill-redirect: ../../../../.agents/skills/quiver-implement/comlpeted/SKILL.md -->'
        Set-Content -LiteralPath (Join-Path $ordinary 'SKILL.md') -Encoding UTF8 -Value @('---', 'name: ordinary', '---', '', '# ordinary skill')
        if (@(Get-RedirectFindings (Join-Path $root '.agents') (Join-Path $root '.claude')).Count -ne 0) { throw 'normal self-test failed' }

        Set-Content -LiteralPath (Join-Path $agents 'SKILL.md') -Encoding UTF8 -Value '<!-- quiver-historical-skill-redirect: ../other/SKILL.md -->'
        if (@(Get-RedirectFindings (Join-Path $root '.agents') (Join-Path $root '.claude')).Count -eq 0) { throw 'multi-hop self-test failed' }

        Set-Content -LiteralPath (Join-Path $agents 'SKILL.md') -Encoding UTF8 -Value 'historical body'
        Set-Content -LiteralPath (Join-Path $outside 'SKILL.md') -Encoding UTF8 -Value 'outside body'
        $escaped = Join-Path $root '.agents/skills/escaped'
        try {
            New-Item -ItemType Junction -Path $escaped -Target $outside -ErrorAction Stop | Out-Null
        } catch {
            throw "junction self-test setup failed: $($_.Exception.Message)"
        }
        if (-not ((Get-Item -LiteralPath $escaped -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'junction self-test did not create a reparse point' }
        Set-Content -LiteralPath (Join-Path $claude 'SKILL.md') -Encoding UTF8 -Value '<!-- quiver-historical-skill-redirect: ../../../../.agents/skills/escaped/SKILL.md -->'
        if (@(Get-RedirectFindings (Join-Path $root '.agents') (Join-Path $root '.claude')).Count -eq 0) { throw 'physical escape self-test failed' }
    } finally {
        if ($escaped -and (Test-Path -LiteralPath $escaped)) {
            [IO.Directory]::Delete($escaped, $false)
            if (Test-Path -LiteralPath $escaped) { throw 'junction self-test cleanup failed' }
        }
        if (Test-Path -LiteralPath $root) {
            [IO.Directory]::Delete($root, $true)
            if (Test-Path -LiteralPath $root) { throw 'self-test fixture cleanup failed' }
        }
    }
    Write-Host 'OK: skill-redirect self-test passed.'
    exit 0
}

$findings = @(Get-RedirectFindings $AgentsRoot $ClaudeRoot)
foreach ($finding in $findings) { Write-Host $finding }
if ($findings.Count -gt 0) { exit 1 }
Write-Host 'OK: historical skill redirects resolve and are non-cyclic.'
