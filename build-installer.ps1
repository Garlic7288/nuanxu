# ============================================================
#  暖序 · 一键打包
#  产出：release\暖序-<版本>-安装程序.exe   （双击安装）
#        release\暖序-<版本>-绿色版.zip     （解压即用）
#  用法：powershell -ExecutionPolicy Bypass -File build-installer.ps1
#        加 -SkipPublish 可跳过 dotnet publish，只重打安装包
#  注意：本文件必须存成 UTF-8 带 BOM，否则 PowerShell 5.1 按 GBK 读，中文路径会解析失败
# ============================================================
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root      = $PSScriptRoot
$project   = Join-Path $root 'NuanXu\暖序.csproj'
$dist      = Join-Path $root 'dist'
$installer = Join-Path $root 'installer'
$iss       = Join-Path $installer '暖序.iss'
$release   = Join-Path $root 'release'

$report = New-Object System.Collections.Generic.List[string]
function Say([string]$text) {
    Write-Host $text
    $report.Add($text)
}

# 用 .NET 直接删，不走 Remove-Item：
# 回收站对几十个文件的目录经常半途失败，本项目 dist 里文件量大，走回收站既慢又占空间
function Remove-Tree([string]$path) {
    if ([IO.Directory]::Exists($path)) {
        try { [IO.Directory]::Delete($path, $true) }
        catch { Write-Warning "删不掉 $path（可能被占用）：$($_.Exception.Message)" }
    }
}
function Remove-File([string]$path) {
    if ([IO.File]::Exists($path)) {
        try { [IO.File]::Delete($path) }
        catch { Write-Warning "删不掉 $path：$($_.Exception.Message)" }
    }
}

# ---------- 1. 版本号：从 csproj 读，保证安装包与程序永远一致 ----------
$version = @(([xml](Get-Content $project -Raw -Encoding UTF8)).Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw 'csproj 里读不到 <Version>，先补上再打包。' }
# iss 里是 VersionInfoVersion={#MyAppVersion}.0，四段式要求前三段必须完整，
# 写成 "1.8" 会拼出非法的 "1.8.0" 导致 ISCC 报错
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "csproj 的 <Version> 必须是三段式（当前是 '$version'），否则安装包版本号拼不出来。"
}
Say "==> 版本 $version"

# ---------- 2. 定位 ISCC ----------
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $null
foreach ($c in $candidates) { if (Test-Path $c) { $iscc = $c; break } }
if (-not $iscc) {
    throw "找不到 ISCC.exe。先装 Inno Setup 6：`n    winget install -e --id JRSoftware.InnoSetup --source winget"
}
Say "==> ISCC $iscc"

# ---------- 3. 发布（RID 与 SelfContained 已固定在 csproj 里，不用带参数）----------
if (-not $SkipPublish) {
    # dotnet publish -o 是「合并」语义：不先清空，上一次的旧 dll / 旧 exe / 已删除的文件会留在 dist 里，
    # 一起被打进安装包，出现「界面新、DLL 旧」这种查起来极费劲的组合
    Say '==> 清理 dist'
    Remove-Tree $dist
    Say '==> dotnet publish'
    & dotnet publish $project -c Release -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path (Join-Path $dist '暖序.exe'))) { throw 'dist 里没有 暖序.exe，发布没成功。' }

# ---------- 4. 瘦身：删掉用不上的语言资源与别的架构 loader ----------
Say '==> 清理无用文件'
foreach ($d in @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-Hant')) {
    Remove-Tree (Join-Path $dist $d)
}
foreach ($d in @('win-x86', 'win-arm64')) {
    Remove-Tree (Join-Path $dist "runtimes\$d")
}
# 开发机跑出来的 WebView2 缓存（约 80MB），绝不能进安装包
Remove-Tree (Join-Path $dist '暖序.exe.WebView2')
# 递归清理：原来是 -File 非递归，子目录里的 pdb/xml 会漏进包里
Get-ChildItem -Path $dist -Recurse -File -Include '*.pdb', '*.xml' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-File $_.FullName }

$payloadMB = [math]::Round(((Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Say "    载荷 $payloadMB MB"

# ---------- 5. 外部依赖：缺了才下载 ----------
Say '==> 检查依赖'
$redistDir = Join-Path $installer 'redist'
if (-not (Test-Path $redistDir)) { New-Item -ItemType Directory -Path $redistDir | Out-Null }

$bootstrap = Join-Path $redistDir 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $bootstrap)) {
    Say '    下载 WebView2 在线引导器…'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrap -UseBasicParsing
}

$isl = Join-Path $installer 'ChineseSimplified.isl'
if (-not (Test-Path $isl)) {
    Say '    下载简体中文语言文件…'
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl' -OutFile $isl -UseBasicParsing
    # Inno Setup 要求语言文件是 UTF-8 带 BOM
    $bytes = [IO.File]::ReadAllBytes($isl)
    if (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
        [IO.File]::WriteAllBytes($isl, ([byte[]](0xEF, 0xBB, 0xBF) + $bytes))
    }
}

# ---------- 5b. 静态断言：卸载必须清掉开机自启 ----------
# 自启值写在 HKCU 的 Run 键里，不在 [Registry] 段，卸载器不会自动清。
# 这里做一次文本断言，防止以后重构 .iss 时把它删掉——删了就是卸载后每次开机一条死项。
Say '==> 校验卸载清理'
$issText = [IO.File]::ReadAllText($iss)
$needles = @(
    "RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NuanXu')",
    "RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'NuanXu')"
)
foreach ($needle in $needles) {
    if ($issText.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
        throw "暖序.iss 缺少开机自启清理语句：$needle"
    }
}

# ---------- 6. 编译安装包 ----------
Say '==> 编译安装程序（LZMA2 压缩 145MB 左右，需要 1~3 分钟）'
if (-not (Test-Path $release)) { New-Item -ItemType Directory -Path $release | Out-Null }
& $iscc '/Qp' "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

# ---------- 7. 绿色版 zip ----------
Say '==> 打包绿色版'
$stage = Join-Path $env:TEMP ("暖序-$version")
Remove-Tree $stage
Copy-Item $dist $stage -Recurse

$zip = Join-Path $release ("暖序-$version-绿色版.zip")
Remove-File $zip
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Remove-Tree $stage

# ---------- 8. 汇总 ----------
$setup = Get-ChildItem $release -Filter "*$version*安装程序.exe" | Select-Object -First 1
Say ''
Say '========== 打包完成 =========='
if ($setup) {
    Say ("安装程序  {0}  {1} MB" -f $setup.Name, [math]::Round($setup.Length / 1MB, 1))
}
Say ("绿色版    {0}  {1} MB" -f (Split-Path $zip -Leaf), [math]::Round((Get-Item $zip).Length / 1MB, 1))
Say "输出目录  $release"

# 控制台是 GBK，中文在管道里会乱码；另外存一份 UTF-8 报告，便于事后核对
$reportPath = Join-Path $release 'build-report.txt'
[IO.File]::WriteAllLines($reportPath, $report, (New-Object Text.UTF8Encoding($false)))
