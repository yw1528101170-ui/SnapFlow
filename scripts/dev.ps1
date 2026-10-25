#Requires -Version 5.1
# 注意：本文件必须存成「UTF-8 with BOM」。Windows PowerShell 5.1 读无 BOM 的 UTF-8 会按 GBK 解码，
# 中文会把紧随其后的引号吞掉，整份脚本就报「字符串缺少终止符」。改完确认编辑器没把编码改回去。
<#
.SYNOPSIS
    SnapFlow 开发热重载循环：保存文件 → 自动构建 → 自动优雅重启。

.DESCRIPTION
    WinUI 3 在没装 Visual Studio 的机器上拿不到 XAML 热重载，而进程运行时会锁住
    bin 目录里的 exe/dll，导致“必须先手动关掉程序才能构建”。这个脚本把整条链路自动化：

        改动检测 → dotnet build → 通知旧进程优雅退出（热键/托盘/SQLite/密码库都正常清理）
        → 把新产物同步到独立运行目录 → 用 --dev 重新启动 → 还原刚才开着的窗口

    关键点：构建写在 bin 目录，程序跑在 .dev-run\app，两者不冲突。
    所以构建的这十几秒里旧版本一直可用，只有同步+重启的那 1~2 秒是空窗；
    构建失败时程序也不会被打断，修好保存后会自动重来。

.PARAMETER Mode
    watch（默认）持续监视并自动重建；run 只构建并启动；build 只构建。

.PARAMETER Configuration
    构建配置，默认 Debug（热重载桥只在 Debug 编译里存在）。

.PARAMETER NoBuild
    跳过构建，仅重启当前运行目录里的产物（配合外部构建/IDE 使用）。

.PARAMETER KeepWindows
    重启后不还原上次开着的窗口。

.PARAMETER DebounceMs
    文件改动后的防抖等待，默认 400ms（避免保存一半就开始构建）。

.EXAMPLE
    .\scripts\dev.ps1

.EXAMPLE
    .\scripts\dev.ps1 -Mode run

.EXAMPLE
    .\scripts\dev.ps1 -NoBuild      # 只重启，不构建
