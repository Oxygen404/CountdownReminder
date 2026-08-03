$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedProjectDir = (Resolve-Path -LiteralPath $projectDir).Path
$expectedProjectDir = (Join-Path (Resolve-Path -LiteralPath (Split-Path -Parent $projectDir)).Path 'claude-Console')
if (-not [string]::Equals($resolvedProjectDir, $expectedProjectDir, [System.StringComparison]::OrdinalIgnoreCase)) {
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
$iconPath = Join-Path $assetDir 'ClaudeConsole.ico'
& $csc /nologo /target:exe /optimize+ /out:$iconMaker /reference:System.Drawing.dll (Join-Path $projectDir 'tools\IconMaker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon build failed.' }
& $iconMaker $iconPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

$references = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Net.Http.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Windows.Forms.dll'
)

$source = Join-Path $projectDir 'src\Program.cs'
$manifest = Join-Path $projectDir 'app.manifest'
$output = Join-Path $projectDir 'Claude Console.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /win32manifest:$manifest /win32icon:$iconPath /out:$output $references $source
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$testOutput = Join-Path $objDir 'ClaudeConsole.Tests.exe'
& $csc /nologo /target:exe /platform:anycpu /optimize+ /warn:4 /main:ClaudeConsole.Tests /out:$testOutput $references $source (Join-Path $projectDir 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }

Write-Host "Built: $output"
Write-Host "Tests: $testOutput"
