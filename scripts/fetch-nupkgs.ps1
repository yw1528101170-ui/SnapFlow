$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 缓存目录固定在仓库根目录（脚本位于 scripts/ 下）
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root '.nupkg-cache'

# ==============================================================================
# 完整性校验清单（SHA-256，小写十六进制）
# ------------------------------------------------------------------------------
# 说明：首次使用时清单值均为 $null（占位）。脚本下载完成后会计算实际哈希并打印
# "PIN <id>|<version> <sha256>" 提示行；把该值填入下方对应条目即可启用校验。
# 之后每次运行（包括命中缓存时）都会对文件重新计算 SHA-256 并与清单比对，
# 不一致即抛错退出，防止被篡改或截断的包进入离线还原流程。
# 填充方式：
#   1. 保持脚本默认（清单值为 $null）跑一遍，从输出的 PIN 行复制哈希；或手动执行
#      `Get-FileHash <nupkg> -Algorithm SHA256` 取哈希值。
#   2. 把哈希（小写十六进制，64 位）填到对应包的右侧。
#   3. 再次运行脚本，确认无校验报错。
# 注意：不要填入未经核实的哈希值；错误哈希会让所有后续运行直接失败（这符合预期）。
# ==============================================================================
$PackageChecksums = @{
    "Microsoft.WindowsAppSDK.WinUI|2.3.9"        = "d36145bd6c0f46fc04e07fb4af365d093fb42c39b625948d0ead07a0b26fce96"
    "Microsoft.WindowsAppSDK.Foundation|2.3.12"  = "ce04d01d68cb16b5dc85a5c9efb8162dba876bc2e5b766b5968979899d4efa9e"
    "Microsoft.WindowsAppSDK.Runtime|2.5.1"      = "2fbd27012291be50cafed6942306964281d37167af6a6af3887e1163ec5cf063"
    "Microsoft.Windows.SDK.BuildTools|10.0.26100.4654" = "5f5783658a200f83ea2addfd406e2303a901b27ad7c49b0347c34aefcc21883d"
    "Microsoft.Data.Sqlite|10.0.0"               = "a787e8342fe25835b0832133171755c9a360eb111ead34c8a2ec64ee0d86d5d3"
    "Microsoft.Windows.SDK.NET.Ref|10.0.26100.57" = "eaa0e3b938319f75beac5c046c84ef57212c2743e9263e0db33b2019ab70b524"
}

function Test-PkgHash([string]$file, [string]$key) {
    $expected = $PackageChecksums[$key]
    $actual = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToLower()
    if ($expected) {
        if ($actual -ne $expected.ToLower()) {
            throw "完整性校验失败：$file 的 SHA256 为 $actual，与清单值 $expected 不一致。请删除该文件后重新下载，或核实清单。"
        }
        Write-Host "OK  $key (sha256 校验通过)"
    }
    else {
        # 清单尚未固定：打印 PIN 行提示填充，本次不拦截。
        Write-Host "PIN $key $actual" -ForegroundColor Yellow
        Write-Host "    ^ 请把该 SHA256 填入 fetch-nupkgs.ps1 顶部的 \$PackageChecksums 以启用校验" -ForegroundColor Yellow
    }
}

function Resolve-Ver([string]$v) {
    if ($v -match '^\[([^,\]]+)\]$') { return $Matches[1] }
    if ($v -match '^\[([^,\]]+),') { return $Matches[1] }
    if ($v -match '^([^,\(\)\[\]]+)$') { return $Matches[1] }
    return $null
}

function Get-Deps([string]$file) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($file)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        $reader = New-Object System.IO.StreamReader($entry.Open())
        $text = $reader.ReadToEnd()
        $reader.Close()
    }
    finally { $zip.Dispose() }
    $list = New-Object System.Collections.Generic.List[string]
    $groups = [regex]::Matches($text, '<group([^>]*)>(.*?)</group>', 'Singleline')
    if ($groups.Count -eq 0) {
        foreach ($m in [regex]::Matches($text, '<dependency\s+id="([^"]+)"\s+version="([^"]+)"')) {
            $ver = Resolve-Ver $m.Groups[2].Value
            if ($ver) { $list.Add(("{0}|{1}" -f $m.Groups[1].Value, $ver)) }
        }
        return $list
    }
    foreach ($g in $groups) {
        $tfm = $g.Groups[1].Value
        $keep = $tfm -notmatch 'targetFramework' -or $tfm -match 'net([6-9]|10)|netstandard2|native'
        if (-not $keep) { continue }
        foreach ($m in [regex]::Matches($g.Groups[2].Value, '<dependency\s+id="([^"]+)"\s+version="([^"]+)"')) {
            $ver = Resolve-Ver $m.Groups[2].Value
            if ($ver) { $list.Add(("{0}|{1}" -f $m.Groups[1].Value, $ver)) }
        }
    }
    return $list
}

