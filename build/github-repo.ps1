<#
Creates the GitHub repository for PilotLauncher (or updates an existing one) with its description and topics, and can push
master. Nothing is published unless you run it: the visibility has to be chosen explicitly.

  .\build\github-repo.ps1 -Visibility private -DryRun       shows what would be done
  .\build\github-repo.ps1 -Visibility public                creates mahach666/PilotLauncher, sets description and topics
  .\build\github-repo.ps1 -Visibility public -Push          ... and pushes master

Needs the GitHub CLI (gh) signed in with the "repo" scope. To publish a release afterwards, push a tag vX.Y.Z
(see README, "Сборка").
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('public', 'private')][string]$Visibility,
    [string]$Repo = 'mahach666/PilotLauncher',
    [switch]$Push,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$description = 'Лаунчер версий Pilot (ICE Enterprise, ICE, BIM, ECM): скачивает версии из центра загрузок, запускает несколько версий рядом в изолированных профилях, ставит расширения и показывает заметки о выпуске. Windows, WPF, .NET 10.'
$topics = 'pilot', 'ascon', 'pilot-ice', 'launcher', 'version-manager', 'windows', 'wpf', 'dotnet', 'csharp', 'desktop-app', 'portable', 'release-notes'

function Invoke-Gh {
    param([string[]]$Arguments)
    if ($DryRun) { Write-Host ('gh ' + ($Arguments -join ' ')); return }
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments[0]) failed ($LASTEXITCODE)" }
}

if ($DryRun) {
    Write-Host "Dry run: nothing is created or changed. Repository: $Repo ($Visibility)"
    $exists = $false
}
else {
    & gh repo view $Repo *> $null
    $exists = ($LASTEXITCODE -eq 0)
}

if ($exists) {
    Invoke-Gh @('repo', 'edit', $Repo, '--description', $description)
}
else {
    Invoke-Gh @('repo', 'create', $Repo, "--$Visibility", '--description', $description, '--source', $root, '--remote', 'origin')
}

$editArgs = @('repo', 'edit', $Repo)
foreach ($topic in $topics) { $editArgs += @('--add-topic', $topic) }
Invoke-Gh $editArgs

if ($Push) {
    if ($DryRun) { Write-Host 'git push -u origin master' }
    else {
        git -C $root push -u origin master
        if ($LASTEXITCODE -ne 0) { throw "git push failed ($LASTEXITCODE)" }
    }
}
