param(
    [Parameter(Mandatory = $true)][string]$SPTPath,
    [string]$Dotnet = 'dotnet',
    [string]$Tag = 'v1.0.6'
)

# Packaging never installs files or publishes. Build only from the exact tag.
& (Join-Path $PSScriptRoot 'scripts/Release.ps1') -SPTPath $SPTPath -Dotnet $Dotnet -Tag $Tag
