param([switch]$Demo)

$ErrorActionPreference = 'Stop'
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Instalá el SDK de .NET 10 y volvé a abrir la terminal.'
}

$project = Join-Path $PSScriptRoot 'src\OpenAIUsageWidget\OpenAIUsageWidget.csproj'
& $dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló. No se abrió la aplicación.' }

$executable = Join-Path $PSScriptRoot 'src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe'
$options = @{ FilePath = $executable; WorkingDirectory = $PSScriptRoot }
if ($Demo) { $options.ArgumentList = @('--demo') }
Start-Process @options
