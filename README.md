# CNM

把本机的 GGUF 模型变成一条 **OpenAI 兼容的局域网服务**（这层对外接口叫 **哨兵**），
OpenCode、Cherry Studio、任何 OpenAI SDK 客户端都能直接接；
顺带能在线浏览 / 下载模型，界面是深色小窗，关窗收进托盘也不断服务。

> 参考了 LM Studio 的能力（本机 `Bionic.exe` 就是 LM Studio 的换名版），但**没有搬它的代码** ——
> 它是专有软件。这里复刻的是公开技术栈：llama.cpp（MIT，自带 OpenAI 兼容 server）+ HuggingFace 公开 API + GGUF 公开格式。

---

## 下载与安装

三种包，按需要挑一个（都在 Releases 页面）：

| 包 | 大约大小 | 说明 |
| --- | --- | --- |
| `CNM-Setup-<版本>.exe` | 63 MB | Windows 安装包：中文向导、装到 `Program Files\CNM`、桌面快捷方式、防火墙放行 1234，还能选「装不装推理引擎」。**普通用户推荐这个** |
| `CNM-Portable-<版本>.zip` | 93 MB | 免安装便携版，**带**推理引擎（Vulkan 版）：解压 → 双击 `启动 CNM.cmd`（或直接双击 `CNM.exe`）就能用 |
| `CNM-Portable-Lite-<版本>.zip` | 62 MB | 精简便携版，**不带引擎**：初次打开后到「设置 → 推理引擎（llama.cpp）」点「下载并安装引擎」，它会按你的显卡推荐后端 |

三个包都**不用**装 .NET（自带运行时）、不用 Python、不用 Ollama / LM Studio。
引擎要是没带，程序里能一键下载，而且带**镜像加速 + 断点续传 + 装本地 zip**（慢网、断线都不用从头再来）。

> 模型是几个 GB 起步的大文件，所以**不放进任何安装包**——第一次用去「模型库 → 在线浏览」下一个，
> 或者把已有的 GGUF 丢进模型目录。

### 便携版怎么「一键部署」

1. 解压 zip，得到一个 `CNM` 文件夹（放哪都行，别太深）
2. 双击 `启动 CNM.cmd`：顺手检查 WebView2 运行时、提醒有没有带引擎，然后启动 CNM
3. 界面里：模型库挑一个「加载」→ 右上角「启动服务」→ 别的程序填 `http://127.0.0.1:1234/v1`
4. 局域网里其它设备也要用：设置里打开「局域网可访问」，把地址里的 `127.0.0.1` 换成这台机器的 IP

## 一分钟上手

1. 双击 `dist\CNM\CNM.exe`（自包含版，自带运行时，不用装 .NET）。
2. 首次打开会自动扫描模型目录 —— 默认就是你这套：`E:\C\CTools\AI Make\LLM\models`
   （沿用 LM Studio 的 `<作者>\<仓库>\xxx.gguf` 布局，**两个程序可以共用同一个目录，互不干扰**）。
3. 「模型库」里挑一个 → 点 **加载**；或者什么都不用做，等 agent 发请求时自动加载（设置里默认开着）。
   这就是**按需打开**：没人调用就不占显存，第一次请求时才把模型读进显卡；
   默认**空闲 10 分钟没人调用就自动卸载**、把显存还回去（设置 → 服务 → 空闲自动卸载，可改可关）。
4. 「服务与日志 → 哨兵」里把 **Base URL** 和 **API Key** 复制出去：

```
Base URL : http://127.0.0.1:1234/v1        ← 本机 agent 用这个
           http://<你的局域网IP>:1234/v1    ← 手机/别的电脑用这个（默认已开局域网）
API Key  : 程序里显示的那串 sk-chem-…（可以在设置里换）
模型名    : /v1/models 列出的 Id，例如 Bonsai-27B-Q1_0
```

**不需要额外装任何东西**：CNM 自带推理引擎（`engine\llama\llama-server.exe`，Vulkan 版），
**不依赖 Ollama / LM Studio / Python**。别的程序只要会说 OpenAI 协议（Base URL + key）就能连上，
模型文件（GGUF）也能在「在线浏览」里直接下 —— 所以它是「装上就能用」，不是「还得先搭个环境」。

