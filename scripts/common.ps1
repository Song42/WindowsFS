Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WindowsFSDotNet {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $localSdk = Join-Path $RepositoryRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) {
        return $localSdk
    }

    $installedSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $installedSdk) {
        throw 'Install the .NET 10 SDK, or place a local SDK in .tools/dotnet.'
    }

    return $installedSdk.Source
}

function Initialize-WindowsFSDotNet {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    # Keep generated developer state inside an ignored workspace directory.
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_HOME = Join-Path $RepositoryRoot '.tools/cli-home'
    $env:NUGET_PACKAGES = Join-Path $RepositoryRoot '.tools/nuget/packages'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
}
