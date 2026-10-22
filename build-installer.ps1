[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'No se encontró el SDK de .NET 10. Ejecutá .\setup-build-deps.ps1 y volvé a abrir la terminal.'
}

if (-not $IsccPath) {
    $roots = @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)})
    foreach ($root in $roots) {
        if (-not $root) { continue }
        foreach ($folder in @('Inno Setup 7', 'Inno Setup 6')) {
            $candidate = Join-Path $root "$folder\ISCC.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $IsccPath = $candidate
                break
            }
        }
        if ($IsccPath) { break }
    }
}
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'No se encontró Inno Setup 6.7 o posterior. Ejecutá .\setup-build-deps.ps1 o indicá -IsccPath "ruta\ISCC.exe".'
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path

$project = Join-Path $PSScriptRoot 'src\OpenAIUsageWidget\OpenAIUsageWidget.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?$') {
    throw 'El proyecto debe declarar una Version válida para el instalador.'
}

$outputDir = Join-Path $PSScriptRoot 'artifacts\installer'
# Use a fresh staging directory so old published files never enter a new setup.
$publishDir = Join-Path $PSScriptRoot ('artifacts\installer-staging\' + [Guid]::NewGuid().ToString('N'))
$setupFile = Join-Path $outputDir "OpenAIUsageWidget-$version-win-x64-Setup.exe"
try {
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false --nologo -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'La publicación falló. No se generó el instalador.' }

    Write-Host 'Compilando el instalador con Inno Setup...'
    & $IsccPath /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DInstallerOutputDir=$outputDir" (Join-Path $PSScriptRoot 'installer\OpenAIUsageWidget.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup no pudo generar el instalador.' }
    if (-not (Test-Path -LiteralPath $setupFile -PathType Leaf)) { throw 'No se encontró el instalador generado.' }
}
finally {
    # Only remove the fresh GUID directory owned by this invocation.
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
}

Write-Host "Instalador creado: $setupFile"
