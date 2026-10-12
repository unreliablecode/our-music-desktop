param([string]$OutputPath = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'tools/yt-dlp.exe'
if (-not (Test-Path -LiteralPath $helper)) { throw 'Run Setup.ps1 before publishing.' }
dotnet publish (Join-Path $PSScriptRoot 'src/OurMusic.Desktop/OurMusic.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'NOTICE') -Destination $OutputPath
New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/desktop.png') -Destination (Join-Path $OutputPath 'docs/desktop.png')
New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath 'licenses') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | Copy-Item -Destination (Join-Path $OutputPath 'licenses')
Write-Host ('Ready: ' + (Join-Path $OutputPath 'OurMusic.Desktop.exe'))
