<#
.SYNOPSIS
    Builds a Deckard release: an installer (Setup.exe) and the update files the Deckard server hands to installed copies.

.DESCRIPTION
    Publishes the app self-contained with the server address built in, then packs it with Velopack (vpk, a .NET tool:
    dotnet tool install -g vpk). Players run Setup.exe once: it installs per user, with no admin rights, into
    %LOCALAPPDATA%\DeckardApp (Deckard's own data stays in %LOCALAPPDATA%\Deckard) and adds a Start menu shortcut.
    Afterwards Deckard updates itself from the server: copy everything in the output folder except Setup.exe into the
    server's Trade:UpdatesRoot folder. Each release keeps the earlier packages, so vpk can make small delta updates.

    Updates run code on every installed copy, so the server must be https (a localhost one is allowed for testing),
    and a release must be signed unless you pass -Unsigned on purpose: -AzureSignFile signs with Azure Artifact Signing
    (Synica's certificate, signed in with az login and the Artifact Signing Certificate Profile Signer role), or
    -SignParams with signtool and a certificate of your own. Unsigned, Windows SmartScreen warns players before the
    first run, and nothing but the server vouches for an update.

.EXAMPLE
    ./tools/installer/pack.ps1 -Version 1.0.0 -Server https://deckard.example.net -AzureSignFile C:\path\artifact-signing.json
.EXAMPLE
    ./tools/installer/pack.ps1 -Version 0.2.0 -Server https://deckard.example.net -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
.EXAMPLE
    ./tools/installer/pack.ps1 -Version 0.1.0 -Server http://localhost:5180 -Unsigned   # a local test release
#>
param(
    [Parameter(Mandatory)][string]$Version,          # semantic version, e.g. 0.2.0; must grow with every release
    [Parameter(Mandatory)][string]$Server,           # the Deckard server the release talks to, e.g. https://deckard.example.net
    [string]$Out,                                    # where Setup.exe and the update files go (kept between releases), relative to the repo or absolute;
                                                     # dist/releases, or dist/releases-<channel>
    [string]$Channel,                                # "early": a release for patrons with early access only
    [string]$SignParams,                             # signtool parameters, e.g. '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
    [string]$AzureSignFile,                          # Artifact Signing metadata.json: Endpoint, CodeSigningAccountName, CertificateProfileName
    [switch]$Unsigned                                # build without signing anyway (testing, or until there's a certificate)
)
$ErrorActionPreference = "Stop"
if ($Channel -and $Channel -ne "early") {
    throw "The only other channel is early (the server serves /updates/early/ to patrons with early access)."
}
if (-not $Out) { $Out = if ($Channel) { "dist/releases-$Channel" } else { "dist/releases" } }
$root = Resolve-Path "$PSScriptRoot/../.."
$Out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out } # e.g. the folder the last release was packed in
$publish = Join-Path $root "dist/publish"

$serverUri = [Uri]$Server
if ($serverUri.Scheme -ne "https" -and -not $serverUri.IsLoopback) {
    throw "The server must be https ($Server isn't): installed copies take updates and send their session to it."
}
if (-not $SignParams -and -not $AzureSignFile -and -not $Unsigned) {
    throw "Pass -AzureSignFile or -SignParams to sign the release, or -Unsigned to build it unsigned on purpose."
}
if ($SignParams -and $AzureSignFile) {
    throw "Pass one of -AzureSignFile and -SignParams, not both."
}
if ($Unsigned) {
    Write-Warning "Building an UNSIGNED release: SmartScreen will warn players, and only the server vouches for updates."
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk isn't installed: dotnet tool install -g vpk"
}

Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root "src/Deckard.App") -c Release -r win-x64 --self-contained `
    -p:Version=$Version -p:DeckardServer=$Server -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$pack = @("pack", "--packId", "DeckardApp", "--packVersion", $Version, "--packDir", $publish, "--mainExe", "Deckard.exe",
    "--packTitle", "Deckard", "--packAuthors", "Deckard", "--icon", (Join-Path $root "src/Deckard.App/Assets/Deckard.ico"),
    "--outputDir", $Out)
if ($SignParams) { $pack += @("--signParams", $SignParams) }
if ($AzureSignFile) { $pack += @("--azureTrustedSignFile", (Resolve-Path $AzureSignFile)) }
if ($Channel) { $pack += @("--channel", $Channel) }
vpk @pack
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if ($Channel) {
    Write-Host "Early release $Version in $Out. Copy all but Setup.exe to the early folder in the server's Trade:UpdatesRoot."
} else {
    Write-Host "Release $Version in $Out. Copy all but Setup.exe to the server's Trade:UpdatesRoot; give players Setup.exe."
}
