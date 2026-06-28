param(
    [switch] $Package,
    [string] $Configuration = 'Release',
    [string] $EasyHookVersion = '2.7.7097'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildRoot = Join-Path $repoRoot 'build'
$packageRoot = Join-Path $repoRoot 'packages'
$distRoot = Join-Path $repoRoot 'dist'
$artifactRoot = Join-Path $repoRoot 'artifacts'
$defaultDistDir = Join-Path $distRoot 'VirtualDesktopSwitcher'
$distDir = $defaultDistDir
$easyHookPackage = Join-Path $packageRoot "EasyHook.$EasyHookVersion.nupkg"
$easyHookExtract = Join-Path $packageRoot "EasyHook.$EasyHookVersion"
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

function New-CleanDirectory {
    param([string] $Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Invoke-Csc {
    param([string[]] $Arguments)

    & $csc @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
try {
    New-CleanDirectory $distDir
}
catch {
    $distDir = Join-Path $distRoot ("VirtualDesktopSwitcher-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Warning "Could not clean $defaultDistDir. It may contain DLLs loaded by Virtual Desktop Streamer. Building to $distDir instead."
    New-CleanDirectory $distDir
}

if (-not (Test-Path -LiteralPath $easyHookPackage)) {
    $uri = "https://www.nuget.org/api/v2/package/EasyHook/$EasyHookVersion"
    Write-Host "Downloading EasyHook $EasyHookVersion..."
    Invoke-WebRequest -Uri $uri -OutFile $easyHookPackage
}

if (-not (Test-Path -LiteralPath $easyHookExtract)) {
    $zipPath = Join-Path $packageRoot "EasyHook.$EasyHookVersion.zip"
    Copy-Item -LiteralPath $easyHookPackage -Destination $zipPath -Force
    Expand-Archive -LiteralPath $zipPath -DestinationPath $easyHookExtract
}

Get-ChildItem -Path $easyHookExtract -Recurse -File | Unblock-File

$easyHookManaged = Join-Path $easyHookExtract 'lib\net40\EasyHook.dll'
$easyHookNativeDir = Join-Path $easyHookExtract 'content\net40'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'

$workerOut = Join-Path $distDir 'VdCodecWorker.dll'
$payloadOut = Join-Path $distDir 'VdCodecPayload.dll'
$exeOut = Join-Path $distDir 'VirtualDesktopSwitcher.exe'

Write-Host "Compiling worker..."
Invoke-Csc @(
    '/nologo',
    '/target:library',
    '/platform:x64',
    '/optimize+',
    "/out:$workerOut",
    "/reference:$(Join-Path $framework 'System.Core.dll')",
    "/reference:$(Join-Path $wpf 'PresentationFramework.dll')",
    "/reference:$(Join-Path $wpf 'WindowsBase.dll')",
    "/reference:$(Join-Path $framework 'System.Xaml.dll')",
    (Join-Path $repoRoot 'src\VdCodecRotator.Worker\StreamerCodecWorker.cs')
)

Write-Host "Compiling payload..."
Invoke-Csc @(
    '/nologo',
    '/target:library',
    '/platform:x64',
    '/optimize+',
    "/out:$payloadOut",
    "/reference:$easyHookManaged",
    "/reference:$workerOut",
    (Join-Path $repoRoot 'src\VdCodecRotator.Payload\PayloadEntryPoint.cs')
)

Write-Host "Compiling rotator..."
Invoke-Csc @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    "/out:$exeOut",
    "/reference:$easyHookManaged",
    "/reference:$(Join-Path $framework 'System.Core.dll')",
    "/reference:$(Join-Path $framework 'System.Drawing.dll')",
    "/reference:$(Join-Path $framework 'System.Windows.Forms.dll')",
    (Join-Path $repoRoot 'src\VdCodecRotator\Program.cs')
)

Copy-Item -LiteralPath $easyHookManaged -Destination (Join-Path $distDir 'EasyHook.dll') -Force
Copy-Item -LiteralPath (Join-Path $easyHookNativeDir 'EasyHook64.dll') -Destination (Join-Path $distDir 'EasyHook64.dll') -Force
Copy-Item -LiteralPath (Join-Path $easyHookNativeDir 'EasyLoad64.dll') -Destination (Join-Path $distDir 'EasyLoad64.dll') -Force
Copy-Item -LiteralPath (Join-Path $easyHookNativeDir 'EasyHook64Svc.exe') -Destination (Join-Path $distDir 'EasyHook64Svc.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $distDir 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $distDir 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $distDir 'THIRD-PARTY-NOTICES.md') -Force

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $artifactRoot 'latest-dist.txt') -Value $distDir
Write-Host "Built $exeOut"

if ($Package) {
    $zip = Join-Path $artifactRoot 'VirtualDesktopSwitcher.zip'
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zip
    Write-Host "Packaged $zip"
}
