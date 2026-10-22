$ErrorActionPreference = 'Stop'
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$candidates = @()
if ($command) { $candidates += $command.Source }
$candidates += @(
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
)
$dotnet = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $dotnet) {
    throw 'No se encontró dotnet. Ejecutá .\setup-build-deps.ps1 para instalar el SDK de .NET 10.'
}

$project = Join-Path $PSScriptRoot 'src\OpenAIUsageWidget\OpenAIUsageWidget.csproj'
Push-Location $PSScriptRoot
try {
    $sdkVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
        throw "Se requiere el SDK de .NET 10 (global.json pide 10.0.100); detectado: $sdkVersion. Ejecutá .\setup-build-deps.ps1."
    }

    & $dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }
}
finally {
    Pop-Location
}

Write-Host 'Compilación correcta. El widget no se ejecutó.'