> **换台电脑**：把整个 `dist\CNM\` 文件夹拷过去（234 MB，`CNM.exe` 旁边那堆 `.dll` 和 `engine\` 都得带上，
> 别只拷一个 exe），双击 `CNM.exe` 就能跑 —— .NET 运行时是**自带**的。
> 唯一可能缺的是系统的 **WebView2 运行时**（Win11 和带 Edge 的 Win10 都有；真没有的话装一次微软的
> Evergreen Runtime 即可）。模型文件是单独下的大文件，跟着 `models` 目录一起拷就行。

> 顺手：浏览器里直接打开 `http://127.0.0.1:1234` 会看到一页说明（Base URL、接口清单、curl 例子、
> key 在哪看、当前模型和显存）——那是给**人**看的，程序要调 API 还是用上面的 Base URL。

### 接到 OpenCode 里

`opencode.json`：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "cnm": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "CNM (本地)",
      "options": {
        "baseURL": "http://127.0.0.1:1234/v1",
        "apiKey": "sk-chem-把程序里那串粘过来"
      },
      "models": {
        "Bonsai-27B-Q1_0": { "name": "Bonsai 27B" },
        "QwenPaw-Flash-9B-heretic-MTP-Q4_K_M": { "name": "QwenPaw 9B" }
      }
    }
  }
}
```

`models` 里的键就是 `/v1/models` 返回的 Id；agent 请求哪个 Id，CNM 就自动切到哪个模型（可以关掉这个行为）。

---

## 支持的能力

| 能力 | 说明 |
|---|---|
| OpenAI 兼容转发 | `/v1/models`、`/v1/chat/completions`（含 SSE 流式）、`/v1/completions`、`/v1/embeddings` 等，路径透传 |
| 多模型热切换 | 请求里的 `model` 换一个，自动卸载旧的、加载新的（默认开） |
| 局域网开放 | 监听 `0.0.0.0`，带 Bearer API Key 校验：**推理接口要 key**，**模型列表默认也要**（设置 → 服务 → 「模型列表也要 Key」可关）；只有 `/health` 和首页说明免 key（探活、监控用） |
| 兼容前缀 | 除标准 `/v1/*`，也接受 `/api/v1/*`、以及 LM Studio 风格的 `/api/v0/models` —— 客户端 base URL 带不带 `/api` 都能直接用 |
| 浏览器说明页 | 打开服务根路径给一页说明（Base URL、可用接口、curl 例子、key 在哪）；页签还有程序图标作 favicon |
| 显存实测 | 用 Windows 性能计数器读引擎真正占的显存（和任务管理器同源），「哨兵」和 `/health` 都能看到 |
| 探活 | `GET /health`（免 key）返回模型名 / 显存 MiB / 空闲秒数 / 自动卸载分钟数 / 引擎日志路径，方便接监控 |
| 模型库 | 分**本地模型 / 外地模型**两块（默认本地），两块的工具栏**各管各的**：本地那页有**量化**筛选 + 体积排序 + 重新扫描 / 打开目录；外地那页没有量化这回事，只按「**厂家**」（谁家的：Anthropic / OpenAI / 谷歌 / 阿里 / 智谱…）筛，外加「最近用过」排序。递归扫描 GGUF，读元数据（架构 / 原生上下文 / 量化 / 是否带视觉编码器 / 是否有 chat 模板）；外地那块把上游的云端模型也做成一样的卡片，点「接入」就当默认，点名字点哪个用哪个。**用量**钉在这一页最下方常驻，模型卡片在中间自己滚，互不挤 |
| 视觉模型 | 同目录的 `mmproj-*.gguf` 自动配对，加载时带 `--mmproj` |
| 工具调用 | 启动参数带 `--jinja`，用模型自带 chat 模板，agent 的 function calling 才走得通 |
| 在线浏览下载 | 走 `hf-mirror.com`（HF 国内镜像），可按热门/最新/好评排序，看文件大小，勾选下载；支持断点续传。每条左边一块「谁家的」：**真 logo + 中文名**（Qwen / DeepSeek / OpenAI / Claude / 谷歌 / Meta / 智谱 / 月之暗面…走 simple-icons 官方标；随机作者取 HF 真头像；都取不到才画首字母），过滤框里直接敲「阿里」「Meta」「千问」也能筛 |
| 自定义网址 | 贴 HF 仓库页 → 列文件；贴任意 `.gguf` 直链 → 直接下 |
| 引擎自助安装 | 设置页一键下载 llama.cpp 官方构建（Vulkan / CPU / ROCm / CUDA 12.4 / 13.4）并解压到位，装完自动重新检测设备 |
| 引擎下载加速 | 国内直连 GitHub 常常只有 ~100 KB/s，所以：① 可填**加速前缀**（镜像优先、失败自动回落直连）；② 支持**断点续传**——慢网/断线不用从零再来，进度存在 `%TEMP%\cnm-engine-<后端>.zip.part`，再点一次接着下，换镜像也接着下；③ 「**选择本地 zip 安装**」完全绕开网络（用浏览器/下载器下好包直接装） |
| 托盘常驻 | 关窗不退出、服务不断；托盘可启停服务、退出 |
| 主题 | 深色 / 浅色点一下立刻切换，**标题栏、窗口底色、网页一起变**（选项按钮 ◐ 也能切） |
| 自定义图标 | 设置 → 外观与图标：传一张 PNG / JPG / ICO（正方形最好），侧栏 logo、窗口图标、托盘图标立刻换；打包时 exe 图标也用这张 |
| 请求日志 | 谁（来源 IP）、什么时候、要了哪个模型、状态码、耗时、返回字节数；模型列表 / 探活这类探测请求**成功时不刷屏**，失败(4xx/5xx)照常显示 |
| 用量统计 | 模型库最下方常驻一块：**日 / 月 / 年** 切换，看调用次数、本地 vs 外地、token（输入/输出）、平均耗时、回传字节，柱状图区分本地/外地；只记数字不记对话内容，落在 `%APPDATA%\CNM\usage.json`，可随时删 |
| 云端上游（守望者） | 「服务与日志」右侧把网上买的 OpenAI 兼容中转站接进来（左侧是**哨兵**——别人连进来的门，右侧是**守望者**——连你买的那些云，只填地址 + Key）。云端模型在**「模型库 → 外地模型」**里和本地模型长得一样（左边也是真 logo + 中文名：`gpt-*` → OpenAI、`claude-*` → Claude、`gemini-*` → Gemini），点「接入」就当默认；`/v1/models` 里云端模型照样列出来（`owned_by = cnm-upstream:<名字>`），agent 不用改配置就能用 |

---

## 目录结构

```
CNM\
├─ CNM.exe            主程序（WinForms 宿主 + WebView2 渲染界面）
├─ wwwroot\               界面（HTML/CSS/JS，纯静态，随手改）
├─ engine\llama\          推理引擎 llama-server.exe（Vulkan 版，约 90 MB）
├─ build.ps1              一键构建（默认出自包含版）
└─ dist\CNM\          构建产物（可直接拷走）
配置和缓存：%APPDATA%\CNM\    ← settings.json / gguf-cache.json / webview2 用户数据
```

## 默认推理参数

| 参数 | 默认 | 在哪改 |
|---|---|---|
| 上下文长度 `-c` | 8192 | 设置 → 推理参数（模型本身可能支持 262144，按显存和需要调） |
| GPU 层数 `-ngl` | 99（全丢显卡） | 设置 → 推理参数 |
| 并行槽位 `--parallel` | 2 | 设置 → 推理参数 |
| Flash Attention | 开 | 设置 → 推理参数 |
| CPU 线程 `-t` | 自动 | 设置 → 推理参数 |

引擎命令行（可在「服务与日志」里看到实际发出的命令）：

```
llama-server.exe -m <模型.gguf> --host 127.0.0.1 --port <内部端口> -c 8192 -ngl 99 \
                 --parallel 2 --alias <模型Id> --jinja --metrics --no-webui [--mmproj <视觉编码器>]
```

## 云端上游（API 中转站）

界面在「服务与日志」右侧的**守望者**（左边是**哨兵** —— 别人连进来的门）。

1. 「+ 添加上游」→ 填中转站给的 Base URL（例 `https://api.xxx.com/v1`）和它的 Key；**代理那格默认 `auto`**；
2. `auto` = **自动检测**：CNM 并发去试「直连 → 跟随系统代理 → 本机常见代理端口（7890 / 7897 / 10809 / 10808 / 1080 / 2080 / 8889 / 8118 …）」，谁先通用谁，并**记住它**（卡片上会写「走 http://127.0.0.1:7890」）。也可以点卡片上的「**自动检测**」按钮看每个候选的成败；
   > 不是所有 API 都非得走代理 —— 是**你买的那家域名在国内直连被卡**（超时 / 连不上）。换线路、换域名可能直连就行，所以别写死，交给它自己试。
3. 要写死也行：`direct` / 留空 = 只直连（**不会偷偷用系统代理**），`system` = 跟随系统代理，`http://127.0.0.1:7890`、`socks5://127.0.0.1:1080` = 指定；
4. 点「测试」= 拉它的模型列表，能列出来就是通了；「试一句」= 真发一条对话过去，并把**实际地址和回应**打在卡片上（排查路径 / 鉴权 / 代理最直观）；
5. 「保存上游」写盘并重连。

拉到的云端模型在 **「模型库 → 外地模型」**（模型库左上角那块切换），卡片和本地模型长得一样：

- 点「**接入**」= 把它设成**兜底模型**；再点一次「取消接入」；
- 「试一句」单独试这个模型通不通；「复制名字」方便粘到 agent 配置里；
- 卡片上「用过」的小标 = 请求日志里出现过，一眼知道自己用过哪些；
- 顶上的**厂家**下拉按「谁家的」筛（Anthropic / OpenAI / 谷歌 / 阿里 / 智谱…后面带数量），也可以按「最近用过」排 —— 外地模型没有量化这回事，所以这一页不摆量化框。

`model` 字段到底给谁（路由顺序）：

1. 名字在**本地库**里 → 走本地引擎（**本地优先**，不会被云端截走）；
2. 名字命中**某个上游的模型表** → 原样转给它（**点名哪个用哪个**，按需使用）；
3. 都命中不了、又「接入」了某个外地模型 → 转给接入的那个（兜底，写了 `gpt-4o` 之类也不会报错）；
4. 都没有 → 按原样报错（不会偷偷扣你的额度）。

命令行自测（不用开界面）：

```powershell
# 看通不通、有哪些模型
CNM.exe --upstream-test https://api.831800.xyz/v1 sk-xxx --proxy http://127.0.0.1:7890

# 真发一条对话，并把实际 URL / HTTP 状态 / 回应打出来
CNM.exe --upstream-chat https://api.831800.xyz/v1 sk-xxx claude-sonnet-5-5-low --proxy http://127.0.0.1:7890
```

> 实测：不少中转站国内直连是超时的，填上本机代理就通（`--proxy auto` 可以让它自己试）；CNM 转发是**按块边收边发**的，`stream:true` 的 SSE 原样透传。
> 日志：界面「引擎日志」面板右上角「日志文件」= `%APPDATA%\CNM\logs\cnm.log`，上游失败会把**实际地址 + 发过去的 body** 记进去（这次那个 404 就是靠它一眼看出来的：URL 少了个斜杠）。

## 用量（日 / 月 / 年）

「模型库」最下方常驻一块**用量**（本地模型 / 外地模型两页都在），中上那块模型卡片自己滚，互不挤：

- **日 / 月 / 年** 三挡：日 = 最近 7 天，月 = 最近 12 个月，年 = 最近 5 年；柱状图**下面一段是本地、上面一段是外地**；
- 一行大字：该挡的调用次数、本地 / 外地拆分、token 输入 / 输出、失败次数、平均耗时、回传字节；下面小字是**累计**；
- 只统计**真干活儿的请求**（`/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`、`/v1/responses`）——模型列表、探活这类探测不算，401 也不算；
- token 从响应里的 `usage` 认（OpenAI 的 `prompt_tokens` / `completion_tokens`、Anthropic 风格的 `input_tokens` / `output_tokens` 都认；流式取响应尾巴里那段）；
- 数字落在 `%APPDATA%\CNM\usage.json`（一天一条，只记数字不记对话内容），删了就从零开始，不影响服务。

## 「谁家的」（真 logo + 中文名）

模型列表 / 在线浏览每一行、每张卡左边都有一块厂家标，用来一眼认出这是哪家的模型：

- **真 logo**：走 [simple-icons](https://simpleicons.org) 的官方品牌 SVG（jsdelivr 直连，**不用翻墙**）——Qwen、DeepSeek、OpenAI、Claude、谷歌、Meta、微软、Kimi、MiniMax、英伟达…都有；
- **随机作者**（unsloth、bartowski、TheBloke 这类量化搬运工）取它在 HF 上的**真头像**：先用 hf-mirror 问地址，图直连不通就自动借本机代理（和上游那套「自动检测」一样，直连 → 系统代理 → 7890/7897/10809…）；
- 图下面固定一行**中文名**（千问 / 智谱 / 月之暗面 / Claude / OpenAI…），认不出的挂**作者名**，再不行才是首字母色块；
- 取回来的图缓存在 `%APPDATA%\CNM\logos\`（一个键一个 txt，存的就是 data URL），**第二次开程序不再联网**；想让它重新取就把这个目录删了。

认厂家靠「作者名 + 模型名字特征」（`deepseek-ai/…`、`…Qwen3…`、`…gemma…`、`claude-…`）。想加一家：改 `wwwroot\logos.js` 里的 `LABS` 表加一行就行（中文名 `cn` / 色块 `mark` / 底色 `bg` / 作者 `who` / 名字特征 `re` / 图标 `icon` / HF 组织 `hf`）。



## 推理引擎（llama.cpp）

引擎不进仓库（80+ MB 的二进制），也不一定非用包里那份：**界面「设置 → 推理引擎（llama.cpp）」能一键下载 / 换后端 / 查更新**。
它会读注册表认你的显卡并给出建议（本机 7900 XTX 会被建议 Vulkan）：

- **Vulkan**：A 卡 / I 卡 / N 卡通吃，包里默认带的就是它
- **CUDA 13.4 / 12.4**：NVIDIA，通常最快；会自动把配套的 `cudart` 运行库一起装上（少了它 CUDA 版起不来）
- **ROCm 10.0**：AMD（Windows 上驱动要够新）
- **CPU**：没有独显时的兜底，慢但一定能跑

「检查更新」会去 llama.cpp 的 release 列表比对版本号（本机 b11711 → 官方 b11714，一眼看出有没有新版）。
慢网、断线都不怕：进度落在 `%TEMP%\cnm-engine-<后端>.zip.part`，再点一次就**接着下**，换镜像也接着下。

命令行是同一套逻辑（装完就退，适合脚本 / CI）：

```powershell
# 装 / 升级引擎（默认 Vulkan；纯净模式，装完就退）
CNM.exe --install-engine vulkan

# 走加速前缀装（镜像优先，不行自动回落直连）
CNM.exe --install-engine vulkan --engine-mirror https://gh-proxy.com/

# 用已经下好的包装（网再慢都不怕）
CNM.exe --install-engine-zip D:\下载\llama-b11712-bin-win-vulkan-x64.zip

# 装到别的地方（默认装到 exe 同级的 engine\llama\）
CNM.exe --install-engine cpu --engine-dir D:\CNM-engine
```

其它自检/工具开关：`--make-icon`、`--logo-sheet`、`--set-icon <图>`、`--clear-icon`、`--menu-preview <png> [--light]`。

## 已知边界

- **只跑 GGUF**。safetensors / PyTorch 权重不在范围内（要转换得另上 llama.cpp 的 convert 工具 + Python 环境）。
- 一次**只加载一个模型**（同一时刻一份显存占用），换模型会重启引擎进程。
- 上下文、量化、模型能不能跑动，取决于显卡；本机 7900 XTX（24G）实测 27B-Q1_0 约 60 tok/s。
- 模型目录里的文件是**只读复用**：CNM 只扫描、只新建（下载到 `<作者>\<仓库>\`），不会改动 LM Studio 已有的文件。
- 防火墙：第一次监听局域网时 Windows 可能弹窗，点“允许”即可（或在防火墙里放行 1234 端口）。

## 从源码构建

```powershell
cd "E:\C\CTools\AI Make\LLM\ChemLLM"
.\build.ps1                      # 自包含版 → dist\CNM（自带 .NET 运行时，换机器直接跑）
.\build.ps1 -FrameworkDependent  # 精简版（依赖本机已装 .NET 8 Desktop 运行时）
```

需要 .NET 8 SDK（本机在 `E:\C\CTools\Windowstools\dotnet`）、WebView2 运行时（Win11 自带）。
代码结构：`Core\` 后端（HTTP 监听 / 转发、引擎进程、模型库、设置），`UI\` 窗口 + 托盘，
`wwwroot\` 界面（原生 JS，零第三方库），`installer\` 安装包，`deploy\` 打包与发布。

## 一键打包 / 发布

```powershell
# 便携包（免安装）
.\deploy\portable.ps1              # dist\CNM-Portable-<版本>.zip（带 Vulkan 引擎）
.\deploy\portable.ps1 -NoEngine    # dist\CNM-Portable-Lite-<版本>.zip（不带引擎）

# 安装包（要 Inno Setup；本机汉化版在 E:\C\CTools\Windowstools\Inno Setup 7.1.0 汉化版）
.\installer\build.ps1

# 一条龙：构建 → 便携包 → 安装包 → git 提交打 tag →（装了 gh 就）发 GitHub Release
.\deploy\release.ps1 -Version 0.2.0 -Push
```

推一个 `v*` 标签到 GitHub，`.github\workflows\release.yml` 会在 windows-latest 上自动构建、
现装引擎、打两个便携包、编译安装包并发 Release（CI 上装的是官方版 Inno，编不出来也不影响便携包）。

> 90 MB 级的包**别直接 commit**（GitHub 单文件 100 MB 限制），走 Release 资产。

## 协议

MIT，见 `LICENSE`。引擎是 llama.cpp（MIT）；界面自己写，零第三方 JS 库。
