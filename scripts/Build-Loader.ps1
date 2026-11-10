[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('NX_HOME')][string]$NxHome,
    [string]$OutputDirectory,
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Get-RepositoryRoot
$nx = Get-Nx12Layout -NxHome $NxHome
if (-not $Compiler) { $Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw '.NET Framework 4 x64 csc.exe is required; it is not installed by this script.' }
if (-not $OutputDirectory) {
    $name = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $root ('data\builds\' + $name)
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-OutsideNxInstallation -NxHome $nx.Home -Paths @($root, $OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Build output already exists. Choose a new OutputDirectory; no files are overwritten.' }
$sources = @('NX_MCP_Loader.cs', 'NX_MCP_Assembly.cs', 'NX_MCP_DraftingExtension.cs') | ForEach-Object { Join-Path $root ('mcp\loader\' + $_) }
$sdkReferences = @('NXOpen.dll', 'NXOpen.UF.dll', 'NXOpenUI.dll', 'NXOpen.Utilities.dll') | ForEach-Object { Join-Path $nx.Sdk $_ }
foreach ($path in @($sources) + @($sdkReferences)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required source/reference missing: ' + $path) }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$dll = Join-Path $OutputDirectory 'NX_MCP_Loader.dll'
$arguments = @('/nologo', '/target:library', '/platform:x64', '/codepage:65001', ('/out:' + $dll))
$arguments += @($sdkReferences | ForEach-Object { '/reference:' + $_ })
$arguments += @('System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Core.dll', 'System.dll') | ForEach-Object { '/reference:' + $_ }
$arguments += $sources
$previousPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $output = @(& $Compiler @arguments 2>&1)
    $code = $LASTEXITCODE
} finally { $ErrorActionPreference = $previousPreference }
$log = Join-Path $OutputDirectory 'compile.log'
$output | ForEach-Object { [string]$_ } | Set-Content -LiteralPath $log -Encoding UTF8
if ($code -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw ('Build failed. Log retained: ' + $log) }
if ((Get-Item -LiteralPath $dll).Length -eq 0) { throw 'Compiler produced an empty DLL.' }
$result = [pscustomobject]@{
    outputDirectory = $OutputDirectory; dll = $dll; dllSHA256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    nxVersion = $nx.Version; nxSdkDirectory = $nx.Sdk; compiled = $true; nativeRuntimeVerified = $false
    sourceSHA256 = @($sources | ForEach-Object { [pscustomobject]@{ file = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
}
Write-LocalJson -Value $result -Path (Join-Path $OutputDirectory 'build.json')
Write-Host 'Compilation completed. No NX process was started; native acceptance is separate.'
$result
