$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'NuanXu\暖序.csproj'
$output = Join-Path $PSScriptRoot 'dist'

dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) {
    throw '暖序构建失败。请先安装 .NET 8 SDK，然后重新运行此脚本。'
}

Write-Host "构建完成：$output\暖序.exe"
