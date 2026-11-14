param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDir = Join-Path $projectDir $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
& (Join-Path $projectDir 'build-native.ps1') -OutputDirectory $OutputDirectory
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /codepage:65001 "/out:$outputDir\PopupGuard.exe" "/win32manifest:$projectDir\app.manifest" "/resource:$projectDir\data\presets.json,PopupGuard.presets.json" "/resource:$projectDir\data\software-rules.json,PopupGuard.software-rules.json" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Management.dll /r:Microsoft.CSharp.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$outputDir\PopupGuard.Tests.exe" "/win32manifest:$projectDir\app.manifest" "/r:$outputDir\PopupGuard.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "$projectDir\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$outputDir\PopupGuard.Demo.exe" "/win32manifest:$projectDir\app.manifest" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$projectDir\tests\Demo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Demo compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$outputDir\MaintenanceFixture.exe" /r:System.dll "$projectDir\tests\MaintenanceFixture.cs"
if ($LASTEXITCODE -ne 0) { throw 'Maintenance fixture compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$outputDir\PopupGuard.MaintenanceTests.exe" "/win32manifest:$projectDir\app.manifest" "/r:$outputDir\PopupGuard.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Xml.dll "$projectDir\tests\MaintenanceTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Maintenance test compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$outputDir\PopupGuard.CaptureTests.exe" "/win32manifest:$projectDir\app.manifest" "/r:$outputDir\PopupGuard.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$projectDir\tests\CaptureTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Capture test compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$outputDir\PopupGuard.LatencyTests.exe" "/win32manifest:$projectDir\app.manifest" "/r:$outputDir\PopupGuard.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$projectDir\tests\LatencyTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Latency test compilation failed.' }
Write-Output "Built: $outputDir\PopupGuard.exe"
