[CmdletBinding()]
param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$configuration = Read-InstallConfiguration -ConfigPath $ConfigPath
$nx = Get-Nx12Layout -NxHome $configuration.nxHome
Assert-OutsideNxInstallation -NxHome $nx.Home -Paths @((Get-RepositoryRoot), $configuration.workspace, $configuration.nxUserDirectory)
if (-not (Test-Path -LiteralPath $configuration.python -PathType Leaf)) { throw 'The isolated Python interpreter is missing.' }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $configuration.python
$resolvedConfig = if ($ConfigPath) { [IO.Path]::GetFullPath($ConfigPath) } else { Join-Path (Get-RepositoryRoot) 'data\install.json' }
$entry = Join-Path (Get-RepositoryRoot) 'mcp\run_stdio.py'
$info.Arguments = ('"' + $entry + '" --config "' + $resolvedConfig + '"')
$info.WorkingDirectory = Get-RepositoryRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
Set-ChildEnvironment -ProcessInfo $info -Values (Get-ChildEnvironment -Configuration $configuration)
$process = [Diagnostics.Process]::Start($info)
$process.WaitForExit()
exit $process.ExitCode
