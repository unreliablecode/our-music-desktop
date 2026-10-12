$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'tools/yt-dlp.exe'
if (-not (Test-Path -LiteralPath $helper)) { throw 'Run Setup.ps1 before publishing.' }
dotnet publish (Join-Path $PSScriptRoot 'src/OurMusic.Desktop/OurMusic.Desktop.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'NOTICE') -Destination (Join-Path $PSScriptRoot 'publish')
Write-Host ('Ready: ' + (Join-Path $PSScriptRoot 'publish/OurMusic.Desktop.exe'))
