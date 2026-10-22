[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'

function Get-DotNetPath {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @()
    if ($command) { $candidates += $command.Source }
    $candidates += @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    )
    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}

function Get-RequiredDotNetMajor {
    $globalJsonPath = Join-Path $PSScriptRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "No se encontró global.json: $globalJsonPath"
    }
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    $version = [version]$globalJson.sdk.version
    return $version.Major
}

function Test-DotNetSdk([string]$DotNetPath, [int]$RequiredMajor) {
    if (-not $DotNetPath) { return $false }
    $versionText = (& $DotNetPath --version 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { return $false }
    $version = [version]::MinValue
    return [version]::TryParse($versionText, [ref]$version) -and $version.Major -eq $RequiredMajor
}

function Get-InnoInstallRecords {
    $records = @()
    $uninstallPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $baseKey = $null
            $uninstallKey = $null
            try {
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $uninstallKey = $baseKey.OpenSubKey($uninstallPath)
                if (-not $uninstallKey) { continue }

                foreach ($subKeyName in $uninstallKey.GetSubKeyNames()) {
                    if ($subKeyName -notmatch '^Inno Setup(?: \d+)?_is1$') { continue }
                    $appKey = $null
                    try {
                        $appKey = $uninstallKey.OpenSubKey($subKeyName)
                        if (-not $appKey) { continue }
                        $displayName = [string]$appKey.GetValue('DisplayName')
                        $versionText = [string]$appKey.GetValue('DisplayVersion')
                        if ($versionText -notmatch '^\d+\.\d+(?:\.\d+)?') {
                            $match = [regex]::Match($displayName, '(\d+\.\d+(?:\.\d+)?)')
                            if ($match.Success) { $versionText = $match.Groups[1].Value }
                        }
                        $version = [version]::MinValue
                        if (-not [version]::TryParse($versionText, [ref]$version)) { continue }

                        $location = [string]$appKey.GetValue('InstallLocation')
                        if (-not $location) {
                            $icon = [string]$appKey.GetValue('DisplayIcon')
                            if ($icon) { $location = Split-Path -Parent ($icon.Trim('"').Split(',')[0]) }
                        }
                        if ($location) {
                            $records += [pscustomobject]@{ Version = $version; Location = $location }
                        }
                    }
                    finally {
                        if ($appKey) { $appKey.Dispose() }
                    }
                }
            }
            catch [System.Security.SecurityException] { }
            catch [System.UnauthorizedAccessException] { }
            finally {
                if ($uninstallKey) { $uninstallKey.Dispose() }
                if ($baseKey) { $baseKey.Dispose() }
            }
        }
    }
    return $records
}

function Get-InnoCompilerCandidates([string]$ExplicitPath) {
    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "No se encontró ISCC.exe en: $ExplicitPath"
        }
        return ,(Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $paths = @()
    foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        foreach ($folder in @('Inno Setup 7', 'Inno Setup 6')) {
            $paths += Join-Path $root "$folder\ISCC.exe"
        }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $paths += $command.Source }

    foreach ($record in (Get-InnoInstallRecords | Sort-Object Version -Descending)) {
        $paths += Join-Path $record.Location 'ISCC.exe'
    }

    return $paths | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
}

function Get-InnoCompilerVersion([string]$CompilerPath, [object[]]$InstallRecords) {
    foreach ($record in $InstallRecords) {
        $recordCompiler = Join-Path $record.Location 'ISCC.exe'
        if ([string]::Equals([IO.Path]::GetFullPath($recordCompiler), [IO.Path]::GetFullPath($CompilerPath), [StringComparison]::OrdinalIgnoreCase)) {
            return $record.Version
        }
    }

    $pathMatch = [regex]::Match($CompilerPath, '(?i)Inno(?: Setup)?[- ](?<version>\d+(?:\.\d+){0,2})')
    if ($pathMatch.Success) {
        $versionText = $pathMatch.Groups['version'].Value
        if ($versionText -notmatch '\.') {
            if ([int]$versionText -ge 7) { return [version]::new([int]$versionText, 0, 0) }
            return $null
        }
        $version = [version]::MinValue
        if ([version]::TryParse($versionText, [ref]$version)) { return $version }
    }
    return $null
}

