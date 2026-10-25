@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo 暖序需要 .NET 8 SDK 才能打包。
  echo 请先安装 .NET 8 SDK，然后再次双击此文件。
  echo 下载地址：https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1"
if errorlevel 1 pause
