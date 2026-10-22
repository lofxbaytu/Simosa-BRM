# 3D 視景無頭驗證(Windows;與 visual-headless-check.sh 相同流程):
# dotnet build 視景專案 → Godot --headless --import → --headless 煙霧場景(假狀態序列,N 幀)→ build\visual\smoke.json
# Godot 的編輯器/使用者資料以 APPDATA/LOCALAPPDATA 環境變數導向專案內 .cache\godot\(只影響本程序),不寫到 C:\Users。
# 用法:.\tools\visual-headless-check.ps1 [-Godot <Godot_v4.5.1-stable_mono_win64.exe>] [-Frames 200] [-Quality low|mid|high] [-Export]
param(
  [string]$Godot = $env:SIMOSA_GODOT,
  [int]$Frames = 200,
  [ValidateSet('low','mid','high')][string]$Quality = 'low',
  [switch]$Export
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Godot) {
  $cand = Get-ChildItem -Path (Join-Path $root '.cache\godot') -Filter 'Godot_v4.*mono_win64*.exe' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*console*' } | Select-Object -First 1
  if ($cand) { $Godot = $cand.FullName }
}
if (-not $Godot -or -not (Test-Path $Godot)) { throw '找不到 Godot 4.5 .NET 執行檔;以 -Godot <路徑> 或環境變數 SIMOSA_GODOT 指定,或放在 .cache\godot\<版本>\ 下(旁邊放 _sc_ 空檔即為自含模式)' }
$godotData = Join-Path $root '.cache\godot\appdata'
New-Item -ItemType Directory -Force -Path (Join-Path $godotData 'roaming'), (Join-Path $godotData 'local'), (Join-Path $root 'build\visual') | Out-Null
$env:APPDATA = Join-Path $godotData 'roaming'
$env:LOCALAPPDATA = Join-Path $godotData 'local'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet-home' }
$proj = Join-Path $root 'src\Visual.Godot'
$log = Join-Path $root 'build\visual'

Write-Host "== Godot:$Godot"
& $Godot --headless --version | Select-Object -Last 1
Write-Host '== dotnet build(Debug)'
dotnet build (Join-Path $proj 'Visual.Godot.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失敗' }
Write-Host '== 匯入專案(--import)'
& $Godot --headless --path $proj --import *> (Join-Path $log 'import.log')
Write-Host "== 煙霧場景($Frames 幀,品質 $Quality)"
& $Godot --headless --path $proj -- "--smoke=$Frames" "--quality=$Quality" "--data-dir=$root" "--smoke-out=$(Join-Path $log 'smoke.json')" *> (Join-Path $log 'smoke.log')
$rc = $LASTEXITCODE
Get-Content (Join-Path $log 'smoke.log') | Where-Object { $_ -match '^\[visual\]|^\[quality\]|ERROR|Unhandled' }
if (Test-Path (Join-Path $log 'smoke.json')) {
  $r = Get-Content (Join-Path $log 'smoke.json') -Raw | ConvertFrom-Json
  Write-Host ("smoke.json:ok={0} frames={1} mean={2:N2} ms max={3:N2} ms areas={4} renderer={5}" -f $r.ok, $r.frames.Count, $r.meanFrameTime_ms, $r.maxFrameTime_ms, ($r.areas -join ','), $r.renderer)
}
if ($Export) {
  Write-Host '== 匯出(需匯出範本;無法取得時為待辦)'
  & $Godot --headless --path $proj --export-release 'Windows Desktop' (Join-Path $root 'build\visual\export\windows\SimosaBRM.Visual.exe') *> (Join-Path $log 'export.log')
  if ($LASTEXITCODE -ne 0) { Write-Host "匯出失敗(通常為缺少匯出範本),見 $log\export.log" }
}
Write-Host "== 結束碼 $rc(0 = 煙霧測試 ok)"
exit $rc
