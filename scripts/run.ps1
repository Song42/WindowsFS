[CmdletBinding()]
param([string]$Scan)

. (Join-Path $PSScriptRoot 'common.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Initialize-WindowsFSDotNet -RepositoryRoot $repositoryRoot
$dotnet = Get-WindowsFSDotNet -RepositoryRoot $repositoryRoot

Push-Location -LiteralPath $repositoryRoot
try {
    $runArguments = @('run', '--project', 'src/WindowsFS.App/WindowsFS.App.csproj', '--configuration', 'Release')
    if (-not [string]::IsNullOrWhiteSpace($Scan)) {
        $runArguments += @('--', '--scan', $Scan)
    }
    & $dotnet @runArguments
    if ($LASTEXITCODE -ne 0) { throw "Application exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
