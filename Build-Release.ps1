param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'ArmorPackSplitter.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactRoot ("ArmorPackSplitter-$version-win-x64")
if (Test-Path -LiteralPath $publishRoot) { throw "Release folder already exists: $publishRoot. Preserve or remove it before rebuilding." }
& dotnet publish (Join-Path $projectRoot 'ArmorPackSplitter.csproj') -c $Configuration -o $publishRoot -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
$checks = Join-Path $artifactRoot ('checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checks | Out-Null
$process = Start-Process -FilePath (Join-Path $publishRoot 'ArmorPackSplitter.exe') -ArgumentList '--self-test' -WorkingDirectory $checks -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Regression checks failed. See $checks\self-test.txt" }
$zip = Join-Path $artifactRoot ("ArmorPackSplitter-$version-win-x64.zip")
if (Test-Path -LiteralPath $zip) { throw "Archive already exists: $zip" }
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $publishRoot | Select-Object -ExpandProperty FullName) -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($zip + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
Write-Output $zip
Write-Output "Regression checks: $checks\self-test.txt"
