param([string]$OutputDirectory='dist')
$ErrorActionPreference='Stop'
$projectDir=$PSScriptRoot
$outputDir=Join-Path $projectDir $OutputDirectory
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs) { throw 'Visual C++ Build Tools are required for window capture.' }
$msvc=Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk=Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$captureIncludes=@((Join-Path $msvc.FullName 'include'))+@('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $sdk.FullName $_ })
$captureLibraries=@((Join-Path $msvc.FullName 'lib\x64'))+@('ucrt','um' | ForEach-Object { Join-Path $sdkRoot ('Lib\'+$sdk.Name+'\'+$_+'\x64') })
$compiler=Join-Path $msvc.FullName 'bin\Hostx64\x64\cl.exe'
$options=@('/nologo','/std:c++17','/EHsc','/O2','/MT','/W4','/utf-8','/DUNICODE','/D_UNICODE')+@($captureIncludes | ForEach-Object { '/I'+$_ })
$link=@('/link')+@($captureLibraries | ForEach-Object { '/LIBPATH:'+$_ })+@('windowsapp.lib','d3d11.lib','dxgi.lib','windowscodecs.lib','ole32.lib','user32.lib','dwmapi.lib','wtsapi32.lib','/SUBSYSTEM:WINDOWS','/ENTRY:wmainCRTStartup','/MANIFEST:EMBED',('/MANIFESTINPUT:'+(Join-Path $projectDir 'app.manifest')))
& $compiler @options (Join-Path $projectDir 'native\WindowCapture.cpp') ('/Fo'+(Join-Path $outputDir 'WindowCapture.obj')) ('/Fe'+(Join-Path $outputDir 'PopupGuard.Capture.exe')) @link
if($LASTEXITCODE -ne 0) { throw 'Window capture compilation failed.' }
& $compiler @options (Join-Path $projectDir 'native\CaptureFixture.cpp') ('/Fo'+(Join-Path $outputDir 'CaptureFixture.obj')) ('/Fe'+(Join-Path $outputDir 'PopupGuard.GpuFixture.exe')) @link dcomp.lib d2d1.lib dwrite.lib
if($LASTEXITCODE -ne 0) { throw 'GPU capture fixture compilation failed.' }
