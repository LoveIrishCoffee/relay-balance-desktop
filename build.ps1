[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\RelayBalance.exe'),
    [string]$BuildDirectory = (Join-Path $PSScriptRoot 'build'),
    [string]$NodePath = (Get-Command node -CommandType Application | Select-Object -First 1 -ExpandProperty Source)
)
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler is required.' }
$nodeVersion=(& $NodePath --version).Trim()
if ([Version]($nodeVersion.TrimStart('v')) -lt [Version]'22.13.0') { throw 'Node.js 22.13+ is required. Version 24 is recommended.' }
New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
$license=Join-Path $PSScriptRoot 'NODE-LICENSE.txt'
if (-not (Test-Path -LiteralPath $license)) { throw 'Place the matching official Node.js LICENSE at NODE-LICENSE.txt.' }
$payload=Join-Path $BuildDirectory 'node.exe.gz'
$inputStream=[IO.File]::OpenRead($NodePath)
$outputStream=[IO.File]::Create($payload)
try {
    $gzip=[IO.Compression.GZipStream]::new($outputStream,[IO.Compression.CompressionLevel]::Optimal,$true)
    try { $inputStream.CopyTo($gzip) } finally { $gzip.Dispose() }
} finally { $inputStream.Dispose();$outputStream.Dispose() }
function Get-LowerHash([string]$file) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant() }
$nodeHash=Get-LowerHash $NodePath
$coreHash=Get-LowerHash (Join-Path $PSScriptRoot 'src\core.mjs')
$workerHash=Get-LowerHash (Join-Path $PSScriptRoot 'src\desktop-worker.mjs')
$licenseHash=Get-LowerHash $license
$hasher=[Security.Cryptography.SHA256]::Create()
try { $payloadId=([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($nodeHash+$coreHash+$workerHash)))).Replace('-','').Substring(0,12).ToLowerInvariant() } finally { $hasher.Dispose() }
$generated='namespace RelayBalanceDesktop { public static class RuntimeManifest { public const string NodeHash="'+$nodeHash+'"; public const string CoreHash="'+$coreHash+'"; public const string WorkerHash="'+$workerHash+'"; public const string LicenseHash="'+$licenseHash+'"; public const string PayloadId="'+$payloadId+'"; } }'
[IO.File]::WriteAllText((Join-Path $BuildDirectory 'RuntimeManifest.cs'),$generated,[Text.UTF8Encoding]::new($false))
$icon=Join-Path $PSScriptRoot 'src\app.ico'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)+(Join-Path $BuildDirectory 'RuntimeManifest.cs')
$compilerArguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:'+$OutputPath),('/win32icon:'+$icon),('/win32manifest:'+(Join-Path $PSScriptRoot 'src\app.manifest')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',('/resource:'+$payload+',Payload.Node'),('/resource:'+(Join-Path $PSScriptRoot 'src\core.mjs')+',Payload.Core'),('/resource:'+(Join-Path $PSScriptRoot 'src\desktop-worker.mjs')+',Payload.Worker'),('/resource:'+$license+',Payload.License'),('/resource:'+$icon+',Payload.Icon'))+$sources
& $compiler @compilerArguments
if($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$checksum=Get-LowerHash $OutputPath
[IO.File]::WriteAllText(($OutputPath+'.sha256'),$checksum+'  '+[IO.Path]::GetFileName($OutputPath)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Write-Host ('Built '+$OutputPath)
Write-Host ('Node runtime '+$nodeVersion+'; SHA256 '+$checksum)
