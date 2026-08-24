param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src/Yatagarasu/Yatagarasu.csproj"
$outputDirectory = Join-Path $repositoryRoot ".zero-dependency-gate"

# 第 1 ゲート: コアプロジェクト自身に PackageReference がないことを確認する。
[xml]$project = Get-Content -Raw -Encoding UTF8 -LiteralPath $projectPath
$packageReferences = @($project.SelectNodes("/Project/ItemGroup/PackageReference"))
if ($packageReferences.Count -ne 0) {
    $names = $packageReferences | ForEach-Object { $_.Include }
    throw "Yatagarasu.csproj contains PackageReference items: $($names -join ', ')"
}

if (Test-Path -LiteralPath $outputDirectory) {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDirectory | Out-Null

try {
    dotnet pack $projectPath --configuration $Configuration --no-restore --output $outputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed with exit code $LASTEXITCODE."
    }

    $packages = @(Get-ChildItem -LiteralPath $outputDirectory -Filter "Yatagarasu.*.nupkg" |
        Where-Object { $_.Name -notlike "*.symbols.nupkg" })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one Yatagarasu nupkg, found $($packages.Count)."
    }

    # 第 2 ゲート: SDK/props/ProjectReference の影響を含む最終 nuspec を検証する。
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $nuspecEntry = $archive.Entries |
            Where-Object { $_.FullName.EndsWith(".nuspec", [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "The nupkg does not contain a nuspec."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $dependencies = @($nuspec.SelectNodes(
            "//*[local-name()='dependencies']/*[local-name()='dependency'] | " +
            "//*[local-name()='dependencies']/*[local-name()='group']/*[local-name()='dependency']"))
        if ($dependencies.Count -ne 0) {
            $names = $dependencies | ForEach-Object { $_.id }
            throw "Generated nuspec contains dependencies: $($names -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Zero-dependency gate passed: csproj PackageReference=0, nuspec dependency=0."
}
finally {
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
}
