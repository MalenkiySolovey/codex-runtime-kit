param(
    [switch]$SkipPackaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repoRoot 'Codex.RuntimeKit.slnx'
$packageProject = Join-Path $repoRoot 'src\Codex.RuntimeKit\Codex.RuntimeKit.csproj'
$privacyScript = Join-Path $PSScriptRoot 'verify-privacy.ps1'

Push-Location $repoRoot
try {
    $expectedSdk = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $expectedSdk) {
        throw "RuntimeKit requires .NET SDK $expectedSdk; found '$actualSdk'."
    }

    & dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    & dotnet format $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Format verification failed.' }

    & dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    & dotnet test $solution --configuration Release --no-build --no-restore --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'RuntimeKit tests failed.' }

    $auditOutput = & dotnet list $solution package --vulnerable --include-transitive --no-restore 2>&1
    $auditExitCode = $LASTEXITCODE
    $auditOutput | Write-Output
    if ($auditExitCode -ne 0) { throw 'NuGet vulnerability audit failed.' }
    if (($auditOutput | Out-String) -match 'has the following vulnerable packages') {
        throw 'NuGet vulnerability audit found vulnerable dependencies.'
    }

    & pwsh -NoProfile -File $privacyScript
    if ($LASTEXITCODE -ne 0) { throw 'Privacy verification failed.' }

    if (-not $SkipPackaging) {
        $packageVersion = ([xml](Get-Content -LiteralPath $packageProject -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
        $packageRoot = Join-Path $repoRoot 'artifacts\verification-packages'
        $firstFeed = Join-Path $packageRoot 'first'
        $secondFeed = Join-Path $packageRoot 'second'
        $consumerRoot = Join-Path $repoRoot 'artifacts\package-consumer'
        New-Item -ItemType Directory -Path $firstFeed, $secondFeed, $consumerRoot -Force | Out-Null

        foreach ($feed in @($firstFeed, $secondFeed)) {
            & dotnet pack $packageProject --configuration Release --no-build --no-restore --output $feed `
                "-p:PackageVersion=$packageVersion" `
                '-p:ContinuousIntegrationBuild=true' `
                '-p:Deterministic=true' `
                '-p:DeterministicTimestamp=2020-01-01T00:00:00Z'
            if ($LASTEXITCODE -ne 0) { throw "Package creation failed for $feed." }
        }

        $firstPackage = Join-Path $firstFeed "Codex.RuntimeKit.$packageVersion.nupkg"
        $secondPackage = Join-Path $secondFeed "Codex.RuntimeKit.$packageVersion.nupkg"
        $firstHash = (Get-FileHash -LiteralPath $firstPackage -Algorithm SHA256).Hash
        $secondHash = (Get-FileHash -LiteralPath $secondPackage -Algorithm SHA256).Hash
        if ($firstHash -cne $secondHash) {
            throw "Package output is not reproducible: $firstHash != $secondHash"
        }

        & pwsh -NoProfile -File $privacyScript -PackagePath $firstPackage
        if ($LASTEXITCODE -ne 0) { throw 'Packed package privacy verification failed.' }

        $consumerProject = Join-Path $consumerRoot 'PackageConsumer.csproj'
        $consumerProgram = Join-Path $consumerRoot 'Program.cs'
        @'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $consumerRoot 'Directory.Packages.props') -Encoding utf8
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Codex.RuntimeKit" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $consumerProject -Encoding utf8
        @'
using Codex.RuntimeKit.AppServer;
using Codex.RuntimeKit.Discovery;

var resolver = new CodexRuntimeResolver();
var validation = resolver.ValidateExecutable("synthetic-candidate");
var clientOptions = new AppServerClientOptions { ExecutablePath = "codex", CodexHome = "isolated-home" };
Console.WriteLine($"{validation.IsValid}:{clientOptions.ClientInfo.Name}");
'@ | Set-Content -LiteralPath $consumerProgram -Encoding utf8

        & dotnet restore $consumerProject --source $firstFeed
        if ($LASTEXITCODE -ne 0) { throw 'Standalone package consumer restore failed.' }
        & dotnet build $consumerProject --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Standalone package consumer build failed.' }

        Write-Host "PACKAGE_SHA256=$firstHash"
        Write-Host "PACKAGE_VERSION=$packageVersion"
    }

    Write-Host 'RUNTIME_KIT_QUALITY=PASS'
}
finally {
    Pop-Location
}
