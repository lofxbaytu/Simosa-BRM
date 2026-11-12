<#
.SYNOPSIS
  simosa-pkg 包裝腳本(Windows):以專案內建置輸出執行簽章更新包、紀錄包與直連同步工具。

.DESCRIPTION
  規劃書第 5.5、9.1 節;設計文件 docs/design/packaging-sync.md。
  - 未建置時先以 Release 建置 src/Tools.Packaging.Cli(輸出在 build\bin\,不寫 C:\)。
  - 預設安裝根目錄為本腳本所在的專案/安裝根目錄(D: 或 G: 皆可;可用 -Root 或環境變數 SIMOSA_INSTALL_ROOT 覆寫;不會落到 C:\)。
  - 其餘參數原樣傳給 simosa-pkg,例如:
      .\tools\simosa-pkg.ps1 status
      .\tools\simosa-pkg.ps1 verify D:\usb\simosa-transfer\to-ship\upd.sbupd
      .\tools\simosa-pkg.ps1 sync --ship FSB1 --cert 0123ABCD... --push .\build\packages\params-2026.10.2.sbupd
      .\tools\simosa-pkg.ps1 serve --listen 192.168.77.11

.EXAMPLE
  .\tools\simosa-pkg.ps1 -Root "D:\Simosa BRM" status --json
#>
param(
    [string]$Root,
    [switch]$Rebuild,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $repo 'build\bin\Tools.Packaging.Cli\Release\net8.0\simosa-pkg.dll'

# 所有快取留在專案資料夾內(CLAUDE.md)
$env:DOTNET_CLI_HOME = Join-Path $repo '.cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.cache\nuget\packages'
# NuGet 的 HTTP 快取、外掛快取與暫存只能用環境變數導回專案內(nuget.config 無對應鍵;預設在 C:\Users\…\AppData\Local\NuGet 與 %TEMP%)
if (-not $env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.cache\nuget\http' }
if (-not $env:NUGET_PLUGINS_CACHE_PATH) { $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $repo '.cache\nuget\plugins' }
if (-not $env:NUGET_SCRATCH) { $env:NUGET_SCRATCH = Join-Path $repo '.cache\nuget\scratch' }
# 暫存檔 → 專案內 build\tmp(只影響本程序與子程序;SIMOSA_SYSTEM_TEMP=1 時留在 Windows 預設的 C:\Users\…\Temp,需經同意)
if ($env:SIMOSA_SYSTEM_TEMP -ne '1') { $simosaTmp = Join-Path $repo 'build\tmp'; New-Item -ItemType Directory -Force -Path $simosaTmp | Out-Null; $env:TEMP = $simosaTmp; $env:TMP = $simosaTmp }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if ($Rebuild -or -not (Test-Path $dll)) {
    Write-Host '建置 simosa-pkg(Release)…'
    dotnet build (Join-Path $repo 'src\Tools.Packaging.Cli\Tools.Packaging.Cli.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '建置失敗' }
}

# 未指定 -Root 也沒有 SIMOSA_INSTALL_ROOT 時不在此寫死磁碟:由 simosa-pkg 依執行檔所在位置決定
#(InstallLayout.DefaultRoot:安裝根目錄或專案根目錄 → D:\Simosa BRM → G:\Simosa BRM;不會落到 C:\)。
if ($Root) { $env:SIMOSA_INSTALL_ROOT = $Root }

dotnet $dll @Rest
exit $LASTEXITCODE
