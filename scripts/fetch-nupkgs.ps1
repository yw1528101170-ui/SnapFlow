$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 缓存目录固定在仓库根目录（脚本位于 scripts/ 下）。
# 必须显式创建：目录被 .gitignore 排除，全新检出（如 CI）上不存在，
# 大文件分块路径的 File.Open 会直接抛 DirectoryNotFoundException。
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root '.nupkg-cache'
if (-not (Test-Path -LiteralPath $dest)) {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
}

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
    # ── 直接依赖（种子）──────────────────────────────────────────────
    "Microsoft.WindowsAppSDK.WinUI|2.3.9"              = "d36145bd6c0f46fc04e07fb4af365d093fb42c39b625948d0ead07a0b26fce96"
    "Microsoft.WindowsAppSDK.Foundation|2.3.12"        = "ce04d01d68cb16b5dc85a5c9efb8162dba876bc2e5b766b5968979899d4efa9e"
    "Microsoft.WindowsAppSDK.Runtime|2.5.1"            = "2fbd27012291be50cafed6942306964281d37167af6a6af3887e1163ec5cf063"
    "Microsoft.Windows.SDK.BuildTools|10.0.26100.4654" = "5f5783658a200f83ea2addfd406e2303a901b27ad7c49b0347c34aefcc21883d"
    "Microsoft.Data.Sqlite|10.0.0"                     = "a787e8342fe25835b0832133171755c9a360eb111ead34c8a2ec64ee0d86d5d3"
    "Microsoft.Windows.SDK.NET.Ref|10.0.26100.57"      = "eaa0e3b938319f75beac5c046c84ef57212c2743e9263e0db33b2019ab70b524"
    "Microsoft.NET.Test.Sdk|17.14.1"                   = "9995330c5bc5a7bc759caadc9d17748616cdbb983c11062df122a74607614ff0"
    "xunit|2.9.3"                                      = "04fae949b8e5201ecfa07fa8702baca8c0eb3198114194b3793789cc72bf23d7"
    "xunit.runner.visualstudio|3.1.5"                  = "3b9eb9efcf3840695db33b045901420d14d7562141999e06182fb8894faeb124"
    "ZXing.Net|0.16.10"                                = "d27ace46197fe68b97607943aae6b76c4ca4a7a5ed28044b0f4ba61d33a70350"
    "Microsoft.WindowsAppSDK.InteractiveExperiences|2.1.9" = "f9159d73e3e46717f159606e43b4acf8336aaff5bdd4d395fe3a93fdb41437ac"
    "NETStandard.Library|2.0.0"                        = "3e9edf47295a8bc26b1353bef5319f20426b00e9a75932512d613ea8905a84ad"
    "Microsoft.NETCore.Platforms|1.1.0"                = "15e338d24b5c39b4099389cc612841eb51ff13c07bb4829f97d39b27420e7023"
    # ── 传递依赖（由脚本从官方源下载后计算固化）──────────────────────
    "Microsoft.CodeCoverage|17.14.1"                   = "7fc432b46f06bd1a0fe3baced8a1269c32f1229c9eb1aab6e931630dd5b8d06b"
    "Microsoft.Data.Sqlite.Core|10.0.0"                = "d388d883d11bad073e2c7eb02a1dc048208d6aeeb06791aad2d0d877ca081c0b"
    "Microsoft.TestPlatform.ObjectModel|17.14.1"       = "40c7fa3bec34213fb5e8caf3a3bc27f8ddb47f72f5fe60e1b3faa3984a35ad8b"
    "Microsoft.TestPlatform.TestHost|17.14.1"          = "d5cc4759cbc7443ee8ad0dc41043cfc55184913a71a26d7fce82020bd4889c9b"
    "Microsoft.Web.WebView2|1.0.3719.77"               = "2f6be3a10a1a8d6d1fde986af4131dab344f8181fffe75590824e0f4b037ed73"
    "Microsoft.Windows.SDK.BuildTools.MSIX|1.7.251221100" = "e61c560805029f5ae15433be52e153f3862b6faaf725292638a9ece592f7a060"
    "Microsoft.WindowsAppSDK.Base|2.0.4"               = "e3e13478c4c80c59ed5f8f89542fe49a2985daa484753e93a5858e90c2d46a4d"
    "Newtonsoft.Json|13.0.3"                           = "872fc189e638ab1056555b03aaa38f68bcb54286e221aa646eb1129babf63c77"
    "SQLitePCLRaw.bundle_e_sqlite3|2.1.11"             = "91645aa4c4d511f71cd03c6723d022d7e470000711dbe1d4bbe585f8e78b242b"
    "SQLitePCLRaw.core|2.1.11"                         = "b3f7f112862535ff5cd82e0766e78cccf081be98958835654a983b7a90741976"
    "SQLitePCLRaw.lib.e_sqlite3|2.1.11"                = "6667df6c73609cb51db0f6e29229440228715e5d4c79d2014355f3b7ddb6e81c"
    "SQLitePCLRaw.lib.e_sqlite3.android|2.1.11"       = "c2b01759e5b1b9277385bd2205c661d818bc0077ff3a913bd5336c05e212bfa0"
    "SQLitePCLRaw.lib.e_sqlite3.ios|2.1.11"            = "c330fc87ec66cfdf2ab5d6fce32b3c2e6e59840ea9bbdbc27f5072971319637c"
    "SQLitePCLRaw.lib.e_sqlite3.tvos|2.1.11"           = "ad671c2e19dddaee3cd17af73eeb7153e143c7b32cf85368ac8c92db589af41b"
    "SQLitePCLRaw.provider.e_sqlite3|2.1.11"           = "2dd7d5df6e409988013b09b03fb30d67130966434ee9bc2b1ef07a0b94b222d0"
    "SQLitePCLRaw.provider.internal|2.1.11"            = "d710833964793b4175c361a58a26d7d1056ba2f2836ca74e5cc0417d0a91f9d1"
    "System.Buffers|4.4.0"                             = "293c408586b0146d95e555f58f0de9cf1dc8ad05d1827d53b2f8233e0c406ea0"
    "System.Buffers|4.5.1"                             = "c30b3dd2c7e2f4cee4b823d692fd42118309b42ab1f5007f923d329a5b0d6b12"
    "System.Collections.Immutable|8.0.0"               = "17b3958ca370a6a6d487c95389d6ea256622e3bea7b2af67fba934f90551a37c"
    "System.Memory|4.5.3"                              = "0af97b45b45b46ef6a2b37910568dabd492c793da3859054595d523e2a545859"
    "System.Memory|4.5.5"                              = "10f43da352a29fb2b3188e4edd4dcf5100194c8b526e4f61fe2e2b5623775a22"
    "System.Numerics.Vectors|4.4.0"                    = "6ae5d02b67e52ff2699c1feb11c01c526e2f60c09830432258e0809486aabb65"
    "System.Reflection.Metadata|8.0.0"                 = "750182df425ab880d63565ccad234e2677156b5ba6475b228dacd8c140dd4881"
    "System.Runtime.CompilerServices.Unsafe|4.5.2"     = "f1e5175c658ed8b2fbb804cc6727b6882a503844e7da309c8d4846e9ca11e4ef"
    "System.Runtime.CompilerServices.Unsafe|4.5.3"     = "96764c52a44ee1161151e48ef07489f72047a851cb55b99e9f01d6908536d1a9"
    "System.Runtime.CompilerServices.Unsafe|6.0.0"     = "6c41b53e70e9eee298cff3a02ce5acdd15b04125589be0273f0566026720a762"
    "System.Runtime.InteropServices.NFloat.Internal|6.0.1" = "8ecea5991043b0ed2651643b83c230a168b76b0c342c05c007d888ee1c0b2a07"
    "xunit.abstractions|2.0.3"                         = "d03d72fc2df8880448f7a81bddb00e1bcb5c18f323c7e7cc69b4cfa727469403"
    "xunit.analyzers|1.18.0"                           = "0ce81a98b9df8bd51ae480e6dc9566f4c68e15b4926e6ab997c8f634f3b7a9dd"
    "xunit.assert|2.9.3"                               = "bc760e75ef1b775d293a6afb89301836d3e5a87cec265e31dedd430ee61d0c20"
    "xunit.core|2.9.3"                                 = "aa4550f09c3f2d95a6c62ae43cec22af2edbbd99f722e691ceefeca761fc6a7c"
    "xunit.extensibility.core|2.9.3"                   = "99ca555fe9b447b1747aff426819db6a2f60b6ee0655caa28c4b91a9ef3d0f48"
    "xunit.extensibility.execution|2.9.3"              = "dabc4cb360ede1c01c98e14cc0fe587774693f40679a22fc71794acac5d8d23c"
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
    # NuGet 官方 v3 flatcontainer 源（globalcdn 镜像不可控，官方源 + 哈希校验更可靠）。
    # 注意 URL 必须带版本目录段：{id}/{version}/{id}.{version}.nupkg，
    # 根级平铺形式（无版本目录）在 api.nuget.org 上返回 404。
    $url = "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/$($ver.ToLower())/$name"
    # 每次使用随机临时文件，避免并发运行时互相踩踏
    $probe = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName())
    $len = -1
    # CI 出口 IP 可能被 nuget.org 瞬时限流（429），探测也必须重试
    for ($t = 0; $t -lt 6 -and $len -lt 0; $t++) {
        if ($t -gt 0) { Start-Sleep -Milliseconds (500 * $t) }
        $headers = & curl.exe -4 -s -D - --tls-max 1.2 --retry 3 -H "Range: bytes=0-0" -o $probe $url 2>$null
        $match = [regex]::Match(($headers -join "`n"), "Content-Range:\s*bytes\s+\d+-\d+/(\d+)", "IgnoreCase")
        if ($match.Success) { $len = [int64]$match.Groups[1].Value }
    }
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
    if ($len -lt 0) {
        # 精确版本探测失败：个别 nuspec 依赖声明指向从未发布的版本号（例如
        # WindowsAppSDK 组件声明 2.1.8，官方源上只有 2.1.9）。NuGet 还原时按
        # “>= 声明版本的最小可用版本”解析，这里遵循同一规则从版本索引回退，
        # 否则离线缓存会缺包、CI 还原在 NU1101 上失败。
        try {
            $indexUrl = "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/index.json"
            $indexJson = & curl.exe -4 -s --tls-max 1.2 --retry 3 $indexUrl 2>$null | ConvertFrom-Json
            $want = [version]$ver
            $picked = $indexJson.versions |
                Where-Object { $_ -notmatch '-' -and ([version]$_ -ge $want) } |
                Sort-Object { [version]$_ } | Select-Object -First 1
            if ($picked) {
                Write-Host "RESOLVE $id $ver -> $picked"
                $ver = $picked
                $key = "$id|$ver"
                $name = "{0}.{1}.nupkg" -f $id.ToLower(), $ver.ToLower()
                $file = Join-Path $dest $name
                $url = "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/$($ver.ToLower())/$name"
                for ($t = 0; $t -lt 6 -and $len -lt 0; $t++) {
                    if ($t -gt 0) { Start-Sleep -Milliseconds (500 * $t) }
                    $headers = & curl.exe -4 -s -D - --tls-max 1.2 -H "Range: bytes=0-0" -o $probe $url 2>$null
                    $match = [regex]::Match(($headers -join "`n"), "Content-Range:\s*bytes\s+\d+-\d+/(\d+)", "IgnoreCase")
                    if ($match.Success) { $len = [int64]$match.Groups[1].Value }
                }
            }
        }
        catch { }
    }
    if ($len -lt 0) { Write-Host "SKIP $id $ver"; return $null }
    Write-Host "GET $name $len"
    $part = "$file.$([System.IO.Path]::GetRandomFileName()).part"
    if ($len -lt 2500000) {
        # CI 出口 IP 偶发被限流：整包最多再重试 3 轮，失败时带出 curl 退出码与错误文本，
        # 便于从 CI 注解直接定位（完整日志需要登录才能看）。
        $ok = $false
        for ($t = 0; $t -lt 4 -and -not $ok; $t++) {
            if ($t -gt 0) { Start-Sleep -Seconds (2 * $t); Write-Host "RETRY $name #$t" }
            $errFile = "$part.err"
            & curl.exe -4 -L --tls-max 1.2 --retry 6 --retry-all-errors -o $part $url 2>$errFile
            if ($LASTEXITCODE -eq 0 -and (Test-Path $part)) {
                $ok = $true
            }
            else {
                $errText = if (Test-Path $errFile) { Get-Content $errFile -Raw } else { "" }
                Write-Host ("FAIL {0} curl={1} {2}" -f $name, $LASTEXITCODE, ($errText -replace "\s+", " "))
            }
            Remove-Item $errFile -Force -ErrorAction SilentlyContinue
        }
        if (-not $ok) { throw "download $name (curl exit $LASTEXITCODE)" }
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
    "Microsoft.Windows.SDK.NET.Ref|10.0.26100.57",
    "Microsoft.NET.Test.Sdk|17.14.1",
    "xunit|2.9.3",
    "xunit.runner.visualstudio|3.1.5",
    "ZXing.Net|0.16.10"
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
