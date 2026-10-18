<#
Prints the CHANGELOG.md section of one version (the text of a GitHub release). Fails when the section is missing,
so a release cannot be tagged before its changelog entry is written.

  .\build\release-notes.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Out
)
$ErrorActionPreference = 'Stop'

$changelog = Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md'
$text = [IO.File]::ReadAllText($changelog, [Text.Encoding]::UTF8)
$match = [regex]::Match($text, '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\r\n]*\r?\n(?<body>.*?)(?=^## \[|\z)')
if (-not $match.Success) { throw "CHANGELOG.md has no section for version $Version." }

$notes = $match.Groups['body'].Value.Trim()
if ($Out) { Set-Content $Out $notes -Encoding utf8 } else { $notes }