#>
[CmdletBinding()]
param(
    [ValidateSet('watch', 'run', 'build')]
    [string]$Mode = 'watch',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild,

    [switch]$KeepWindows,

    [int]$DebounceMs = 400
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Jietu\Jietu.csproj'
$sourceDir = Join-Path $root 'src\Jietu'
$runDir = Join-Path $root '.dev-run\app'
$sessionFile = Join-Path ([System.IO.Path]::GetTempPath()) 'snapflow-dev-session.json'

function Write-Step([string]$Text) { Write-Host "› $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "  ✓ $Text" -ForegroundColor Green }
function Write-Warn2([string]$Text) { Write-Host "  ! $Text" -ForegroundColor Yellow }
function Write-Err([string]$Text) { Write-Host "  × $Text" -ForegroundColor Red }
# 空行用「一个空格 + Write-Host」，比 Write-Host "" 在各种宿主里都稳（后者可能不产生换行）。
function Write-Gap { Write-Host " " }

# ── 产物目录 ────────────────────────────────────────────────────────────────
# 不写死 bin\x64\Debug\net10.0-windows…\win-x64：项目里有 RID/Platform 变化时
# 目录会跟着变，这里直接从已有的 SnapFlow.exe 反推。
function Get-OutputDir {
    $binRoot = Join-Path $root 'src\Jietu\bin'
    $exe = Get-ChildItem -LiteralPath $binRoot -Recurse -Filter 'SnapFlow.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match ('\\' + [regex]::Escape($Configuration) + '\\') } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $exe) {
        throw "在 src\Jietu\bin 下找不到 $Configuration 配置的 SnapFlow.exe，先跑一次 -Mode build"
    }

    return $exe.Directory.FullName
}

# ── 构建 ────────────────────────────────────────────────────────────────────
function Invoke-Build {
    $started = Get-Date
    # 用 Start-Process 重定向到临时文件再按 UTF-8 读，而不是直接 `& dotnet ...`：
    # dotnet 往重定向流里写的是 UTF-8，中文 Windows 的 PowerShell 5.1 默认按 GBK 解码，
    # 直接捕获会把构建报错变成乱码。
    $stdout = Join-Path $env:TEMP ("snapflow-dev-build-{0}.out" -f [guid]::NewGuid().ToString('N'))
    $stderr = "$stdout.err"
    try {
        $build = Start-Process -FilePath 'dotnet' -NoNewWindow -Wait -PassThru `
            -ArgumentList @('build', $project, '-c', $Configuration, '--nologo', '-v', 'q', '-clp:NoSummary') `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        $lines = @()
        foreach ($file in @($stdout, $stderr)) {
            if (Test-Path -LiteralPath $file) {
                $lines += @(Get-Content -LiteralPath $file -Encoding UTF8 -ErrorAction SilentlyContinue)
            }
        }

        $errors = @($lines |
            Where-Object { $_ -match ':\s*error\s' } |
            ForEach-Object { $_.Trim() } |
            Select-Object -Unique)

        return @{
            Ok      = ($build.ExitCode -eq 0)
            Errors  = $errors
            Seconds = [int]((Get-Date) - $started).TotalSeconds
        }
    }
    finally {
        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

# ── 同步到独立运行目录 ──────────────────────────────────────────────────────
function Sync-RunDir([string]$outputDir) {
    if (-not (Test-Path -LiteralPath $runDir)) {
        New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    }

    # /MIR 保证运行目录和产物完全一致（增量只搬变化的文件，通常 1 秒内）。
    $null = robocopy $outputDir $runDir /MIR /NFL /NDL /NJH /NJS /NP /R:3 /W:1
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy 同步失败（退出码 $LASTEXITCODE）"
    }
}

# ── 与开发实例通信 ──────────────────────────────────────────────────────────
function Send-DevCommand([int]$ProcessId, [string]$Command) {
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', "SnapFlow.Dev.$ProcessId", [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(2000)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine($Command)
        $reader = New-Object System.IO.StreamReader($pipe)
        $reply = $reader.ReadLine()
        return ($reply -eq 'bye' -or $reply -eq 'ok')
    }
    catch {
        return $false
    }
    finally {
        if ($pipe) { $pipe.Dispose() }
    }
}

function Get-DevProcesses {
    $list = @()
    foreach ($p in (Get-Process -Name SnapFlow -ErrorAction SilentlyContinue)) {
        try {
            if ($p.Path -and $p.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                $list += $p
            }
        }
        catch {
            # 拿不到 Path（权限不足）时不碰它。
        }
    }

    return $list
}

# VS 按 F5 启动的进程，父进程就是 devenv.exe。这类实例绝对不能接管：
# 杀掉它等于把调试会话（以及正在跑的热重载）打断，VS 里会直接报“调试器已分离”。
function Test-UnderDebugger([int]$ProcessId) {
    try {
        $info = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
        if (-not $info) { return $false }
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($info.ParentProcessId)" -ErrorAction SilentlyContinue
        return ($parent -and $parent.Name -eq 'devenv.exe')
    }
    catch {
        return $false
    }
}

function Test-Debugging {
    foreach ($p in (Get-DevProcesses)) {
        if (Test-UnderDebugger $p.Id) { return $true }
    }

    return $false
}

function Stop-DevApp {
    $targets = @(Get-DevProcesses)
    foreach ($p in $targets) {
        if (Test-UnderDebugger $p.Id) {
            Write-Warn2 "pid $($p.Id) 正在被 Visual Studio 调试，跳过接管"
            continue
        }

        if (Send-DevCommand $p.Id 'exit') {
            if ($p.WaitForExit(10000)) {
                Write-Ok "旧进程已优雅退出 (pid $($p.Id))"
                continue
            }
        }

        # 没有 --dev（管道不存在）或退出超时：只能强杀。
        try {
            $p.Kill()
            $null = $p.WaitForExit(3000)
            Write-Warn2 "强制结束 pid $($p.Id)（未走优雅退出，贴图/密码库会话丢失属正常）"
        }
        catch {
            Write-Warn2 "结束 pid $($p.Id) 失败：$($_.Exception.Message)"
        }
    }

    if ($KeepWindows -and (Test-Path -LiteralPath $sessionFile)) {
        Remove-Item -LiteralPath $sessionFile -Force -ErrorAction SilentlyContinue
    }
}

function Start-DevApp {
    $exe = Join-Path $runDir 'SnapFlow.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "找不到 $exe，先跑一次 -Mode build 或去掉 -NoBuild"
    }

    $process = Start-Process -FilePath $exe -ArgumentList '--dev' -PassThru
    Start-Sleep -Milliseconds 900
    $process.Refresh()
    if ($process.HasExited) {
        Write-Err "进程启动后立刻退出（ExitCode $($process.ExitCode)），看日志：$env:LOCALAPPDATA\SnapFlow\log.txt"
        return
    }

    Write-Ok "已启动 pid $($process.Id)（托盘图标应已出现）"
}

# ── 源码指纹（轮询，比 FileSystemWatcher 在 PowerShell 里稳）─────────────────
function Get-SourceSignature {
    $builder = New-Object System.Text.StringBuilder
    $files = Get-ChildItem -LiteralPath $sourceDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj)\\' -and
            $_.Extension -in @('.cs', '.xaml', '.csproj', '.manifest', '.ico', '.json')
        } |
        Sort-Object FullName

    foreach ($f in $files) {
        [void]$builder.Append($f.FullName).Append('|').
            Append($f.LastWriteTimeUtc.Ticks).Append('|').
            Append($f.Length).Append("`n")
    }

    return $builder.ToString()
}

