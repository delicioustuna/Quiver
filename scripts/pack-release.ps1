param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/nupkg',
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projects = @(
    'src/Quiver/Quiver.csproj',
    'src/Quiver.Rag/Quiver.Rag.csproj',
    'src/Quiver.Hosting/Quiver.Hosting.csproj',
    'src/Quiver.OpenTelemetry/Quiver.OpenTelemetry.csproj'
)

foreach ($project in $projects) {
    $arguments = @(
        'pack',
        $project,
        '--configuration', $Configuration,
        '--no-restore',
        '--output', $OutputDirectory
    )
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $arguments += "-p:Version=$Version"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $project with exit code $LASTEXITCODE"
    }
}