function Invoke-WingetInstall([string]$PackageId, [switch]$UserScope) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'No se encontró WinGet. Instalá o actualizá “Instalador de aplicación” (App Installer) desde Microsoft Store y volvé a ejecutar este script.'
    }

    $arguments = @('install', '--id', $PackageId, '--exact', '--source', 'winget')
    if ($UserScope) { $arguments += @('--scope', 'user') }
    Write-Host "Instalando dependencia con WinGet: $PackageId"
    if ($PackageId -eq 'Microsoft.DotNet.SDK.10') {
        Write-Warning 'El instalador oficial del SDK puede solicitar elevación de Windows (UAC). No se puede garantizar una instalación de máquina sin permisos de administrador.'
    }
    Write-Host 'WinGet puede pedir confirmar los acuerdos del origen y del paquete.'
    & $winget.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WinGet no pudo instalar $PackageId (código $LASTEXITCODE). Revisá el mensaje anterior y los permisos del equipo."
    }
}

$requiredMajor = Get-RequiredDotNetMajor
$minimumInnoVersion = [version]'6.7.0'
$installerRecords = @(Get-InnoInstallRecords)

Push-Location $PSScriptRoot
try {
    $dotnet = Get-DotNetPath
    if (Test-DotNetSdk $dotnet $requiredMajor) {
        $sdkVersion = (& $dotnet --version).Trim()
        Write-Host ".NET SDK $sdkVersion ya está disponible."
    }
    else {
        Invoke-WingetInstall -PackageId "Microsoft.DotNet.SDK.$requiredMajor"
        $dotnet = Get-DotNetPath
        if (-not (Test-DotNetSdk $dotnet $requiredMajor)) {
            throw "WinGet terminó, pero no se detecta un SDK .NET $requiredMajor desde $PSScriptRoot. Cerrá y volvé a abrir PowerShell para actualizar PATH y ejecutá otra vez este script."
        }
        Write-Host ".NET SDK $((& $dotnet --version).Trim()) instalado y disponible."
    }

    $compiler = $null
    foreach ($candidate in (Get-InnoCompilerCandidates -ExplicitPath $IsccPath)) {
        $version = Get-InnoCompilerVersion -CompilerPath $candidate -InstallRecords $installerRecords
        if ($version -and $version -ge $minimumInnoVersion) {
            $compiler = $candidate
            Write-Host "Inno Setup $version ya está disponible: $compiler"
            break
        }
    }

    if (-not $compiler) {
        if ($IsccPath) {
            throw "No se pudo verificar que '$IsccPath' sea Inno Setup $minimumInnoVersion o posterior. Indicá un ISCC.exe instalado y verificable, o quitá -IsccPath para permitir instalar Inno Setup 7 con WinGet."
        }
        Invoke-WingetInstall -PackageId 'JRSoftware.InnoSetup.7' -UserScope
        $installerRecords = @(Get-InnoInstallRecords)
        foreach ($candidate in (Get-InnoCompilerCandidates)) {
            $version = Get-InnoCompilerVersion -CompilerPath $candidate -InstallRecords $installerRecords
            if ($version -and $version -ge $minimumInnoVersion) {
                $compiler = $candidate
                Write-Host "Inno Setup $version instalado y disponible: $compiler"
                break
            }
        }
        if (-not $compiler) {
            throw 'WinGet terminó, pero no se encontró ISCC.exe 6.7 o posterior. Cerrá y volvé a abrir PowerShell para actualizar PATH y ejecutá otra vez este script.'
        }
    }

    Write-Host 'Dependencias de compilación listas. Ahora podés ejecutar .\build.ps1 o .\build-installer.ps1.'
}
finally {
    Pop-Location
}
