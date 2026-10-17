# 编译 CNM 安装包（Inno Setup）
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -InnoDir "C:\Program Files (x86)\Inno Setup 6"
# 找 ISCC 的顺序：-InnoDir → 本机汉化版（默认）→ PATH 里的 ISCC → 常见安装目录
param([string]$InnoDir = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path          # ...\ChemLLM\installer
$proj = Split-Path -Parent $root                                  # ...\ChemLLM
$iss  = Join-Path $root 'CNM.iss'
$log  = Join-Path $env:TEMP 'opencode\iscc-build.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null

function Find-Iscc {
    param([string]$Hint)
    $tries = New-Object System.Collections.ArrayList
    if ($Hint) { [void]$tries.Add((Join-Path $Hint 'ISCC.exe')) }
    [void]$tries.Add('E:\C\CTools\Windowstools\Inno Setup 7.1.0 汉化版\ISCC.exe')
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { [void]$tries.Add($cmd.Source) }
    foreach ($p in @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                     "$env:ProgramFiles(x86)\Inno Setup 7\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe")) {
        [void]$tries.Add($p)
    }
    foreach ($t in $tries) { if ($t -and (Test-Path $t)) { return $t } }
    return $null
}

$iscc = Find-Iscc -Hint $InnoDir
if (-not $iscc) {
    Write-Host '找不到 ISCC.exe（Inno Setup 编译器）。' -ForegroundColor Yellow
    Write-Host '  装一个 Inno Setup，或用 -InnoDir 指定；本机汉化版在：'
    Write-Host '  E:\C\CTools\Windowstools\Inno Setup 7.1.0 汉化版'
    exit 2
}
Write-Host "== 用编译器：$iscc ==" -ForegroundColor Cyan

# .iss 存成 UTF-8 BOM（Inno 编译器靠 BOM 判断编码，不然中文会乱）
$txt = [System.IO.File]::ReadAllText($iss, [Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($iss, $txt, (New-Object System.Text.UTF8Encoding($true)))

# 版本号从 .iss 里取（#define AppVer "0.1.0"）
$ver = '0.1.0'
if ($txt -match '#define\s+AppVer\s+"([^"]+)"') { $ver = $Matches[1] }

$sw = [Diagnostics.Stopwatch]::StartNew()
cmd /c "`"$iscc`" `"$iss`" > `"$log`" 2>&1"
$code = $LASTEXITCODE
$sw.Stop()

# ISCC 输出是 UTF-8，转存一份 UTF-8，方便工具直接看中文报错
$bytes = [IO.File]::ReadAllBytes($log)
[IO.File]::WriteAllText($log, [Text.Encoding]::UTF8.GetString($bytes), (New-Object Text.UTF8Encoding($false)))

Write-Host ("exit={0}  耗时 {1:N0} 秒" -f $code, $sw.Elapsed.TotalSeconds)
$lines = Get-Content $log
$lines | Where-Object { $_ -match 'Error on line|Compile aborted|Successful compile|警告|Warning' } | Select-Object -First 12 | ForEach-Object { Write-Host $_ }
if ($code -eq 0) {
    $setup = Join-Path $proj ("dist\CNM-Setup-{0}.exe" -f $ver)
    if (Test-Path $setup) {
        $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
        Write-Host ("产物：{0}  ({1} MB)" -f $setup, $mb)
    }
}
exit $code
