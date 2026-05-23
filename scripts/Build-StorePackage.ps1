<#
.SYNOPSIS
    Build a Microsoft Store-ready .msixupload package for SkimDown for Windows.

.DESCRIPTION
    Publishes the WinUI 3 app self-contained for each requested architecture,
    creates per-architecture .msix packages with `winapp package`, bundles them
    into a multi-architecture .msixbundle with `MakeAppx.exe bundle`, and finally
    wraps the bundle into a .msixupload zip that can be uploaded directly to
    Microsoft Partner Center.

    The output is intentionally unsigned by default. The Microsoft Store re-signs
    every package during ingestion, so production submission does not require a
    signing certificate. Pass -Sign to also produce a locally-installable signed
    bundle (a dev certificate that matches the manifest Publisher will be
    generated under bin/StorePackage/devcert.pfx if -CertPath is not provided).

    Trimming is forcibly disabled (-p:PublishTrimmed=false) for Store builds
    because WinUI 3 / CommunityToolkit.Mvvm reflection-heavy code paths are not
    fully trim-safe and can throw at runtime in trimmed builds. ReadyToRun is
    left enabled.

.PARAMETER Architectures
    Target architectures to build. Defaults to x64 + arm64 (the modern Store
    baseline). Accepted values: x64, arm64, x86. Case-insensitive.

.PARAMETER IncludeX86
    Shortcut that appends x86 to -Architectures. Equivalent to listing x86
    explicitly in -Architectures.

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release. Store submissions should
    always be Release.

.PARAMETER OutputDir
    Where to place the build layout, per-arch .msix files, the .msixbundle and
    the final .msixupload. Defaults to <repo>\bin\StorePackage.

.PARAMETER Sign
    Also produce a signed copy of the .msixbundle (for local install / WACK).
    Microsoft Store submission does not need this.

.PARAMETER CertPath
    Path to a .pfx whose Subject matches the manifest Publisher. If omitted
    when -Sign is set, a dev cert is auto-generated at <OutputDir>\devcert.pfx
    via `winapp cert generate --manifest`.

.PARAMETER CertPassword
    Password for the .pfx. Defaults to 'password' (matches `winapp cert generate`).

.PARAMETER SkipClean
    Skip removing OutputDir before building. Useful for incremental local
    iteration; never use for an actual Store submission build.

.EXAMPLE
    .\scripts\Build-StorePackage.ps1

    Builds an unsigned x64 + arm64 .msixupload ready for Partner Center upload.

.EXAMPLE
    .\scripts\Build-StorePackage.ps1 -IncludeX86

    Adds x86 to the bundle as well.

.EXAMPLE
    .\scripts\Build-StorePackage.ps1 -Sign

    Builds the upload package, plus a signed copy of the bundle (Add-AppxPackage
    compatible) for local validation.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'x86', IgnoreCase = $true)]
    [string[]] $Architectures = @('x64', 'arm64'),

    [switch]   $IncludeX86,

    [ValidateSet('Release', 'Debug')]
    [string]   $Configuration = 'Release',

    [string]   $OutputDir,

    [switch]   $Sign,

    [string]   $CertPath,

    [string]   $CertPassword = 'password',

    [switch]   $SkipClean
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\SkimDownForWindows'
$projectFile = Join-Path $projectDir 'SkimDownForWindows.csproj'
$manifestFile = Join-Path $projectDir 'Package.appxmanifest'

if (-not (Test-Path $projectFile))   { throw "Project file not found: $projectFile" }
if (-not (Test-Path $manifestFile))  { throw "Manifest not found: $manifestFile" }

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'bin\StorePackage'
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

# ---------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------
function Assert-Command {
    param([string] $Name, [string] $InstallHint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH. $InstallHint"
    }
}

Assert-Command -Name 'dotnet' -InstallHint "Install .NET 10 SDK."
Assert-Command -Name 'winapp' -InstallHint "Install via 'winget install Microsoft.WinAppCLI'."

# ---------------------------------------------------------------------------
# Architecture normalisation
# ---------------------------------------------------------------------------
$archTable = @{
    'x64'   = [pscustomobject]@{ Key = 'x64';   Platform = 'x64';   Rid = 'win-x64'   }
    'arm64' = [pscustomobject]@{ Key = 'arm64'; Platform = 'ARM64'; Rid = 'win-arm64' }
    'x86'   = [pscustomobject]@{ Key = 'x86';   Platform = 'x86';   Rid = 'win-x86'   }
}

$resolvedArchs = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($a in $Architectures) {
    $k = $a.ToLowerInvariant()
    if ($seen.Add($k)) { [void]$resolvedArchs.Add($archTable[$k]) }
}
if ($IncludeX86 -and $seen.Add('x86')) {
    [void]$resolvedArchs.Add($archTable['x86'])
}
if ($resolvedArchs.Count -eq 0) { throw 'No target architectures resolved.' }

