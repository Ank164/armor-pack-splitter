$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packageCache = ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
$noticeRoot = Join-Path $projectRoot 'licenses'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
$entries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name })
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($dependency in $framework.Value.downloadDependencies) {
        if ($dependency.name -match 'Runtime.win-x64$') {
            $version = $dependency.version.Trim('[',']',' ').Split(',')[0].Trim()
            $entries += "$($dependency.name)/$version"
        }
    }
}
$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add('# Third-party notices')
$notices.Add('')
$notices.Add('Armor Pack Splitter uses the following NuGet components and Microsoft runtimes. Each retains its original copyright and license. Package metadata and license texts are in licenses/. Repository URLs identify corresponding upstream sources; exact package versions and commit metadata are preserved.')
foreach ($entry in ($entries | Sort-Object -Unique)) {
    $parts = $entry.Split('/')
    $id = $parts[0]; $version = $parts[1]
    $packageRoot = Join-Path $packageCache ($id.ToLowerInvariant() + '\' + $version)
    if (!(Test-Path -LiteralPath $packageRoot)) { continue }
    $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { continue }
    [xml]$metadataXml = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $metadataXml.package.metadata
    $destination = Join-Path $noticeRoot "$id-$version"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force
    $existing = Join-Path (Split-Path $projectRoot) "WeaponTweaker\licenses\$id-$version"
    if (Test-Path -LiteralPath $existing) {
        Get-ChildItem -LiteralPath $existing -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
    }
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD-PARTY-NOTICES|ThirdPartyNotices)' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
    $license = $metadata.license.InnerText
    if (!$license) { $license = [string]$metadata.license }
    $hasLicense = @(Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' }).Count -gt 0
    if (!$hasLicense -and $metadata.license.type -eq 'expression') {
        Invoke-WebRequest -Uri ('https://licenses.nuget.org/' + [Uri]::EscapeDataString($license)) -OutFile (Join-Path $destination 'LICENSE.txt')
    }
    $notices.Add(''); $notices.Add("## $id $version")
    $notices.Add("Authors: $($metadata.authors)")
    $notices.Add("Copyright: $($metadata.copyright)")
    $notices.Add("License: $license")
    $sourceUrl = if ($metadata.repository.url) {$metadata.repository.url} else {$metadata.projectUrl}
    $notices.Add("Source: $sourceUrl")
    if ($metadata.repository.commit) {$notices.Add("Commit: $($metadata.repository.commit)")}
    $notices.Add("Metadata and license: licenses/$id-$version/")
}
[IO.File]::WriteAllLines((Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'), $notices)