function Test-ConsoleKey([string]$Key) {
    try {
        if (-not [Console]::KeyAvailable) { return $false }
        $pressed = [Console]::ReadKey($true)
        return ($pressed.KeyChar -eq $Key)
    }
    catch {
        # 输出被重定向（比如接到管道里）时没有键盘可用。
        return $false
    }
}

# ── 主流程 ──────────────────────────────────────────────────────────────────
Write-Gap
Write-Host "SnapFlow 开发热重载" -ForegroundColor Magenta
Write-Host "  项目      : src\Jietu\Jietu.csproj ($Configuration)"
Write-Host "  运行目录  : .dev-run\app  （与 bin 分离，构建时不会锁住正在运行的程序）"
Write-Host "  启动参数  : --dev （独立单实例通道 + 优雅退出管道 + 启动即开设置窗口 / 重启还原窗口）"
if ($Mode -eq 'watch') {
    Write-Host "  自动重建  : src\Jietu 下 .cs / .xaml / .csproj 保存后触发"
    Write-Host "  退出监视  : Ctrl+C（只停监视，不会关掉正在运行的 SnapFlow）"
    Write-Host "  VS 调试中 : 自动避让，不抢进程、不打断热重载"
}
Write-Gap

if ($Mode -eq 'build') {
    $result = Invoke-Build
    if ($result.Ok) {
        Write-Ok "构建成功 $($result.Seconds)s"
        exit 0
    }

    $result.Errors | Select-Object -First 20 | ForEach-Object { Write-Err $_ }
    exit 1
}

$outputDir = Get-OutputDir
Write-Host "  构建输出  : $outputDir"
Write-Gap

# VS 正在调试时不要接管：那会把调试会话连同热重载一起打断。
if (Test-Debugging) {
    Write-Warn2 "检测到 Visual Studio 正在调试 SnapFlow。"
    Write-Host "  调试 / XAML 热重载期间本脚本不接管进程。想切回自动重启，先在 VS 里 Shift+F5 停止调试。" -ForegroundColor DarkGray
    exit 0
}

# 先接管可能残留在仓库目录下的实例（包括之前手动从 bin 双击起来的那个）。
Stop-DevApp

$first = $true
if (-not $NoBuild) {
    Write-Step "首次构建…"
    $result = Invoke-Build
    if (-not $result.Ok) {
        $result.Errors | Select-Object -First 20 | ForEach-Object { Write-Err $_ }
        if ($Mode -eq 'run') { exit 1 }
        Write-Warn2 "首次构建失败；改好保存后会自动重试"
        $first = $false
    }
    else {
        Write-Ok "构建成功 $($result.Seconds)s"
        Sync-RunDir $outputDir
        Write-Ok "已同步到 $runDir"
    }
}

if ($first) {
    try {
        Start-DevApp
    }
    catch {
        Write-Err $_.Exception.Message
        exit 1
    }
}

if ($Mode -eq 'run') {
    exit 0
}

$signature = Get-SourceSignature
Write-Gap
Write-Host "监视中… 改完保存即可，构建期间旧版本仍可用。" -ForegroundColor DarkGray

while ($true) {
    Start-Sleep -Milliseconds 500

    if (Test-ConsoleKey 'q') {
        Write-Host "已停止监视（SnapFlow 继续运行）。" -ForegroundColor DarkGray
        break
    }

    if (Test-ConsoleKey 'r') {
        Write-Step "手动触发重建"
        # 落到下面的统一流程，用 -NoBuild 之外的方式复用
        $changed = $true
    }
    else {
        $current = Get-SourceSignature
        $changed = ($current -ne $signature)
        if ($changed) { $signature = $current }
    }

    if (-not $changed) { continue }

    # 防抖：编辑器可能还在写盘（尤其是格式化后整文件重写的场景）。
    Start-Sleep -Milliseconds $DebounceMs
    $signature = Get-SourceSignature

    # 调试期间只记录不动作：既不去构建（bin 可能被占用），也不去抢进程。
    if (Test-Debugging) {
        Write-Warn2 "VS 正在调试中，跳过本次自动重启（停止调试后再改一次文件即可恢复）"
        continue
    }

    if (-not $NoBuild) {
        Write-Step "改动已保存，构建中…"
        $result = Invoke-Build
        if (-not $result.Ok) {
            Write-Err "构建失败（$($result.Seconds)s），旧版本继续运行；改好保存会自动重试"
            $result.Errors | Select-Object -First 20 | ForEach-Object { Write-Err $_ }
            continue
        }

        Write-Ok "构建成功 $($result.Seconds)s"
    }

    try {
        Stop-DevApp
        Sync-RunDir $outputDir
        Start-DevApp
    }
    catch {
        Write-Err "重启失败：$($_.Exception.Message)"
    }

    Write-Gap
}
