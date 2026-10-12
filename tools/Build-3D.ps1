$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSource = Join-Path $taskRoot 'Native3D'
$taskOutput = Join-Path $taskRoot 'artifacts/native3d'
$taskDll = Join-Path $taskOutput 'bin/Release/PaperDesk3D.dll'
$taskWorker = Join-Path $taskOutput 'bin/Release/PaperDeskModelWorker.exe'
$taskInputs = @((Get-ChildItem -LiteralPath $taskSource -Recurse -File), (Get-ChildItem -LiteralPath (Join-Path $taskRoot 'third-party/nanoprc') -Recurse -File), (Get-ChildItem -LiteralPath (Join-Path $taskRoot 'third-party/zlib') -Recurse -File)) | ForEach-Object { $_ }
if ((Test-Path -LiteralPath $taskDll) -and (Test-Path -LiteralPath $taskWorker)) {
    $taskLastBuild = [Math]::Min((Get-Item -LiteralPath $taskDll).LastWriteTimeUtc.Ticks, (Get-Item -LiteralPath $taskWorker).LastWriteTimeUtc.Ticks)
    if (-not ($taskInputs | Where-Object { $_.LastWriteTimeUtc.Ticks -gt $taskLastBuild })) { exit 0 }
}
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVswhere)) { throw 'Native 3D build requires Visual Studio C++ Build Tools and CMake; see README.md.' }
$taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $taskVs) { throw 'Native 3D build requires Visual Studio C++ Build Tools and CMake; see README.md.' }
$taskCmake = Join-Path $taskVs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
& $taskCmake -S $taskSource -B $taskOutput -A x64
if ($LASTEXITCODE -ne 0) { throw 'Native 3D CMake configuration failed; see the output above.' }
& $taskCmake --build $taskOutput --config Release --target PaperDesk3D PaperDeskModelWorker -j 4
if ($LASTEXITCODE -ne 0) { throw 'Native 3D compilation failed; see the compiler output above.' }
