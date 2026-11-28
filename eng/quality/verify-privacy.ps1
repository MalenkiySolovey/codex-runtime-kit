param(
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourceRoot = Join-Path $repoRoot 'src\Codex.RuntimeKit'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj' })

foreach ($file in $sourceFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($content -match 'CodexSwitcher\.(Core|Infra|App)|CodexSwitchboard|ISwitchboardCodexProcessRegistry') {
        throw "RuntimeKit source depends on a product-specific Control Center contract: $($file.FullName)"
    }
    if ($content -match 'TotpSecret|CredentialVault|ApiKeyStore' -or
        ($content -match 'auth\.json' -and $file.FullName -cne (Join-Path $sourceRoot 'Configuration/CodexRuntimeAuthFile.cs'))) {
        throw "RuntimeKit source contains a credential-storage dependency: $($file.FullName)"
    }
    if ($content -match '(?i)sk-[A-Za-z0-9]{20,}|eyJhbGciOi[A-Za-z0-9_-]{10,}\.') {
        throw "RuntimeKit source contains a credential-shaped literal: $($file.FullName)"
    }
}

$projectFile = Join-Path $sourceRoot 'Codex.RuntimeKit.csproj'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$projectReferences = $project.SelectNodes('/Project/ItemGroup/ProjectReference')
$packageReferences = $project.SelectNodes('/Project/ItemGroup/PackageReference')
if ($projectReferences.Count -gt 0) {
    throw 'The standalone library must not contain project references.'
}
$centralPackages = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
$approvedParser = $centralPackages.SelectNodes("/Project/ItemGroup/PackageVersion[@Include='Esprima' and @Version='3.0.6']")
if ($packageReferences.Count -ne 1 -or $packageReferences[0].Include -cne 'Esprima' -or $approvedParser.Count -ne 1) {
    throw 'The standalone library permits only the pinned Esprima parser for the Desktop usage recipe.'
}

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Package does not exist: $PackagePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(^|/)(tests?|auth\.json|appsettings\.json)(/|$)') {
                throw "Package contains a test or credential-bearing file: $($entry.FullName)"
            }
            if ($entry.Length -gt 0 -and $entry.Length -lt 5MB -and $entry.FullName -match '\.(dll|xml|md|json|txt)$') {
                $reader = [System.IO.StreamReader]::new($entry.Open())
                try { $content = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
                if ($content -match '(?i)sk-[A-Za-z0-9]{20,}|eyJhbGciOi[A-Za-z0-9_-]{10,}\.') {
                    throw "Package contains a credential-shaped literal: $($entry.FullName)"
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host 'PRIVACY_SCAN=PASS'
