param(
    [string]$Dotnet = 'dotnet',
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory
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

$android = @('-p:LodestoneAndroid=true')
if ($AndroidSdkDirectory) {
    $android += "-p:AndroidSdkDirectory=$((Resolve-Path -LiteralPath $AndroidSdkDirectory).Path)"
}
if ($JavaSdkDirectory) {
    $android += "-p:JavaSdkDirectory=$((Resolve-Path -LiteralPath $JavaSdkDirectory).Path)"
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
    $output = 'src/Lodestone.App/bin/Release/android-apk'
    Invoke-BuildCommand $Dotnet (@('restore', $project, '--locked-mode', '-p:Configuration=Release') + $android)
    Invoke-BuildCommand $Dotnet (@('publish', $project, '--no-restore', '-c', 'Release', '-o', $output) + $android)
    $apk = Get-Item -LiteralPath (Join-Path $output 'com.denkinop.lodestone-Signed.apk')
    Write-Host "APK: $($apk.FullName)"
} finally {
    Pop-Location
}
