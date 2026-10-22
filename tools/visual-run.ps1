# 啟動 3D 視景(Windows;docs/design/visual-client.md 第 6–7 節)。多螢幕:每台顯示器各啟動一個程序訂閱同一狀態流,例如
#   .\tools\visual-run.ps1 -View bridge -Monitor 0
#   .\tools\visual-run.ps1 -View portWing -Monitor 1
#   .\tools\visual-run.ps1 -View stbdWing -Monitor 2
# 品質 low 以 Compatibility 渲染器啟動(--rendering-method gl_compatibility);mid/high 用 Forward+;auto(預設)讀 build\visual\quality.json
# (視景在偵測、自動降級、離開時寫入):manualOverride 為 low,或無覆寫且 lastAutoTier 為 low → Compatibility,否則 Forward+
# (與 Visual.Shared QualityPersistence.StartupRenderer 同一規則;第一次啟動沒有檔案 → Forward+,偵測為 low 後下次自動改用 Compatibility)。
# Godot 的使用者資料以 APPDATA/LOCALAPPDATA 環境變數導向 .cache\godot\appdata(只影響本程序);quality.json、quality.log、smoke.json 在 build\visual\。
param(
  [string]$Godot = $env:SIMOSA_GODOT,
  [ValidateSet('bridge','portWing','stbdWing','aft','overview')][string]$View = 'bridge',
  [int]$Monitor = 0,
  [string]$Ws = 'ws://127.0.0.1:8765',
  [ValidateSet('auto','low','mid','high')][string]$Quality = 'auto',
  [string]$Area = '',
  [string]$Ship = 'FSB1',
  [ValidateSet('full','ballast')][string]$Loading = 'full',
  [ValidateSet('auto','day','dusk','night')][string]$DayNight = 'auto',
  [string]$StartUtc = '',
  [switch]$Fullscreen,
  [switch]$NoHud,
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Godot) {
  $cand = Get-ChildItem -Path (Join-Path $root '.cache\godot') -Filter 'Godot_v4.*mono_win64*.exe' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*console*' } | Select-Object -First 1
  if ($cand) { $Godot = $cand.FullName }
}
if (-not $Godot -or -not (Test-Path $Godot)) { throw '找不到 Godot 4.5 .NET 執行檔;以 -Godot 或環境變數 SIMOSA_GODOT 指定' }
$godotData = Join-Path $root '.cache\godot\appdata'
New-Item -ItemType Directory -Force -Path (Join-Path $godotData 'roaming'), (Join-Path $godotData 'local') | Out-Null
$env:APPDATA = Join-Path $godotData 'roaming'
$env:LOCALAPPDATA = Join-Path $godotData 'local'
$proj = Join-Path $root 'src\Visual.Godot'
if ($Build -or -not (Test-Path (Join-Path $proj '.godot\mono\temp\bin\Debug\SimosaBRM.Visual.Godot.dll'))) {
  dotnet build (Join-Path $proj 'Visual.Godot.csproj') -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失敗' }
}
$engineArgs = @('--path', $proj)
$useCompat = ($Quality -eq 'low')
if ($Quality -eq 'auto') {
  $qj = Join-Path $root 'build\visual\quality.json'
  if (Test-Path $qj) {
    try {
      $q = Get-Content $qj -Raw | ConvertFrom-Json
      $ov = ("$($q.manualOverride)").Trim().ToLowerInvariant()
      $la = ("$($q.lastAutoTier)").Trim().ToLowerInvariant()
      if ($ov -eq 'low' -or (($ov -eq '' -or $ov -eq 'auto') -and $la -eq 'low')) { $useCompat = $true }
      Write-Host "quality.json:manualOverride=$ov lastAutoTier=$la → 渲染器 $(if ($useCompat) { 'Compatibility' } else { 'Forward+' })"
    } catch { Write-Warning "無法讀取 $qj($_);以 Forward+ 啟動" }
  }
}
if ($useCompat) { $engineArgs += @('--rendering-method', 'gl_compatibility') }
$userArgs = @("--view=$View", "--monitor=$Monitor", "--ws=$Ws", "--quality=$Quality", "--data-dir=$root", "--ship=$Ship", "--loading=$Loading", "--daynight=$DayNight")
if ($Area) { $userArgs += "--area=$Area" }
if ($StartUtc) { $userArgs += "--start-utc=$StartUtc" }
if ($Fullscreen) { $userArgs += '--fullscreen' }
if ($NoHud) { $userArgs += '--no-hud' }
& $Godot @engineArgs -- @userArgs
