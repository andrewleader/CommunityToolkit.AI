<#
.SYNOPSIS
  Builds a complete, signed local copy of the site (runtime manifest, runtime zip, catalog) with throwaway dev keys,
  then prints the environment variables that point a dev-mode client at it.

.EXAMPLE
  ./tools/dev-site.ps1
  $env:LOCALAI_DEV_ROOT_URL = ...   # printed by the script
  dotnet run --project samples/ConsoleChat -- --dev --yes --prompt "Hello"
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts\dev-site'),
    [string]$Channel = 'local',
    [string]$Version = '0.1.0-local',
    [string]$Configuration = 'Debug',
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [switch]$Serve,
    [int]$Port = 8787
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$Out = [System.IO.Path]::GetFullPath($Out)
$site = Join-Path $Out 'site'
$keys = Join-Path $Out 'keys'
$work = Join-Path $Out 'work'
New-Item -ItemType Directory -Force $site, $work | Out-Null

function Tools { dotnet run --project (Join-Path $repo 'tools\LocalAI.Tools') -c $Configuration --no-build -- @args; if ($LASTEXITCODE) { throw "localai-tools $($args[0]) failed" } }

dotnet build (Join-Path $repo 'src\Broker') -c $Configuration --nologo -v q
dotnet build (Join-Path $repo 'src\Host') -c $Configuration --nologo -v q
dotnet build (Join-Path $repo 'tools\LocalAI.Tools') -c $Configuration --nologo -v q
if ($LASTEXITCODE) { throw 'Build failed' }

if (-not (Test-Path (Join-Path $keys 'root.pub'))) { Tools keygen --out $keys --valid-days 30 }

$arch = $Rid.Split('-')[-1]
$zip = Join-Path $site "runtime\$Version\runtime-win-$arch.zip"
Tools pack-runtime --broker (Join-Path $repo "src\Broker\bin\$Configuration\net10.0") --host (Join-Path $repo "src\Host\bin\$Configuration\net10.0") --version $Version --rid $Rid --out $zip
Tools release-manifest --channel $Channel --version $Version --runtime-zip $zip --zip-base-url "../$Version/" --out (Join-Path $work 'release.payload.json')
New-Item -ItemType Directory -Force (Join-Path $site "runtime\$Channel"), (Join-Path $site 'catalog') | Out-Null
Tools sign --purpose runtime --in (Join-Path $work 'release.payload.json') --key (Join-Path $keys 'runtime.key') --cert (Join-Path $keys 'runtime.cert.json') --out (Join-Path $site "runtime\$Channel\release.json")
Tools catalog build --models (Join-Path $repo 'catalog\models') --out (Join-Path $work 'catalog.payload.json')
Tools sign --purpose catalog --in (Join-Path $work 'catalog.payload.json') --key (Join-Path $keys 'catalog.key') --cert (Join-Path $keys 'catalog.cert.json') --out (Join-Path $site 'catalog\catalog.json')

$root = if ($Serve) { "http://127.0.0.1:$Port/" } else { ([System.Uri](Join-Path $site '.')).AbsoluteUri }
Write-Host ''
Write-Host 'Dev site ready. Use it from a dev-mode client with:' -ForegroundColor Green
Write-Host "  `$env:LOCALAI_DEV_ROOT_URL  = '$root'"
Write-Host "  `$env:LOCALAI_DEV_TRUST_KEY = '$(Get-Content (Join-Path $keys 'root.pub'))'"
Write-Host "  `$env:LOCALAI_CHANNEL       = '$Channel'"
Write-Host "  `$env:LOCALAI_HOME          = '$(Join-Path $Out 'home')'"

if ($Serve) {
    Write-Host "Serving $site on $root (Ctrl+C to stop)"
    python -m http.server $Port --bind 127.0.0.1 --directory $site
}
