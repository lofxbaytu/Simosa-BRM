# Simosa BRM 開發環境設定(Windows 11)
# 用途:把所有工具的快取與輸出固定在 D:\Simosa BRM 之內,避免寫到 C:\。
# 執行:以系統管理員開 PowerShell,cd 到 D:\Simosa BRM 後執行 .\tools\setup-windows.ps1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "專案根目錄:$root"
$cache = Join-Path $root '.cache'
New-Item -ItemType Directory -Force -Path (Join-Path $cache 'nuget'), (Join-Path $cache 'pnpm'), (Join-Path $cache 'uv'), (Join-Path $cache 'playwright'), (Join-Path $cache 'dotnet-home'), (Join-Path $root 'build'), (Join-Path $root 'data\records') | Out-Null

# 使用者層級環境變數(只影響本機帳號;各工具因此把快取放在專案內)
$vars = @{
  'NUGET_PACKAGES'           = (Join-Path $cache 'nuget\packages')
  'DOTNET_CLI_HOME'          = (Join-Path $cache 'dotnet-home')
  'DOTNET_CLI_TELEMETRY_OPTOUT' = '1'
  'PNPM_HOME'                = (Join-Path $cache 'pnpm\home')
  'UV_CACHE_DIR'             = (Join-Path $cache 'uv')
  'PLAYWRIGHT_BROWSERS_PATH' = (Join-Path $cache 'playwright')
  'SIMOSA_BRM_ROOT'          = $root
}
foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'User'); Set-Item -Path "Env:$k" -Value $vars[$k] }
Write-Host '已設定使用者環境變數:' ($vars.Keys -join ', ')

# 檢查工具
function Check($name, $cmd) { try { $v = & $cmd 2>$null | Select-Object -First 1; Write-Host ("  {0,-12} {1}" -f $name, $v) } catch { Write-Host ("  {0,-12} 未安裝" -f $name) -ForegroundColor Yellow } }
Write-Host '工具版本:'
Check 'dotnet'  { dotnet --version }
Check 'node'    { node --version }
Check 'pnpm'    { pnpm --version }
Check 'uv'      { uv --version }
Check 'git'     { git --version }
Check 'git-lfs' { git lfs version }

Write-Host ''
Write-Host '安裝建議(皆可指定安裝到 D:\):'
Write-Host '  .NET 8 SDK      : winget install Microsoft.DotNet.SDK.8  (安裝程式路徑可選 D:\Tools\dotnet)'
Write-Host '  Node.js 22 LTS  : winget install OpenJS.NodeJS.LTS;  然後 corepack enable pnpm'
Write-Host '  uv              : winget install astral-sh.uv'
Write-Host '  Git + LFS       : winget install Git.Git; git lfs install'
Write-Host '  Godot 4.5 .NET 或 Unity 6:安裝到 D:\Tools\,專案位於 src\Visual.*'
Write-Host ''
Write-Host '完成。之後在此資料夾執行:dotnet build、pnpm install、uv sync 時,快取與輸出都會留在 D:\Simosa BRM 內。'
