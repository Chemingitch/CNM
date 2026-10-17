# CNM 一键构建
#   .\build.ps1                     → 发布【自包含】版到 dist\CNM（自带 .NET 运行时，换机器直接跑）
#   .\build.ps1 -FrameworkDependent → 发布精简版（依赖本机已装 .NET 8 Desktop 运行时）
#
# 说明：本机 dotnet 装在 E:\C\CTools\Windowstools\dotnet（非标准路径），
#       所以自包含版才是"双击就能跑"的那个。

param([switch]$FrameworkDependent)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\CNM"

# 找 dotnet（不用 ?. 语法，兼容 Windows PowerShell 5.1）
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
if (-not $dotnet) { $dotnet = "E:\C\CTools\Windowstools\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "找不到 dotnet，请装 .NET 8 SDK（或改本脚本里的路径）" }

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
Write-Host "== 发布 CNM (self-contained=$selfContained) ==" -ForegroundColor Cyan

# exe 图标：没有就先构建一次，用程序自己生成 app.ico（界面、托盘、exe 图标同一份画法），再正式发布
$ico = Join-Path $root "app.ico"
if (-not (Test-Path $ico)) {
    Write-Host "== 生成应用图标 app.ico ==" -ForegroundColor Cyan
    & $dotnet build (Join-Path $root "CNM.csproj") -c Release -v q --nologo | Out-Null
    $tmpExe = Join-Path $root "bin\Release\net8.0-windows\CNM.exe"
    if (Test-Path $tmpExe) {
        Start-Process -FilePath $tmpExe -ArgumentList "--make-icon `"$ico`"" -Wait -NoNewWindow
    } else {
        Write-Host "提示: 跳过了图标生成（找不到临时 exe），exe 会用默认图标" -ForegroundColor Yellow
    }
}

# 你在「设置 → 外观与图标」里换过图标的话，打包出来的 exe 也用那张
$userIco = Join-Path $env:APPDATA "CNM\brand\icon.ico"
if (Test-Path $userIco) {
    Write-Host "== 使用自定义图标 ==" -ForegroundColor Cyan
    Copy-Item $userIco $ico -Force
}

$env:DOTNET_CLI_UI_LANGUAGE = "en"
$args = @(
    "publish", (Join-Path $root "CNM.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", $selfContained,
    "-p:PublishSingleFile=false",
    "-o", $out
)
& $dotnet @args
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

# 引擎：把工程里的 engine\ 一起带上（已经解压过 llama.cpp Vulkan 版）
$srcEngine = Join-Path $root "engine\llama"
$dstEngine = Join-Path $out "engine\llama"
if ((Test-Path $srcEngine) -and -not (Test-Path (Join-Path $dstEngine "llama-server.exe"))) {
    Write-Host "== 复制推理引擎 ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $dstEngine | Out-Null
    Copy-Item "$srcEngine\*" $dstEngine -Recurse -Force
}

$exe = Join-Path $out "CNM.exe"
$size = (Get-ChildItem $out -Recurse -File | Measure-Object -Sum Length).Sum / 1MB

Write-Host ""
Write-Host "完成 → $exe" -ForegroundColor Green
Write-Host ("目录体积: {0:0} MB" -f $size)
if (-not (Test-Path (Join-Path $out "engine\llama\llama-server.exe"))) {
    Write-Host "提示: 没找到推理引擎，首次运行程序里点【设置 → 下载并安装引擎】即可。" -ForegroundColor Yellow
}
