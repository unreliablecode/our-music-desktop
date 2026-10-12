$ErrorActionPreference = 'Stop'
$toolsPath = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $toolsPath 'yt-dlp.exe')
Write-Host 'yt-dlp is ready. Install Node.js on PATH for online playback.'
