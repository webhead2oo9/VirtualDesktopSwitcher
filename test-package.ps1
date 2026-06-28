param(
    [string] $DistDir,
    [string] $ZipPath = (Join-Path $PSScriptRoot 'artifacts\VirtualDesktopSwitcher.zip')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredFiles = @(
    'VirtualDesktopSwitcher.exe',
    'VdCodecPayload.dll',
    'VdCodecWorker.dll',
    'EasyHook.dll',
    'EasyHook64.dll',
    'EasyLoad64.dll',
    'EasyHook64Svc.exe',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md'
)

if (-not $DistDir) {
    $latestDistPath = Join-Path $PSScriptRoot 'artifacts\latest-dist.txt'
    if (Test-Path -LiteralPath $latestDistPath -PathType Leaf) {
        $DistDir = (Get-Content -LiteralPath $latestDistPath -Raw).Trim()
    }
    else {
        $DistDir = Join-Path $PSScriptRoot 'dist\VirtualDesktopSwitcher'
    }
}

function Assert-FileExists {
    param(
        [string] $Root,
        [string] $Name
    )

    $path = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing package file: $Name"
    }
}

if (-not (Test-Path -LiteralPath $DistDir -PathType Container)) {
    throw "Distribution directory does not exist: $DistDir"
}

foreach ($name in $requiredFiles) {
    Assert-FileExists -Root $DistDir -Name $name
}

$exe = Join-Path $DistDir 'VirtualDesktopSwitcher.exe'
$helpOutput = & $exe --help
if ($LASTEXITCODE -ne 0) {
    throw "VirtualDesktopSwitcher.exe --help failed with exit code $LASTEXITCODE"
}

$helpText = $helpOutput -join "`n"
if ($helpText -notmatch 'VirtualDesktopSwitcher') {
    throw "VirtualDesktopSwitcher.exe --help did not print the expected title."
}

if ($helpText -notmatch 'Known codecs: Automatic, H264, H264Plus, HEVC, HEVC10bit, AV110bit') {
    throw "VirtualDesktopSwitcher.exe --help did not print the expected codec list."
}

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Package zip does not exist: $ZipPath"
}

$extract = Join-Path ([System.IO.Path]::GetTempPath()) ('VirtualDesktopSwitcher-test-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $extract
    foreach ($name in $requiredFiles) {
        Assert-FileExists -Root $extract -Name $name
    }
}
finally {
    if (Test-Path -LiteralPath $extract) {
        Remove-Item -LiteralPath $extract -Recurse -Force
    }
}

Write-Host "Package tests passed."
