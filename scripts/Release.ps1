param(
    [Parameter(Mandatory = $true)][string]$SPTPath,
    [string]$Dotnet = 'dotnet',
    [string]$Tag = 'v1.0.6'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$commit = (& git -C $repository rev-parse HEAD).Trim()
$tagCommit = (& git -C $repository rev-list -n 1 $Tag).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -ne $tagCommit)
{
    throw 'HEAD must be the exact release tag.'
}

if (& git -C $repository status --porcelain --untracked-files=normal)
{
    throw 'Release requires a clean tracked source checkout.'
}

& (Join-Path $PSScriptRoot 'Build.ps1') -SPTPath $SPTPath -Dotnet $Dotnet
$version = $Tag.TrimStart('v')
$artifacts = Join-Path $repository 'artifacts'
$staging = Join-Path $artifacts "Infrastructure-Decay-$version"
$plugin = Join-Path $staging 'BepInEx/plugins/InfrastructureDecay'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
$binary = Join-Path $repository 'src/Client/bin/Release/netstandard2.1/InfrastructureDecay.Client.dll'
Copy-Item -LiteralPath $binary -Destination $plugin
Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $staging

$archive = Join-Path $artifacts "Infrastructure-Decay-$version.zip"
Compress-Archive -Path "$staging/*" -DestinationPath $archive -Force
$sourceArchive = Join-Path $artifacts "Infrastructure-Decay-$version-Source.zip"
& git -C $repository archive --format=zip "--output=$sourceArchive" $commit
if ($LASTEXITCODE -ne 0)
{
    throw 'Source archive failed.'
}

$provenance = [ordered]@{
    Repository = 'https://github.com/killdg/Infrastructure-Decay'
    Tag = $Tag
    Commit = $commit
    BuildConfiguration = 'Release'
    SPT = '4.1.6'
    EFT = '0.16.9.5.40743'
    Sdk = (& $Dotnet --version).Trim()
    BinarySHA256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    AutomatedVerification = '67 scheduling, pattern and native-contract checks'
    LatestGameplayVerification = 'Incomplete; experimental release. See docs/TESTING.md.'
    InstallerRoot = @('README.md', 'BepInEx')
}
$provenancePath = Join-Path $artifacts 'BUILD-PROVENANCE.json'
$provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenancePath
$releaseFiles = @($archive, $sourceArchive, $provenancePath, (Join-Path $artifacts 'verification.txt'))
$checksums = foreach ($file in $releaseFiles)
{
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))"
}
$checksums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt')
Write-Output $archive
