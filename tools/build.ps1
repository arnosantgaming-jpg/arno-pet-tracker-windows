# Build the Windows version on Windows: self-contained app with its capture program,
# zipped as dist\arno-pet-tracker-windows.zip. Needs the .NET SDK 10.0.4xx.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build\win-x64'
Remove-Item -Recurse -Force $out, (Join-Path $root 'dist') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\App\App.csproj') -c Release -r win-x64 --self-contained true -o $out
dotnet publish (Join-Path $root 'src\Capture\Capture.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'capture')
foreach ($f in 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt', 'LEGAL.md', 'assets\icon.png', 'tools\install.ps1') { Copy-Item (Join-Path $root $f) $out }
Copy-Item -Recurse (Join-Path $root 'licenses') (Join-Path $out 'licenses')
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
Compress-Archive -Path $out -DestinationPath (Join-Path $root 'dist\arno-pet-tracker-windows.zip')
Write-Host "Built $out"
