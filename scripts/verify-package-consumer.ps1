param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version = '0.8.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh consumer output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-consumer/Consumer.csproj') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-consumer/Program.cs') -Destination $output
[IO.File]::WriteAllText((Join-Path $output 'Directory.Build.props'), '<Project />')
[IO.File]::WriteAllText((Join-Path $output 'Directory.Build.targets'), '<Project />')
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$feed = [Security.SecurityElement]::Escape($packages)
$cachedFeed = [Security.SecurityElement]::Escape($cache)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'), @"
<configuration><packageSources><clear /><add key="release" value="$feed" /><add key="cached-dependencies" value="$cachedFeed" /></packageSources></configuration>
"@)
$project = Join-Path $output 'Consumer.csproj'
dotnet restore $project --configfile (Join-Path $output 'NuGet.Config') --packages (Join-Path $output 'packages') "-p:PackageVersion=$Version" -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Consumer restore failed.' }
dotnet run --project $project -c Release --no-restore "-p:PackageVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Consumer build/run failed.' }
foreach ($id in @('Yatagarasu','Yatagarasu.Rag','Yatagarasu.Hosting','Yatagarasu.OpenTelemetry')) {
    $package = "$id.$Version.nupkg"
    $original = (Get-FileHash -LiteralPath (Join-Path $packages $package)).Hash
    $restored = (Get-FileHash -LiteralPath (Join-Path $output "packages/$($id.ToLowerInvariant())/$Version/$($package.ToLowerInvariant())")).Hash
    if ($original -ne $restored) { throw "Consumer used a different package: $id" }
}
Write-Output 'All four restored package hashes match the release output.'
