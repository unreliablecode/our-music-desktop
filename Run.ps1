$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot 'src/OurMusic.Desktop/OurMusic.Desktop.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'The application could not start.' }
