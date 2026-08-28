param(
    [string] $EasyHookVersion = '2.7.7097'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
$easyHook = Join-Path $repoRoot "packages\EasyHook.$EasyHookVersion\lib\net40\EasyHook.dll"
if (-not (Test-Path -LiteralPath $easyHook)) {
    throw "EasyHook is not available. Run .\build.ps1 once before running the manual feature tests."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('VirtualDesktopSwitcher-test-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $testExe = Join-Path $testRoot 'ManualFeatureTests.exe'
    & $csc @(
        '/nologo',
        '/target:exe',
        '/platform:x64',
        '/main:VirtualDesktopSwitcher.Tests.ManualFeatureTests',
        "/out:$testExe",
        "/reference:$easyHook",
        "/reference:$(Join-Path $framework 'System.Core.dll')",
        "/reference:$(Join-Path $framework 'System.Drawing.dll')",
        "/reference:$(Join-Path $framework 'System.Web.Extensions.dll')",
        "/reference:$(Join-Path $framework 'System.Windows.Forms.dll')",
        (Join-Path $repoRoot 'src\VdCodecRotator\Program.cs'),
        (Join-Path $repoRoot 'src\VdCodecRotator\ManualSettings.cs'),
        (Join-Path $repoRoot 'src\VdCodecRotator\RawInput.cs'),
        (Join-Path $repoRoot 'tests\ManualFeatureTests.cs')
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Manual feature test compilation failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $easyHook -Destination (Join-Path $testRoot 'EasyHook.dll')
    & $testExe
    if ($LASTEXITCODE -ne 0) {
        throw "Manual feature tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
