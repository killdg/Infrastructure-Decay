# Build and release

Use a local SPT 4.1.6 installation and .NET 10 SDK (including the .NET 8 runtime for the check executable). Proprietary game/BepInEx DLLs are referenced locally and are not included in source archives. No external NuGet package is added by the project; normal SDK restore may consult configured NuGet sources for framework references.

```powershell
./scripts/Build.ps1 -SPTPath 'B:/SPT MOD TESTING'
```

To select an SDK executable explicitly:

```powershell
./scripts/Build.ps1 -SPTPath 'B:/SPT MOD TESTING' -Dotnet 'C:/path/to/dotnet.exe'
```

Formatting uses .editorconfig: four spaces, Allman braces and one statement per line. Source review also checks descriptive identifiers, wrapped calls and explanations of native identifiers.

```powershell
dotnet format whitespace . --folder --include src Tests --verify-no-changes
```

For a release, commit all reviewed source and documentation, tag that commit, and run:

```powershell
./scripts/Release.ps1 -SPTPath 'B:/SPT MOD TESTING' -Tag v1.0.6
```

The script requires HEAD to match the tag and tracked/untracked source to be clean. Ignored bin/obj/artifacts outputs are permitted. It rebuilds, runs all 67 checks, then creates an installer ZIP with only README.md and BepInEx at its root, a Git archive of the exact commit, verification output, checksums and provenance in artifacts/. It does not install or upload anything.

The maintainer publishes these assets to GitHub and downloads them again for checksum verification. The provenance file records the exact source commit and binary hash. No obfuscation or post-build rewriting is performed.
