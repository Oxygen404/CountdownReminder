$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedProjectDir = (Resolve-Path -LiteralPath $projectDir).Path
$expectedName = 'clash-traffic-sentinel'
if ((Split-Path -Leaf $resolvedProjectDir) -ne $expectedName) {
    throw "Unexpected project directory: $resolvedProjectDir"
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw '.NET Framework 4.x C# compiler was not found.'
}

$objDir = Join-Path $projectDir 'obj'
$assetDir = Join-Path $projectDir 'assets'
New-Item -ItemType Directory -Force -Path $objDir, $assetDir | Out-Null

$iconMaker = Join-Path $objDir 'IconMaker.exe'
$iconPath = Join-Path $assetDir 'ClashTrafficSentinel.ico'
& $csc /nologo /target:exe /optimize+ /out:$iconMaker /reference:System.Drawing.dll (Join-Path $projectDir 'tools\IconMaker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon build failed.' }
& $iconMaker $iconPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

$references = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Windows.Forms.dll'
)

$sources = Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$manifest = Join-Path $projectDir 'app.manifest'
$output = Join-Path $projectDir 'Clash 流量哨兵.exe'

& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /win32manifest:$manifest /win32icon:$iconPath /out:$output $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$testOutput = Join-Path $objDir 'ClashTrafficSentinel.Tests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /warn:4 /main:ClashTrafficSentinel.Tests /out:$testOutput $references $sources (Join-Path $projectDir 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }

Write-Host "Built: $output"
Write-Host "Tests: $testOutput"

