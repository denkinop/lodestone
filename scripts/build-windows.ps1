param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'dotnet',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $Dotnet -PathType Leaf) {
    $Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path
}

function Invoke-BuildCommand {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed ($LASTEXITCODE)"
    }
}

Push-Location (Join-Path $root 'frontend')
try {
    Invoke-BuildCommand npm.cmd @('ci', '--no-audit', '--no-fund')
    Invoke-BuildCommand npm.cmd @('run', 'build')
} finally {
    Pop-Location
}

Push-Location $root
try {
    $project = 'src/Lodestone.App/Lodestone.App.csproj'
    Invoke-BuildCommand $Dotnet @('restore', 'Lodestone.sln', '--locked-mode')
    Invoke-BuildCommand $Dotnet @('build', 'Lodestone.sln', '--no-restore', '-c', $Configuration)
    Invoke-BuildCommand $Dotnet @('test', 'tests/Lodestone.Core.Tests/Lodestone.Core.Tests.csproj', '--no-build', '-c', $Configuration)
    if ($Publish) {
        Invoke-BuildCommand $Dotnet @('publish', $project, '--no-restore', '-c', $Configuration)
    }
} finally {
    Pop-Location
}
