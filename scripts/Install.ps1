[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('NX_HOME')][string]$NxHome,
    [string]$Workspace,
    [string]$PythonCommand = 'python',
    [string]$StateDirectory,
    [string]$Wheelhouse,
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Get-RepositoryRoot
$nx = Get-Nx12Layout -NxHome $NxHome
if (-not $StateDirectory) { $StateDirectory = Join-Path $root 'data' }
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
if (-not $Workspace) { $Workspace = Join-Path $StateDirectory 'workspace' }
$Workspace = [IO.Path]::GetFullPath($Workspace)
Assert-OutsideNxInstallation -NxHome $nx.Home -Paths @($root, $StateDirectory, $Workspace)
$configPath = Join-Path $StateDirectory 'install.json'
$venv = Join-Path $StateDirectory 'venv'
if ((Test-Path -LiteralPath $configPath) -or (Test-Path -LiteralPath $venv)) { throw 'Local install state already exists. Preserve it and choose a new StateDirectory; this script does not reset an installation.' }
$requirements = Join-Path $root 'mcp\requirements-tested.txt'
if (-not (Test-Path -LiteralPath $requirements -PathType Leaf)) { throw 'mcp/requirements-tested.txt is missing.' }
& $PythonCommand -c 'import sys; assert sys.version_info[:2] == (3, 12), "Use an installed 64-bit Python 3.12"; assert sys.maxsize > 2**32, "Use 64-bit Python"'
if ($LASTEXITCODE -ne 0) { throw 'The supplied Python does not match the tested Python 3.12 x64 ABI.' }
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $Workspace)) { New-Item -ItemType Directory -Path $Workspace -Force | Out-Null }
if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) { throw 'Workspace is not a directory.' }
& $PythonCommand -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'Creating the isolated Python environment failed; partial local state is retained.' }
$python = Join-Path $venv 'Scripts\python.exe'
$pipArguments = @('-m', 'pip', 'install', '--disable-pip-version-check', '--requirement', $requirements)
if ($Wheelhouse) {
    $Wheelhouse = (Get-Item -LiteralPath $Wheelhouse -ErrorAction Stop).FullName
    if (-not (Test-Path -LiteralPath $Wheelhouse -PathType Container)) { throw 'Wheelhouse must be a local directory.' }
    $pipArguments += @('--no-index', '--find-links', $Wheelhouse)
}
& $python @pipArguments
if ($LASTEXITCODE -ne 0) { throw 'Installing the pinned dependencies failed. No unpinned upgrade fallback is attempted; obtain the exact wheels or choose a new local state for a separately validated dependency set.' }
& $python -c 'import mcp, pydantic, win32pipe; from importlib.metadata import version; assert version("mcp")=="2.3.0"; assert version("pydantic")=="2.14.2"; assert version("pywin32")=="312"; print("PINNED_IMPORTS_OK")'
if ($LASTEXITCODE -ne 0) { throw 'Pinned dependency import verification failed.' }
$buildArguments = @{ NxHome = $nx.Home; OutputDirectory = (Join-Path $StateDirectory 'build') }
if ($Compiler) { $buildArguments['Compiler'] = $Compiler }
$build = & (Join-Path $PSScriptRoot 'Build-Loader.ps1') @buildArguments
$nxUser = Join-Path $StateDirectory 'nx-user'
$startup = Join-Path $nxUser 'startup'
New-Item -ItemType Directory -Path $startup -Force | Out-Null
$loader = Join-Path $startup 'NX_MCP_Loader.dll'
if (Test-Path -LiteralPath $loader) { throw 'A startup DLL already exists; it is not overwritten.' }
Copy-Item -LiteralPath $build.dll -Destination $loader
if ((Get-FileHash -LiteralPath $loader -Algorithm SHA256).Hash -ne $build.dllSHA256) { throw 'Local startup DLL copy failed its hash check.' }
$configuration = [pscustomobject]@{
    schemaVersion = 1; repositoryRoot = $root; nxHome = $nx.Home; nxVersion = $nx.Version
    workspace = $Workspace; python = $python; nxUserDirectory = $nxUser; loader = $loader
    loaderSHA256 = $build.dllSHA256; compiled = $true; nativeRuntimeVerified = $false
    dependencyPins = @{ mcp = '2.3.0'; pydantic = '2.14.2'; pywin32 = '312' }
    dependencyVersionsVerified = $true
}
Write-LocalJson -Value $configuration -Path $configPath
$example = @{ mcpServers = @{ nx12 = @{ command = $python; args = @((Join-Path $root 'mcp\run_stdio.py'), '--config', $configPath); cwd = $root } } }
Write-LocalJson -Value $example -Path (Join-Path $StateDirectory 'mcp-config.local.json')
Write-Host ('Local installation prepared: ' + $configPath)
Write-Host 'No global environment, license, security setting, MCP host configuration, existing NX startup, or running NX process was changed.'
Write-Host 'Next: start NX12 with Start-NX12.ps1, configure your MCP host from mcp-config.local.json, then verify real tool results.'
