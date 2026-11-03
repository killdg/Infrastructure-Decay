param(
    [Parameter(Mandatory = $true)][string]$SPTPath,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$gameRoot = (Resolve-Path -LiteralPath $SPTPath).Path
$clientProject = Join-Path $repository 'src/Client/InfrastructureDecay.Client.csproj'
$checksProject = Join-Path $repository 'Tests/Checks.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll')))
{
    throw 'SPTPath must point to the SPT game directory used for compilation references.'
}

& $Dotnet build $clientProject -c Release "-p:SPTPath=$gameRoot"
if ($LASTEXITCODE -ne 0)
{
    throw 'Client build failed.'
}

& $Dotnet build $checksProject -c Release "-p:SPTPath=$gameRoot"
if ($LASTEXITCODE -ne 0)
{
    throw 'Check harness build failed.'
}

$artifacts = Join-Path $repository 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$binary = Join-Path $repository 'src/Client/bin/Release/netstandard2.1/InfrastructureDecay.Client.dll'
$checks = Join-Path $repository 'Tests/bin/Release/net8.0/Checks.dll'
& $Dotnet $checks $gameRoot $binary | Tee-Object -FilePath (Join-Path $artifacts 'verification.txt')
if ($LASTEXITCODE -ne 0)
{
    throw 'Verification failed.'
}
