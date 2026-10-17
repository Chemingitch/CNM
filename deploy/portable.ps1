# CNM 便携版（免安装）打包 —— 解压出来是一个 CNM 文件夹，双击「启动 CNM.cmd」就能用
#
#   .\deploy\portable.ps1                  → dist\CNM-Portable-<版本>.zip（完整，含 Vulkan 引擎）
#   .\deploy\portable.ps1 -NoEngine        → dist\CNM-Portable-Lite-<版本>.zip（不含引擎，小 80 MB 左右）
#   .\deploy\portable.ps1 -SkipBuild       → 不重新构建，直接打现在 dist\CNM 里的东西
#
# 说明：234 MB 一档的包不适合直接 git 提交（GitHub 单文件 100 MB 限制），请走 Release 资产。
param(
    [switch]$NoEngine,
    [switch]$SkipBuild,
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot                     # 仓库根 ...\ChemLLM
$dist = Join-Path $root 'dist\CNM'
$outDir = Join-Path $root 'dist'
$payload = Join-Path $PSScriptRoot 'payload'

if (-not $Version) {
    $t = [IO.File]::ReadAllText((Join-Path $root 'CNM.csproj'), [Text.Encoding]::UTF8)
    $Version = if ($t -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '0.0.0' }
}

# 打包前先让 CNM 退出，不然文件被占着
Get-Process CNM, llama-server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

if (-not $SkipBuild) {
    Write-Host '== 构建 CNM（自包含发布 → dist\CNM）==' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
}
if (-not (Test-Path (Join-Path $dist 'CNM.exe'))) { throw "没找到 $dist\CNM.exe —— 先跑一次 build.ps1" }

$name = if ($NoEngine) { "CNM-Portable-Lite-$Version" } else { "CNM-Portable-$Version" }
$zip = Join-Path $outDir "$name.zip"
$stage = Join-Path $env:TEMP ('cnm-portable-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$stageRoot = Join-Path $stage 'CNM'
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

try {
    # robocopy 比 Copy-Item 快得多；-NoEngine 就把 engine 目录排除掉
    $rcArgs = @($dist, $stageRoot, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
    if ($NoEngine) { $rcArgs += @('/XD', (Join-Path $dist 'engine')) }
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "复制文件失败（robocopy exit $LASTEXITCODE）" }

    Copy-Item (Join-Path $payload '启动 CNM.cmd') $stageRoot -Force
    Copy-Item (Join-Path $payload 'start.ps1') $stageRoot -Force
    # 使用说明一起放进去（安装包那边也是这么干的）
    if (Test-Path (Join-Path $root 'README.md')) { Copy-Item (Join-Path $root 'README.md') $stageRoot -Force }
    if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $stageRoot -Force }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Push-Location $stage
    & tar -a -c -f $zip CNM
    $tar = $LASTEXITCODE
    Pop-Location
    if ($tar -ne 0) { throw "打 zip 失败（tar exit $tar）" }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("便携包好了：{0}" -f $zip) -ForegroundColor Green
Write-Host ("  {0} MB{1}；解压出来是 CNM 文件夹，双击「启动 CNM.cmd」或 CNM.exe" -f $mb, $(if ($NoEngine) { '（不含引擎）' } else { '' }))
Write-Host ("  SHA256 {0}" -f $sha)
