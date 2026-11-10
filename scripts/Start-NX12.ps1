[CmdletBinding()]
param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$configuration = Read-InstallConfiguration -ConfigPath $ConfigPath
$nx = Get-Nx12Layout -NxHome $configuration.nxHome
Assert-OutsideNxInstallation -NxHome $nx.Home -Paths @((Get-RepositoryRoot), $configuration.workspace, $configuration.nxUserDirectory, $configuration.loader)
if (Get-Process -Name ugraf -ErrorAction SilentlyContinue) { throw 'An NX process is already running. No process will be closed or killed; finish your work before starting another NX session.' }
if ((Get-FileHash -LiteralPath $configuration.loader -Algorithm SHA256).Hash -ne $configuration.loaderSHA256) { throw 'Startup Loader SHA256 does not match the local build. No NX process was started.' }
if (-not (Test-Path -LiteralPath $configuration.workspace -PathType Container)) { throw 'Configured workspace is missing.' }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $nx.Executable
$info.WorkingDirectory = $configuration.workspace
$info.UseShellExecute = $false
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
Set-ChildEnvironment -ProcessInfo $info -Values (Get-ChildEnvironment -Configuration $configuration)
$process = [Diagnostics.Process]::Start($info)
[pscustomobject]@{ pid = $process.Id; nxVersion = $nx.Version; loaderSHA256 = $configuration.loaderSHA256; nxUserDirectory = $configuration.nxUserDirectory; workspace = $configuration.workspace }
