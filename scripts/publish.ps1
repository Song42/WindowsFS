[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

. (Join-Path $PSScriptRoot 'common.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Initialize-WindowsFSDotNet -RepositoryRoot $repositoryRoot
$dotnet = Get-WindowsFSDotNet -RepositoryRoot $repositoryRoot
$outputDirectory = Join-Path $repositoryRoot '.artifacts/publish/win-x64'

Push-Location -LiteralPath $repositoryRoot
try {
    & $dotnet publish src/WindowsFS.App/WindowsFS.App.csproj --configuration Release `
        --runtime $Runtime --self-contained true --output $outputDirectory `
        -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with code $LASTEXITCODE." }
    Write-Output "Portable application: $outputDirectory"
}
finally {
    Pop-Location
}
