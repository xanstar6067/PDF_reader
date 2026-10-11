param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'PDF_Reader\PDF_Reader\PDF_Reader.csproj'
$outputDirectory = Join-Path $projectRoot 'artifacts\PaperDesk'
$standalone = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish $project -c Release -r win-x64 --self-contained $standalone -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать PaperDesk PDF.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputDirectory
Write-Host "Готово: $(Join-Path $outputDirectory 'PaperDesk.exe')"
