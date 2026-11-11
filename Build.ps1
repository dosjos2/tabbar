$ErrorActionPreference = 'Stop'
$previousAppData = $env:APPDATA
$previousPackages = $env:NUGET_PACKAGES
Push-Location $PSScriptRoot
try {
    # Keep restore independent of protected user-level NuGet configuration.
    $env:APPDATA = Join-Path $PSScriptRoot '.buildprofile'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
    dotnet restore --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet publish -c Release --no-restore --no-self-contained -p:OutputPath=bin\Publish\ -o dist\1.7.1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally {
    $env:APPDATA = $previousAppData
    $env:NUGET_PACKAGES = $previousPackages
    Pop-Location
}
