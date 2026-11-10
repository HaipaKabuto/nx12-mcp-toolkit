Set-StrictMode -Version Latest

function Get-RepositoryRoot {
    return [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

function Get-Nx12Layout {
    param([Parameter(Mandatory = $true)][string]$NxHome)
    $homePath = (Get-Item -LiteralPath $NxHome -ErrorAction Stop).FullName
    if (-not (Test-Path -LiteralPath $homePath -PathType Container)) { throw 'NxHome must be a directory.' }
    $executable = Join-Path $homePath 'NXBIN\ugraf.exe'
    $sdk = Join-Path $homePath 'NXBIN\managed'
    $nxAssembly = Join-Path $sdk 'NXOpen.dll'
    foreach ($path in @($executable, $nxAssembly)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required NX12 file missing: ' + $path) }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    $sdkVersion = [Reflection.AssemblyName]::GetAssemblyName($nxAssembly).Version
    if ($version.FileMajorPart -ne 12 -or $sdkVersion.Major -ne 12) {
        throw ('This package requires NX12. Detected executable ' + $version.FileVersion + ', SDK ' + $sdkVersion)
    }
    return [pscustomobject]@{ Home = $homePath; Executable = $executable; Sdk = $sdk; Version = $version.FileVersion }
}

function Write-LocalJson {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)][string]$Path)
    $text = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $text + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Assert-OutsideNxInstallation {
    param([Parameter(Mandatory = $true)][string]$NxHome, [Parameter(Mandatory = $true)][string[]]$Paths)
    $homePath = [IO.Path]::GetFullPath($NxHome).TrimEnd('\', '/')
    $boundary = $homePath + [IO.Path]::DirectorySeparatorChar
    foreach ($path in $Paths) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if ($fullPath.Equals($homePath, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Build output, repository, local state, NX user directory and workspace must remain outside the NX installation.'
        }
    }
}

function Read-InstallConfiguration {
    param([string]$ConfigPath)
    if (-not $ConfigPath) { $ConfigPath = Join-Path (Get-RepositoryRoot) 'data\install.json' }
    $configuration = Get-Content -LiteralPath $ConfigPath -Encoding UTF8 -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($configuration.schemaVersion -ne 1) { throw 'Unsupported local install configuration.' }
    return $configuration
}

function Get-ChildEnvironment {
    param([Parameter(Mandatory = $true)]$Configuration)
    $root = Get-RepositoryRoot
    $paths = @($env:PATH -split ';' | Where-Object {
        $_ -and $_ -notmatch '(?i)[\\/]Siemens[\\/]NX (?!12\.0(?:[\\/]|$))'
    })
    return @{
        NX_HOME = [string]$Configuration.nxHome
        UGII_BASE_DIR = [string]$Configuration.nxHome
        UGII_ROOT_DIR = ((Join-Path $Configuration.nxHome 'UGII') + [IO.Path]::DirectorySeparatorChar)
        UGII_USER_DIR = [string]$Configuration.nxUserDirectory
        UGII_BACKGROUND_URL = 'none'
        NX_MCP_BACKEND = 'loader'
        NX_MCP_WORKSPACE = [string]$Configuration.workspace
        NX_MCP_ENABLE_AUTOMATION = '1'
        NX_MCP_ENABLE_EXPERIMENTAL = '0'
        NX_MCP_ENABLE_JOURNAL = '0'
        PYTHONPATH = (Join-Path $root 'mcp\src')
        PYTHONNOUSERSITE = '1'
        PYTHONDONTWRITEBYTECODE = '1'
        PATH = ((Join-Path $Configuration.nxHome 'NXBIN') + ';' + ($paths -join ';'))
    }
}

function Set-ChildEnvironment {
    param([Parameter(Mandatory = $true)][Diagnostics.ProcessStartInfo]$ProcessInfo, [Parameter(Mandatory = $true)][hashtable]$Values)
    foreach ($key in @('QT_PLUGIN_PATH', 'QT_QPA_PLATFORM_PLUGIN_PATH', 'QML2_IMPORT_PATH', 'QML_IMPORT_PATH', 'UGII_QT_PLUGIN_PATH', 'NX_MCP_AUDIT_ROOT')) {
        $ProcessInfo.EnvironmentVariables.Remove($key)
    }
    foreach ($key in $Values.Keys) { $ProcessInfo.EnvironmentVariables[$key] = [string]$Values[$key] }
}
