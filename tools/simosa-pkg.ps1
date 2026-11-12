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

# 所有快取與暫存留在專案資料夾內(CLAUDE.md):共用 tools\launch\launch-common.ps1 的 Set-LaunchProjectEnv(.NET CLI 家目錄、NuGet 套件 / HTTP 快取 /
# 外掛快取 / 暫存、TEMP/TMP → build\tmp;已設定但指到專案外者改指回專案;SIMOSA_SYSTEM_TEMP=1 時暫存留在 Windows 預設位置,需經同意)。
# 在子範圍內載入:不把 launch-common.ps1 的 Set-StrictMode 與函式帶進本腳本。
& { . (Join-Path $repo 'tools\launch\launch-common.ps1'); Set-LaunchProjectEnv $repo }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# 專案根目錄以本腳本所在位置為準(不沿用可能指向另一份專案的環境變數 — 例如專案從 D: 複製到 G: 之後)
$env:SIMOSA_BRM_ROOT = $repo

if ($Rebuild -or -not (Test-Path $dll)) {
    Write-Host '建置 simosa-pkg(Release)…'
    dotnet build (Join-Path $repo 'src\Tools.Packaging.Cli\Tools.Packaging.Cli.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '建置失敗' }
}

# 安裝根目錄:-Root → 環境變數 SIMOSA_INSTALL_ROOT → 本腳本所在的專案根目錄(不寫死磁碟代號;不會落到 C:\)。
if ($Root) { $env:SIMOSA_INSTALL_ROOT = $Root }
elseif (-not $env:SIMOSA_INSTALL_ROOT) { $env:SIMOSA_INSTALL_ROOT = $repo }

dotnet $dll @Rest
exit $LASTEXITCODE
