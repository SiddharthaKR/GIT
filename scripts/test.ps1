$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$dotnetPath = if (Test-Path "$taskRoot/.dotnet/dotnet.exe") { "$taskRoot/.dotnet/dotnet.exe" } else { 'dotnet' }
Push-Location $taskRoot
try {
    & $dotnetPath build PrivateGit.Tests/PrivateGit.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnetPath PrivateGit.Tests/bin/Release/net10.0/PrivateGit.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
