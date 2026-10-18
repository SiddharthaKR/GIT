param(
    [ValidateSet('linux-x64','linux-arm64')][string]$Runtime = 'linux-x64',
    [switch]$IncludeWindowsClientGuide
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$dotnetPath = if (Test-Path "$taskRoot/.dotnet/dotnet.exe") { "$taskRoot/.dotnet/dotnet.exe" } else { 'dotnet' }
$outputPath = Join-Path $taskRoot "artifacts/publish/$Runtime/$([Guid]::NewGuid().ToString('N'))"
& $dotnetPath publish "$taskRoot/PrivateGit/PrivateGit.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
New-Item -ItemType Directory -Path "$outputPath/deploy" -Force | Out-Null
Copy-Item "$taskRoot/deploy/*" "$outputPath/deploy" -Recurse -Force
Copy-Item "$taskRoot/README.md","$taskRoot/SECURITY.md","$taskRoot/TESTING.md","$taskRoot/EASY-SETUP.md","$taskRoot/OFFICE-SETUP.md","$taskRoot/ARCHITECTURE.md" $outputPath -Force
Copy-Item "$taskRoot/install.sh","$taskRoot/add-client.sh","$taskRoot/admin.sh","$taskRoot/request-certificate.sh","$taskRoot/uninstall-disable.sh","$taskRoot/uninstall-remove.sh" $outputPath -Force
# Optional on-server self-test of the SSH permission gate (sudo bash scripts/test-ssh-gate.sh ./PrivateGit).
New-Item -ItemType Directory -Path "$outputPath/scripts" -Force | Out-Null
Copy-Item "$taskRoot/scripts/test-ssh-gate.sh" "$outputPath/scripts" -Force
# Push hook (protects main branch and tags; enables branch deletion for writers).
Copy-Item "$taskRoot/hooks" "$outputPath/hooks" -Recurse -Force
foreach ($hook in 'pre-receive','post-receive','backup-main') {
    $hookText = [IO.File]::ReadAllText("$outputPath/hooks/$hook").Replace("`r`n", "`n")
    [IO.File]::WriteAllText("$outputPath/hooks/$hook", $hookText, [Text.UTF8Encoding]::new($false))
}
# Project templates (.gitignore / .gitattributes) installed to /opt/privategit/templates.
Copy-Item "$taskRoot/templates" "$outputPath/templates" -Recurse -Force
if ($IncludeWindowsClientGuide) {
    Copy-Item "$taskRoot/WINDOWS-CLIENT.md" $outputPath -Force
}
# Windows checkout settings must not introduce CRLF into Linux shell scripts.
Get-ChildItem $outputPath -Recurse -Filter '*.sh' | ForEach-Object {
    $scriptText = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($_.FullName, $scriptText, [Text.UTF8Encoding]::new($false))
}
Compress-Archive -Path "$outputPath/*" -DestinationPath "$taskRoot/artifacts/privategit-$Runtime.zip" -Force
Write-Host "Transfer artifacts/privategit-$Runtime.zip to the server. See README.md."
