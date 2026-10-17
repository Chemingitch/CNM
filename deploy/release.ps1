# CNM 一键发布：构建 → 便携包 → 安装包 → git 提交打 tag → 传到 GitHub Release
#
#   .\deploy\release.ps1                        # 本地一条龙：构建 + 打包 + 提交 + 打 tag（不推）
#   .\deploy\release.ps1 -Push                  # 再推到 origin（要先加好 remote；装了 gh 会顺手上传 Release）
#   .\deploy\release.ps1 -Version 0.2.0 -Push   # 顺手改版本号（csproj + 安装包 .iss 一起改）
#   .\deploy\release.ps1 -SkipBuild -FullOnly   # 用现成产物，只打完整包 + 发布
#
# 产物（都在 dist\）：
#   CNM-Portable-<版本>.zip        完整便携版（含 Vulkan 引擎）
#   CNM-Portable-Lite-<版本>.zip   精简便携版（不含引擎，装完在界面里下）
#   CNM-Setup-<版本>.exe           Windows 安装包（需要本机有 Inno Setup）
param(
    [string]$Version = '',
    [string]$Notes = '',
    [switch]$SkipBuild,
    [switch]$FullOnly,
    [switch]$NoInstaller,
    [switch]$NoGit,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot                     # 仓库根 ...\ChemLLM
$dist = Join-Path $root 'dist'

function Step($t) { Write-Host ''; Write-Host ("== " + $t + " ==") -ForegroundColor Cyan }
function Run-Ps($file, $more = @()) { & powershell -NoProfile -ExecutionPolicy Bypass -File $file @more }
function RunGit { $out = & git @Args 2>&1; return $out }     # 注意别叫 Git：会跟外部 git.exe 撞名，自己递归自己

# ---------- 0. 版本号 ----------
$csproj = Join-Path $root 'CNM.csproj'
$iss = Join-Path $root 'installer\CNM.iss'
if (-not $Version) {
    $t = [IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8)
    $Version = if ($t -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '0.1.0' }
} else {
    Step "改版本号 → $Version"
    $t = [IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8)
    $t = [Text.RegularExpressions.Regex]::Replace($t, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    [IO.File]::WriteAllText($csproj, $t, (New-Object System.Text.UTF8Encoding($false)))
    $i = [IO.File]::ReadAllText($iss, [Text.Encoding]::UTF8)
    $i = [Text.RegularExpressions.Regex]::Replace($i, '#define\s+AppVer\s+"[^"]*"', "#define AppVer `"$Version`"")
    [IO.File]::WriteAllText($iss, $i, (New-Object System.Text.UTF8Encoding($true)))   # .iss 必须 UTF-8 BOM
    Write-Host "  CNM.csproj / installer\CNM.iss 都改成 $Version 了"
}

# 构建前先让 CNM 退出来，不然文件被占着
Get-Process CNM, llama-server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

# ---------- 1. 构建 ----------
$artifacts = @()
if (-not $SkipBuild) {
    Step '构建 CNM（自包含 → dist\CNM）'
    Run-Ps (Join-Path $root 'build.ps1')
}

# ---------- 2. 便携包 ----------
Step '打便携包（完整 + 精简）'
Run-Ps (Join-Path $PSScriptRoot 'portable.ps1') @('-SkipBuild', '-Version', $Version)
$artifacts += (Join-Path $dist "CNM-Portable-$Version.zip")
if (-not $FullOnly) {
    Run-Ps (Join-Path $PSScriptRoot 'portable.ps1') @('-SkipBuild', '-NoEngine', '-Version', $Version)
    $artifacts += (Join-Path $dist "CNM-Portable-Lite-$Version.zip")
}

# ---------- 3. 安装包 ----------
if (-not $NoInstaller) {
    Step '编译安装包（Inno Setup）'
    $setup = Join-Path $dist "CNM-Setup-$Version.exe"
    if (Test-Path $setup) { Remove-Item $setup -Force }   # 先删旧的：这次编不出来就别把上次的当新产物
    try {
        Run-Ps (Join-Path $root 'installer\build.ps1')
        if (Test-Path $setup) { $artifacts += $setup }
        else { Write-Host '  安装包没生成（检查 Inno Setup 是否装了）' -ForegroundColor Yellow }
    }
    catch {
        Write-Host ("  安装包编译出错：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

# ---------- 4. 校验和 + 汇总 ----------
Step '产物'
$sumFile = Join-Path $dist 'SHA256SUMS.txt'
$sumLines = @()
foreach ($a in $artifacts) {
    if (Test-Path $a) { $sumLines += ((Get-FileHash $a -Algorithm SHA256).Hash.ToLower() + '  ' + (Split-Path $a -Leaf)) }
}
[IO.File]::WriteAllLines($sumFile, $sumLines, (New-Object System.Text.UTF8Encoding($false)))
$artifacts += $sumFile
foreach ($a in $artifacts) {
    if (Test-Path $a) {
        $mb = [math]::Round((Get-Item $a).Length / 1MB, 1)
        $sha = (Get-FileHash $a -Algorithm SHA256).Hash.Substring(0, 16)
        Write-Host ("  {0,-36} {1,7} MB   sha256:{2}…" -f (Split-Path $a -Leaf), $mb, $sha)
    }
}

# ---------- 5. git ----------
$tag = "v$Version"
if (-not $NoGit) {
    Step 'git：提交 + 打标签'
    Push-Location $root
    try {
        if (-not (Test-Path (Join-Path $root '.git'))) {
            Write-Host '  还不是 git 仓库，先 git init（分支 main）'
            RunGit 'init' '-b' 'main' | Out-Null
        }
        # 行尾别被自动改写（否则每次 git status 都一堆噪音）
        RunGit 'config' 'core.autocrlf' 'false' | Out-Null
        # 身份：这台机器要是没配过 git 账号，就落一份仓库级默认（不动你的全局配置）
        # 没身份的话，打带注解的标签会直接失败（"empty ident name"），别踩
        if (-not ((RunGit 'config' 'user.name') -join '').Trim()) {
            RunGit 'config' 'user.name' 'Chemingitch' | Out-Null
            Write-Host '  本机没配 git 身份，先设成仓库级的 Chemingitch（要改随时改）'
        }
        if (-not ((RunGit 'config' 'user.email') -join '').Trim()) {
            RunGit 'config' 'user.email' 'Chemingitch@users.noreply.github.com' | Out-Null
        }
        RunGit 'add' '-A' | Out-Null
        $dirty = (RunGit 'status' '--porcelain' | Measure-Object).Count
        if ($dirty -gt 0) {
            $msg = if ($Notes) { $Notes } else { "release $tag" }
            RunGit 'commit' '-m' $msg | Out-Null
            Write-Host ("  已提交 {0} 处改动：{1}" -f $dirty, $msg)
        } else {
            Write-Host '  没有要提交的改动'
        }
        if ((RunGit 'tag' '-l' $tag | Measure-Object).Count -gt 0) {
            Write-Host "  标签 $tag 已经存在，跳过（要挪就手动 git tag -f）"
        } else {
            RunGit 'tag' '-a' $tag '-m' "CNM $tag" | Out-Null
            Write-Host "  打了标签 $tag"
        }
    }
    finally { Pop-Location }
}

# ---------- 6. 推送 / 发 Release ----------
$remote = ''
Push-Location $root
try { $remote = (RunGit 'remote' 'get-url' 'origin' | Out-String).Trim() } catch { }
Pop-Location

if ($Push -and $remote) {
    Step '推送到 origin'
    Push-Location $root
    try {
        RunGit 'push' '-u' 'origin' 'main'
        RunGit 'push' 'origin' $tag
    }
    finally { Pop-Location }
}

Step 'GitHub Release'
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh -and $remote) {
    $files = $artifacts | Where-Object { Test-Path $_ }
    $notes = if ($Notes) { $Notes } else { "CNM $tag —— 本机 GGUF + 云端中转，一条 OpenAI 兼容服务" }
    & gh release create $tag --title "CNM $tag" --notes $notes @files
}
else {
    Write-Host '  没检测到 gh（GitHub CLI），或者还没配 remote。两条路：'
    Write-Host '    A) 装一个 gh： winget install GitHub.cli   然后  gh auth login  再跑一次本脚本 -Push'
    Write-Host '    B) 手动：浏览器打开仓库页面 → Releases → Draft a new release，Tag 填 ' -NoNewline
    Write-Host $tag -NoNewline
    Write-Host '，把上面对应的文件拖进去'
    if ($remote) {
        $url = $remote -replace 'git@github.com:', 'https://github.com/' -replace '\.git$', ''
        Write-Host ("       你的仓库：{0}/releases/new" -f $url)
    }
    Write-Host ''
    Write-Host '  要上传的文件（Release 资产，别直接 commit —— 单个超 100 MB GitHub 会拒）：'
    foreach ($a in $artifacts) { if (Test-Path $a) { Write-Host ("    " + $a) } }
}
Write-Host ''
Write-Host '完了。' -ForegroundColor Green
exit 0
