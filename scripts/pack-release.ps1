param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/nupkg',
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projects = @(
    'src/Yatagarasu/Yatagarasu.csproj',
    'src/Yatagarasu.SourceGen/Yatagarasu.SourceGen.csproj',
    'src/Yatagarasu.Rag/Yatagarasu.Rag.csproj',
    'src/Yatagarasu.Hosting/Yatagarasu.Hosting.csproj',
    'src/Yatagarasu.OpenTelemetry/Yatagarasu.OpenTelemetry.csproj'
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
