# CNM 便携版启动器（由「启动 CNM.cmd」调用）
#   1. 看这个目录里有没有 CNM.exe
#   2. 看有没有带推理引擎（没带也能用：界面里能一键下载）
#   3. 看系统有没有微软 WebView2 运行时（老系统 / LTSC 常缺）
#   4. 启动 CNM
$ErrorActionPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

function Say($t) { Write-Host $t }
function Pause-Exit($code) { Write-Host ''; Read-Host '按回车键退出'; exit $code }

if (-not (Test-Path (Join-Path $here 'CNM.exe'))) {
    Say '× 这个目录里没找到 CNM.exe。'
    Say '  请把这个脚本放回解压出来的 CNM 文件夹里再运行（解压出来应该有一个 CNM 文件夹）。'
    Pause-Exit 1
}

if (-not (Test-Path (Join-Path $here 'engine\llama\llama-server.exe'))) {
    Say '提示：这个包里没带推理引擎（省了 80 MB）。CNM 打开后到「设置 → 推理引擎（llama.cpp）」'
    Say '      点「下载并安装引擎」就行 —— 会按你的显卡推荐后端，NVIDIA 走 CUDA、AMD 走 Vulkan/ROCm、'
    Say '      没有独显就走 CPU，慢网断了都能接着下。'
    Say ''
}

$sub = '\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$pv = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node$sub").pv
if (-not $pv) { $pv = (Get-ItemProperty "HKCU:\SOFTWARE$sub").pv }
if (-not $pv -or $pv -eq '0.0.0.0') {
    Say '提示：这台机器上没检测到「微软 WebView2 运行时」，CNM 的界面需要它。'
    Say '      装一下很小（约 1.6 MB）：https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    Say '      Win11 和较新的 Win10 一般都自带，也可以先去启动看看。'
    Say ''
}

Say '正在启动 CNM ……'
Start-Process -FilePath (Join-Path $here 'CNM.exe')
Say ''
Say '服务地址：http://127.0.0.1:1234/v1'
Say '  · 别的设备要用，把 127.0.0.1 换成这台电脑的局域网 IP（界面里显示的就是）'
Say '  · API Key 在「设置 → 服务」里看'
Say '  · 想让它开机就跑 / 关窗不退出：设置里的「开机自动开服务」「关窗收进托盘」'
Start-Sleep -Seconds 4