function Save-Pkg([string]$id, [string]$ver) {
    $key = "$id|$ver"
    $name = "{0}.{1}.nupkg" -f $id.ToLower(), $ver.ToLower()
    $file = Join-Path $dest $name
    if (Test-Path $file) {
        try {
            $z = [System.IO.Compression.ZipFile]::OpenRead($file); $z.Dispose()
            Test-PkgHash $file $key
            return $file
        }
        catch { Remove-Item $file -Force }
    }
    # NuGet 官方 v3 flatcontainer 源（globalcdn 镜像不可控，官方源 + 哈希校验更可靠）
    $url = "https://api.nuget.org/v3-flatcontainer/$name"
    # 每次使用随机临时文件，避免并发运行时互相踩踏
    $probe = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName())
    $headers = & curl.exe -4 -s -D - --tls-max 1.2 -H "Range: bytes=0-0" -o $probe $url
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
    $match = [regex]::Match(($headers -join "`n"), "Content-Range:\s*bytes\s+\d+-\d+/(\d+)", "IgnoreCase")
    if (-not $match.Success) { Write-Host "SKIP $id $ver"; return $null }
    $len = [int64]$match.Groups[1].Value
    Write-Host "GET $name $len"
    $part = "$file.$([System.IO.Path]::GetRandomFileName()).part"
    if ($len -lt 2500000) {
        & curl.exe -4 -L --tls-max 1.2 --retry 6 --retry-all-errors -o $part $url
        if ($LASTEXITCODE -ne 0) { throw "download $name" }
        Move-Item $part $file -Force
    }
    else {
        $fs = [System.IO.File]::Open($file, "Create", "Write", "Read")
        try {
            $fs.SetLength($len)
            $chunk = 4MB
            for ($start = [int64]0; $start -lt $len; $start += $chunk) {
                $end = [Math]::Min($start + $chunk - 1, $len - 1)
                $expect = [int]($end - $start + 1)
                $ok = $false
                for ($t = 0; $t -lt 8 -and -not $ok; $t++) {
                    if (Test-Path $part) { Remove-Item $part -Force }
                    & curl.exe -4 -s -S --tls-max 1.2 --connect-timeout 20 --max-time 90 -H "Range: bytes=$start-$end" -o $part $url
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $part) -and (Get-Item $part).Length -eq $expect) { $ok = $true }
                }
                if (-not $ok) { throw "chunk $name $start" }
                $bytes = [System.IO.File]::ReadAllBytes($part)
                $fs.Position = $start
                $fs.Write($bytes, 0, $bytes.Length)
            }
        }
        finally { $fs.Dispose() }
        Remove-Item $part -Force -ErrorAction SilentlyContinue
    }
    Test-PkgHash $file $key
    return $file
}

$seeds = @(
    "Microsoft.WindowsAppSDK.WinUI|2.3.9",
    "Microsoft.WindowsAppSDK.Foundation|2.3.12",
    "Microsoft.WindowsAppSDK.Runtime|2.5.1",
    "Microsoft.Windows.SDK.BuildTools|10.0.26100.4654",
    "Microsoft.Data.Sqlite|10.0.0",
    "Microsoft.Windows.SDK.NET.Ref|10.0.26100.57"
)
$queue = New-Object System.Collections.Generic.Queue[string]
foreach ($seed in $seeds) { $queue.Enqueue($seed) }
$seen = New-Object System.Collections.Generic.HashSet[string]
while ($queue.Count -gt 0) {
    $item = $queue.Dequeue()
    if (-not $seen.Add($item)) { continue }
    $pair = $item.Split("|")
    $file = Save-Pkg $pair[0] $pair[1]
    if ($file) {
        foreach ($d in (Get-Deps $file)) { $queue.Enqueue($d) }
    }
}
Write-Host ("DONE " + (Get-ChildItem $dest -Filter *.nupkg).Count)
