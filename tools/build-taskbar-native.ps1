param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $outputPath -Parent) -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Building the taskbar bridge requires Visual Studio C++ Build Tools and the Windows SDK.' }
$devcmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $projectRoot 'src\DeskBox.Taskbar\TaskbarGlass.cpp'
# Environment setup and compilation stay in cmd; no file operations cross shells.
$compile = 'call "{0}" -no_logo -arch=x64 -host_arch=x64 && cl /nologo /std:c++20 /EHsc /O2 /MT /LD /DUNICODE /D_UNICODE "{1}" /Fo"{2}" /link /OUT:"{3}" /IMPLIB:"{3}.lib" /EXPORT:DllGetClassObject,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE windowsapp.lib ole32.lib oleaut32.lib user32.lib /NOLOGO' -f $devcmd,$source,($outputPath + '.obj'),$outputPath
& $env:ComSpec /d /c $compile
if ($LASTEXITCODE -ne 0) { throw "Native taskbar build failed: $LASTEXITCODE" }
