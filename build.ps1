# 编译 PCLUnlock.exe（单文件，dnlib 内嵌）
# 依赖：Windows + .NET Framework 4.x 自带的 csc.exe，无需安装 SDK

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$cache = Join-Path $root '.cache'
$dnlib = Join-Path $cache 'dnlib.dll'

if (-not (Test-Path $dnlib)) {
    Write-Host '下载 dnlib 4.4.0 ...'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache 'dnlib.zip'
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/dnlib/4.4.0' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $cache 'dnlib') -Force
    Copy-Item (Join-Path $cache 'dnlib/lib/net35/dnlib.dll') $dnlib -Force
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "找不到 csc.exe: $csc（需要 .NET Framework 4.x）"
}

$out = Join-Path $root 'PCLUnlock.exe'
Write-Host '编译 PCLUnlock.exe ...'

$cscArgs = @(
    '/nologo'
    '/target:exe'
    "/out:$out"
    "/r:$dnlib"
    "/resource:$dnlib,dnlib.dll"
    (Join-Path $root 'src/PCLUnlock.cs')
)

& $csc @cscArgs

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

Write-Host "完成: $out"
