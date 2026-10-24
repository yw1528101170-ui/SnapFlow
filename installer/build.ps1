# ==============================================================================
# SnapFlow (瞬流) 标准化发布打包脚本
# ==============================================================================
param (
    # 可选：覆盖版本号。默认从 src/Jietu/Jietu.csproj 的 <Version> 读取。
    [string]$Version,
    # 谨慎使用：无条件强杀运行中的 SnapFlow/Jietu 进程（CI 环境下自动生效）。
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
$dist = Join-Path $root "dist"
$tempPortable = Join-Path $root "artifacts\portable\SnapFlow"

# 0. 版本号：默认从 src/Jietu/Jietu.csproj 的 <Version> 读取，保持单一来源。
if (-not $Version) {
    $csprojPath = Join-Path $root "src\Jietu\Jietu.csproj"
    if (-not (Test-Path $csprojPath)) {
        throw "未找到 $csprojPath，无法读取默认版本号。请用 -Version 参数显式指定。"
    }
    $csprojText = Get-Content -Path $csprojPath -Raw -Encoding UTF8
    $verMatch = [regex]::Match($csprojText, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $verMatch.Success -or -not $verMatch.Groups[1].Value) {
        throw "无法从 $csprojPath 中解析 <Version> 属性。请用 -Version 参数显式指定。"
    }
    $Version = $verMatch.Groups[1].Value
    Write-Host "使用 csproj 中的版本号: $Version" -ForegroundColor DarkGray
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " 开始执行 SnapFlow v$Version 标准化发布打包" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 检查 Inno Setup 6 编译器
$localApp = $env:LOCALAPPDATA
$candidates = @(
    "$localApp\Programs\Inno Setup 6\ISCC.exe",
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    throw "未找到 Inno Setup 6，请确认已安装 ISCC.exe。"
}
Write-Host "[1/6] 找到 Inno Setup 编译器: $iscc" -ForegroundColor Green

# 2. 处理可能正在运行的旧进程，避免文件锁占用
# 默认不强杀：交互式运行时询问确认；仅 -Force 或 CI 环境才自动强杀。
$running = Get-Process -Name SnapFlow, Jietu -ErrorAction SilentlyContinue
if ($running) {
    if ($Force -or $env:CI) {
        $running | ForEach-Object {
            Write-Host "  -> 终止正在运行的 SnapFlow/Jietu 进程 (PID: $($_.Id)) 以释放文件锁..." -ForegroundColor Yellow
            Stop-Process -Id $_.Id -Force
        }
    }
    else {
        $answer = Read-Host "检测到 $($running.Count) 个运行中的 SnapFlow/Jietu 进程，会占用输出文件。是否强制结束它们？(y/N)"
        if ($answer -match '^[Yy]') {
            $running | ForEach-Object {
                Write-Host "  -> 终止进程 (PID: $($_.Id))..." -ForegroundColor Yellow
                Stop-Process -Id $_.Id -Force
            }
        }
        else {
            throw "存在运行中的 SnapFlow/Jietu 进程，已取消打包。请先关闭应用，或使用 -Force 参数重试。"
        }
    }
    Start-Sleep -Milliseconds 500
}

# 3. 清理旧构建产物目录
Write-Host "[2/6] 清理旧构建产物目录..." -ForegroundColor Green
if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}
if (Test-Path $dist) {
    # 整目录删除重建，避免残留子目录（与 $publish 的清理方式一致）
    Remove-Item -Recurse -Force $dist
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# 4. 执行 dotnet publish
Write-Host "[3/6] 执行 dotnet publish (Release, win-x64, Self-Contained)..." -ForegroundColor Green
dotnet publish (Join-Path $root "src\Jietu\Jietu.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    -p:Version=$Version `
    --configfile (Join-Path $PSScriptRoot "nuget.publish.config") `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码: $LASTEXITCODE"
}

# 版本一致性校验：exe 文件版本必须与安装包版本一致，避免“安装包 1.1.1、里面还是 1.1.0”。
$exeVersion = (Get-Item (Join-Path $publish "SnapFlow.exe")).VersionInfo.ProductVersion
if ($exeVersion -notlike "$Version*") {
    throw "发布版本不一致：SnapFlow.exe 版本为 $exeVersion，期望 $Version（检查 csproj 的 Version 属性）。"
}

# 移除调试符号文件以精简安装与便携包体积
Get-ChildItem -Path $publish -Filter "*.pdb" -Recurse | Remove-Item -Force

# 5. 编译 Inno Setup 安装包
Write-Host "[4/6] 编译 Inno Setup 安装向导程序..." -ForegroundColor Green
& $iscc "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot "Jietu.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出码: $LASTEXITCODE"
}

# 6. 打包免安装绿色便携版 (Portable ZIP)
Write-Host "[5/6] 制作免安装绿色便携版 (ZIP)..." -ForegroundColor Green
if (Test-Path $tempPortable) {
    Remove-Item -Recurse -Force (Split-Path -Parent $tempPortable)
}
New-Item -ItemType Directory -Path $tempPortable -Force | Out-Null

# 拷贝发布文件到临时便携目录
Copy-Item -Path (Join-Path $publish "*") -Destination $tempPortable -Recurse -Force

# 复制便携版使用说明（v{VERSION} 占位符替换为实际版本号，版本号唯一来源仍是 csproj）
$readmeSrc = Join-Path $PSScriptRoot "使用说明.txt"
if (Test-Path $readmeSrc) {
    $readmeText = [System.IO.File]::ReadAllText($readmeSrc).Replace("{VERSION}", $Version)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $tempPortable "使用说明.txt"), $readmeText, $utf8NoBom)
}

# 压缩为 Zip 格式
$portableZip = Join-Path $dist "SnapFlow-v$Version-win-x64-Portable.zip"
if (Test-Path $portableZip) {
    Remove-Item -Force $portableZip
}
Compress-Archive -Path "$tempPortable\*" -DestinationPath $portableZip -CompressionLevel Optimal

# 清理临时便携目录
Remove-Item -Recurse -Force (Split-Path -Parent $tempPortable)

# 7. 运行自测试验证发布物质量
Write-Host "[6/6] 运行二进制自测试验证..." -ForegroundColor Green
$testExe = Join-Path $publish "SnapFlow.exe"
$testProc = Start-Process -FilePath $testExe -ArgumentList "--self-test" -Wait -PassThru -NoNewWindow
if ($testProc.ExitCode -ne 0) {
    throw "自测试失败！退出码: $($testProc.ExitCode)"
}
Write-Host "  -> 二进制自测试全部通过！" -ForegroundColor Green

# 8. 生成 SHA256 校验清单
Write-Host "计算 SHA256 校验值..." -ForegroundColor Green
$shaFile = Join-Path $dist "SHA256SUMS.txt"
$checksumLines = @()
$releaseFiles = Get-ChildItem -Path $dist -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" -and $_.Name -ne "RELEASE_NOTES.md" }
foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
    $checksumLines += "$hash  $($file.Name)"
}
$checksumLines | Set-Content -Path $shaFile -Encoding UTF8

# 9. 生成发布说明 Release Notes（v{VERSION} 占位符替换为实际版本号后附上校验哈希）
$rnSrc = Join-Path $PSScriptRoot "RELEASE_NOTES.md"
$rnDst = Join-Path $dist "RELEASE_NOTES.md"
if (Test-Path $rnSrc) {
    $rnContent = Get-Content -Path $rnSrc -Raw -Encoding UTF8
    $rnContent = $rnContent.Replace("{VERSION}", $Version)
    $rnContent += "`r`n`r`n## 🔒 校验哈希 (SHA-256)`r`n`r`n``````text`r`n"
    $rnContent += ($checksumLines -join "`r`n")
    $rnContent += "`r`n```````r`n"
    [System.IO.File]::WriteAllText($rnDst, $rnContent, [System.Text.Encoding]::UTF8)
}

# 10. 清理构建临时产物以保持目录整洁
Write-Host "清理构建临时产物以保持目录整洁..." -ForegroundColor Green
$artifactsRoot = Join-Path $root "artifacts"
if (Test-Path $artifactsRoot) {
    Remove-Item -Recurse -Force $artifactsRoot
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " 打包发布完成！发布文件清单：" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Get-ChildItem -Path $dist | Format-Table Name, Length, LastWriteTime