# ---------------------------------------------------------------------------
# Manifest identity (Name + Version) for output filenames
# ---------------------------------------------------------------------------
[xml] $manifestXml = Get-Content -LiteralPath $manifestFile -Raw
$identity = $manifestXml.Package.Identity
if (-not $identity) { throw "Manifest at $manifestFile is missing <Identity>." }

$packageName    = $identity.Name
$packageVersion = $identity.Version
$packagePublisher = $identity.Publisher
if (-not $packageName -or -not $packageVersion -or -not $packagePublisher) {
    throw "Manifest Identity is missing Name/Version/Publisher."
}

Write-Host "==> SkimDown Store package build" -ForegroundColor Cyan
Write-Host "    Identity Name      : $packageName"
Write-Host "    Identity Publisher : $packagePublisher"
Write-Host "    Version            : $packageVersion"
Write-Host "    Architectures      : $((($resolvedArchs | ForEach-Object Key) -join ', '))"
Write-Host "    Output directory   : $OutputDir"

# ---------------------------------------------------------------------------
# Clean output
# ---------------------------------------------------------------------------
$layoutRoot = Join-Path $OutputDir 'layout'
$msixStaging = Join-Path $OutputDir 'msix'

if (-not $SkipClean -and (Test-Path $OutputDir)) {
    Write-Host "==> Cleaning $OutputDir (preserving screenshots/)" -ForegroundColor Cyan
    # Preserve the screenshots/ subdirectory across rebuilds — it holds the
    # Store-listing PNGs which are expensive to recapture and never recreated
    # by this script. Everything else under OutputDir is build output.
    Get-ChildItem -LiteralPath $OutputDir -Force |
        Where-Object { $_.Name -ne 'screenshots' } |
        Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir, $layoutRoot, $msixStaging | Out-Null

# ---------------------------------------------------------------------------
# Per-architecture: publish + winapp package
# ---------------------------------------------------------------------------
foreach ($arch in $resolvedArchs) {
    Write-Host ""
    Write-Host "==> [$($arch.Key)] dotnet publish ($Configuration, $($arch.Rid))" -ForegroundColor Cyan

    $publishDir = Join-Path $layoutRoot $arch.Key
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    # `dotnet publish` produces the self-contained layout (with AppxManifest.xml
    # token-replaced) under -o. We disable trimming for Store builds; ReadyToRun
    # stays on (kept from the csproj's Release defaults). GenerateAppxPackageOnBuild=false
    # ensures MSBuild does not try to invoke MakeAppx itself — `winapp package`
    # below owns that step.
    $dotnetArgs = @(
        'publish', $projectFile
        '-c', $Configuration
        '-p:Platform=' + $arch.Platform
        '-p:RuntimeIdentifier=' + $arch.Rid
        '-p:SelfContained=true'
        '-p:PublishTrimmed=false'
        '-p:GenerateAppxPackageOnBuild=false'
        '-p:AppxPackageSigningEnabled=false'
        '-o', $publishDir
        '--nologo'
        '-v', 'minimal'
    )
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($arch.Key) (exit $LASTEXITCODE)" }

    # Verify the published layout has the token-replaced manifest. If the
    # publish target dropped it (rare, but happens with some SDK combos), copy
    # it from the canonical bin\<P>\<C>\<TFM>\<RID>\AppxManifest.xml fallback.
    $publishedManifest = Join-Path $publishDir 'AppxManifest.xml'
    if (-not (Test-Path $publishedManifest)) {
        $tfmRoots = Get-ChildItem -LiteralPath (Join-Path $projectDir "bin\$($arch.Platform)\$Configuration") -Directory -ErrorAction SilentlyContinue
        $fallback = $null
        foreach ($tfm in $tfmRoots) {
            $candidate = Join-Path $tfm.FullName "$($arch.Rid)\AppxManifest.xml"
            if (Test-Path $candidate) { $fallback = $candidate; break }
        }
        if (-not $fallback) {
            throw "Published layout for $($arch.Key) is missing AppxManifest.xml and no build-output fallback was found under bin\$($arch.Platform)\$Configuration."
        }
        Write-Host "    AppxManifest.xml missing from publish dir; copying from $fallback" -ForegroundColor Yellow
        Copy-Item -LiteralPath $fallback -Destination $publishedManifest
    }

    # Sanity check: token replacement and arch tagging
    $manifestText = Get-Content -LiteralPath $publishedManifest -Raw
    if ($manifestText -match '\$targetnametoken\$|\$targetentrypoint\$') {
        throw "Published AppxManifest.xml still contains MSBuild tokens for $($arch.Key)."
    }
    if ($manifestText -notmatch 'ProcessorArchitecture\s*=\s*"' + [regex]::Escape($arch.Rid.Substring(4)) + '"') {
        throw "Published AppxManifest.xml for $($arch.Key) does not declare ProcessorArchitecture=`"$($arch.Rid.Substring(4))`"."
    }

    Write-Host "==> [$($arch.Key)] winapp package" -ForegroundColor Cyan
    $msixName = "${packageName}_${packageVersion}_$($arch.Key).msix"
    $msixOut  = Join-Path $msixStaging $msixName

    # `winapp package` without --cert produces an unsigned .msix. The Store
    # re-signs during ingestion, so unsigned is the correct artifact for upload.
    & winapp package $publishDir --output $msixOut --quiet
    if ($LASTEXITCODE -ne 0) { throw "winapp package failed for $($arch.Key) (exit $LASTEXITCODE)" }
    if (-not (Test-Path $msixOut)) { throw "Expected .msix not found: $msixOut" }

    $sizeMB = [math]::Round((Get-Item $msixOut).Length / 1MB, 2)
    Write-Host "    Produced $msixName ($sizeMB MB)"
}

# ---------------------------------------------------------------------------
# Bundle: makeappx bundle
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> Bundling architectures into .msixbundle" -ForegroundColor Cyan
$bundleName = "${packageName}_${packageVersion}.msixbundle"
$bundleOut  = Join-Path $OutputDir $bundleName

# `winapp tool makeappx` shells out to the Windows SDK MakeAppx.exe.
# /bv pins the bundle version to the manifest version; /o overwrites stale output.
& winapp tool makeappx bundle /d $msixStaging /p $bundleOut /bv $packageVersion /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx bundle failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $bundleOut)) { throw "Expected bundle not found: $bundleOut" }

$bundleSizeMB = [math]::Round((Get-Item $bundleOut).Length / 1MB, 2)
Write-Host "    Produced $bundleName ($bundleSizeMB MB)"

# ---------------------------------------------------------------------------
# Optional: sign the bundle for local install validation
# ---------------------------------------------------------------------------
if ($Sign) {
    Write-Host ""
    Write-Host "==> Signing bundle for local validation" -ForegroundColor Cyan
    if (-not $CertPath) {
        $CertPath = Join-Path $OutputDir 'devcert.pfx'
        if (-not (Test-Path $CertPath)) {
            Write-Host "    Generating dev certificate at $CertPath (matches manifest Publisher)"
            & winapp cert generate --manifest $manifestFile --output $CertPath --password $CertPassword --quiet
            if ($LASTEXITCODE -ne 0) { throw "winapp cert generate failed (exit $LASTEXITCODE)" }
        }
    }
    if (-not (Test-Path $CertPath)) { throw "Signing certificate not found: $CertPath" }

    & winapp sign $bundleOut $CertPath --password $CertPassword
    if ($LASTEXITCODE -ne 0) { throw "winapp sign failed (exit $LASTEXITCODE)" }
    Write-Host "    Bundle signed with $CertPath"
    Write-Host "    NOTE: trust the cert with 'winapp cert install $CertPath' (admin) before Add-AppxPackage." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Wrap into .msixupload (a zip containing the bundle at the root)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> Wrapping bundle into .msixupload" -ForegroundColor Cyan
$uploadName = "${packageName}_${packageVersion}.msixupload"
$uploadOut  = Join-Path $OutputDir $uploadName
if (Test-Path $uploadOut) { Remove-Item -LiteralPath $uploadOut -Force }

# .msixupload is just a zip containing the bundle (and optionally .appxsym
# symbol bundles, which we omit). Use System.IO.Compression directly instead
# of Compress-Archive — Compress-Archive emits noisy localized warnings on
# non-existent destinations and double-handles the file extension awkwardly.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($uploadOut, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip,
        $bundleOut,
        $bundleName,
        [System.IO.Compression.CompressionLevel]::Optimal)
} finally {
    $zip.Dispose()
}

$uploadSizeMB = [math]::Round((Get-Item $uploadOut).Length / 1MB, 2)
Write-Host "    Produced $uploadName ($uploadSizeMB MB)"

# Sanity-check the zip
try {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($uploadOut)
    try {
        $entries = $zip.Entries.FullName
        if ($entries -notcontains $bundleName) {
            throw "The .msixupload does not contain $bundleName at its root. Entries: $($entries -join ', ')"
        }
    } finally {
        $zip.Dispose()
    }
} catch {
    throw "Verification of $uploadOut failed: $_"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Bundle  : $bundleOut"
Write-Host "    Upload  : $uploadOut"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Sign in to https://partner.microsoft.com/dashboard/products/9NHTZMM0XMMF"
Write-Host "  2. Create a new submission and open the 'Packages' page."
Write-Host "  3. Drag-drop the .msixupload above onto the package upload area."
Write-Host "  4. Complete listings, ratings, screenshots, then submit for certification."
