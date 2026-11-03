[CmdletBinding()]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$lunaSource = Join-Path $repositoryRoot 'library/LunaTranslator'
$outputDirectory = Join-Path $repositoryRoot 'artifacts/luna-native'
$expectedCommit = '1f784b1a471f3a51a3e130a37d257333f4b9439f'
if ((& git -C $lunaSource rev-parse HEAD) -ne $expectedCommit) {
    throw 'Initialize the pinned LunaTranslator submodule before building.'
}
$vswherePath = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsInstallation = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.ATL -property installationPath
if (-not $vsInstallation) { throw 'Visual C++ Build Tools with the ATL component are required.' }
$vsInstance = Split-Path $vsInstallation -Leaf
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmakePath = $cmakeCommand.Source }
else {
    $cmakePath = Join-Path $vsInstallation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
}
if (-not (Test-Path -LiteralPath $cmakePath)) { throw 'CMake and Visual C++ Build Tools are required.' }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
foreach ($architecture in @('x64', 'Win32')) {
    $bits = if ($architecture -eq 'x64') { '64' } else { '32' }
    $hookBuild = Join-Path $repositoryRoot "artifacts/luna-build/$vsInstance/hook-$bits"
    & $cmakePath -S "$lunaSource/src/NativeImpl/LunaHook" -B $hookBuild -A $architecture `
        "-DCMAKE_BUILD_TYPE=$Configuration" "-DCMAKE_GENERATOR_INSTANCE=$vsInstallation" '-DBUILD_GUI=OFF' '-DWIN10ABOVE=ON' `
        "-DCMAKE_PROJECT_LunaHook_INCLUDE=$repositoryRoot/native/LunaBuildOverrides.cmake"
    if ($LASTEXITCODE -ne 0) { throw "Luna configure failed ($bits)." }
    & $cmakePath --build $hookBuild --config $Configuration --target LunaHostDll LunaHook --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "Luna build failed ($bits)." }
    $upstreamOutput = "$lunaSource/src/NativeImpl/LunaHook/builds/${Configuration}_win10"
    Copy-Item -LiteralPath "$upstreamOutput/LunaHost$bits.dll", "$upstreamOutput/LunaHook$bits.dll", "$upstreamOutput/LunaTmpFontLoader.dll" -Destination $outputDirectory
    $injectorBuild = Join-Path $repositoryRoot "artifacts/luna-build/$vsInstance/injector-$bits"
    & $cmakePath -S "$repositoryRoot/native/LunaInjector" -B $injectorBuild -A $architecture "-DCMAKE_GENERATOR_INSTANCE=$vsInstallation" "-DLUNA_SOURCE_DIR=$lunaSource"
    if ($LASTEXITCODE -ne 0) { throw "Injector configure failed ($bits)." }
    & $cmakePath --build $injectorBuild --config $Configuration --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "Injector build failed ($bits)." }
    Copy-Item -LiteralPath "$injectorBuild/$Configuration/LunaInjector$bits.exe" -Destination $outputDirectory
}
Copy-Item -LiteralPath "$lunaSource/LICENSE" -Destination "$outputDirectory/LunaTranslator-LICENSE.txt"
$dependencySource = Join-Path $repositoryRoot "artifacts/luna-build/$vsInstance/hook-64/_deps"
Copy-Item -LiteralPath "$dependencySource/minhook-src/LICENSE.txt" -Destination "$outputDirectory/MinHook-LICENSE.txt"
Copy-Item -LiteralPath "$dependencySource/uchardet-src/COPYING" -Destination "$outputDirectory/uchardet-COPYING.txt"
Copy-Item -LiteralPath "$dependencySource/uchardet-src/AUTHORS" -Destination "$outputDirectory/uchardet-AUTHORS.txt"
Copy-Item -LiteralPath "$repositoryRoot/THIRD-PARTY-NOTICES.md" -Destination $outputDirectory
Write-Output "Native output: $outputDirectory"
