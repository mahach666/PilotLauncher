<#
Builds the two ways to ship PilotLauncher into .\artifacts:
  PilotLauncher-Setup.exe               per-user installer (the same exe; a "-Setup" name makes it install itself)
  PilotLauncher-portable-win-x64.zip    PilotLauncher.exe + portable.flag: settings stay next to the exe
  SHA256SUMS.txt                        checksums of the two files above
Both contain one self-contained single-file exe, so the target PC needs no .NET installed.

  .\build\publish.ps1                   version from Directory.Build.props
  .\build\publish.ps1 -Version 1.2.0    explicit version (the release workflow passes the tag without the leading "v")
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'." }

$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$portable = Join-Path $artifacts 'portable'
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $portable | Out-Null

dotnet publish (Join-Path $root 'src\PilotLauncher.App') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $publish 'PilotLauncher.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }

$setup = Join-Path $artifacts 'PilotLauncher-Setup.exe'
$zip = Join-Path $artifacts 'PilotLauncher-portable-win-x64.zip'
Copy-Item $exe $setup

Copy-Item $exe (Join-Path $portable 'PilotLauncher.exe')
Set-Content (Join-Path $portable 'portable.flag') '' -Encoding ascii
Set-Content (Join-Path $portable 'README.txt') @'
Pilot Launcher, portable edition.
Run PilotLauncher.exe. Settings, logs and saved passwords of the isolated profiles stay in the "data" folder next to it.
Downloaded Pilot versions go to the folder chosen in Settings (default: C:\ProgramData\PilotLauncher\versions).
'@ -Encoding utf8
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -Force

$sums = foreach ($file in $setup, $zip) {
    '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') $sums -Encoding ascii

Write-Host "PilotLauncher $Version"
Get-ChildItem $artifacts -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
