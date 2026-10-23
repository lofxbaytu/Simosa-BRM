<#
.SYNOPSIS
  發行組裝(Windows,win-x64;Linux/容器版為 build-release.sh,參數相同):規劃書第 5.4–5.5 節、第 11.2 節、第 12.1 節 M4 交付。
.DESCRIPTION
  1) dotnet build SimosaBRM.sln -c Release;2) 網頁 pnpm build;3) dotnet publish(framework-dependent,需 .NET 8 執行階段):
     SimCore.Host → app\simcore、Sensors.Radar.Host → app\radar、simosa-web → app\web-server、simosa-pkg → tools\updater
     (simosa-pkg 放在 app\ 之外:套用軟體更新包時 app\ 整個被替換,執行中的更新工具不可位於其中;docs\design\packaging-sync.md 第 5 節;
      Gateway.Nmea 為類別庫隨 SimCore.Host 發布;Python 校正工具不打包,附說明於 docs\);
  4) 網頁 → app\web、啟動腳本 → app\launch、data\(排除 ships\*\confidential\)、contracts\、docs\、config\ 範例;
  5) Godot 視景:有 Godot 與 Windows 匯出範本時 --export-release "Windows Desktop" → app\visual\,否則略過並記入清單註記;
  6) simosa-pkg pack 把 app\ 包成簽章更新包 packages\simosa-<版本>-software.sbupd(開發用 PEM 金鑰 build\release\dev-keys\,僅供測試);
  7) 發行清單 manifest.json(版本、git 版次、每檔 SHA-256、元件版本)→ release verify、更新包 verify。
  所有輸出留在專案內(build\release\、.cache\),不寫 C:\;不執行 git 指令(版次由 -Git 或直接讀 .git\HEAD)。
.EXAMPLE
  .\tools\build-release.ps1 -Version 2026.11.1 -Git (git rev-parse HEAD)
  .\tools\build-release.ps1 -Version 2026.11.1 -SkipGodot -Zip
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Git,
    [string]$Branch,
    [string]$Rid = 'win-x64',
    [string]$Out = 'build\release',
    [switch]$SkipWeb, [switch]$SkipGodot, [string]$Godot, [switch]$NoPackage, [switch]$Zip
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
if (-not $Version) { $Version = '0.{0:yyyyMMdd}.{1}' -f (Get-Date).ToUniversalTime(), [int](Get-Date).ToUniversalTime().ToString('HHmm') }
if ($Version -notmatch '^[0-9]+(\.[0-9]+){0,3}$') { throw "-Version 須為 1–4 段數字(與更新包版本規則相同):$Version" }

# git 版次:不執行 git,直接讀 .git\HEAD → refs\heads\<分支> 或 packed-refs
if (-not $Git -and (Test-Path '.git\HEAD')) {
    $head = (Get-Content '.git\HEAD' -Raw).Trim()
    if ($head.StartsWith('ref: ')) {
        $ref = $head.Substring(5); if (-not $Branch) { $Branch = $ref -replace '^refs/heads/', '' }
        $refFile = Join-Path '.git' ($ref -replace '/', '\')
        if (Test-Path $refFile) { $Git = (Get-Content $refFile -Raw).Trim() }
        elseif (Test-Path '.git\packed-refs') { $line = Get-Content '.git\packed-refs' | Where-Object { $_ -match "^([0-9a-f]+) $([regex]::Escape($ref))$" } | Select-Object -First 1; if ($line) { $Git = $line.Split(' ')[0] } }
    } else { $Git = $head }
}

# 工具鏈與快取(全部留在專案內)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '找不到 dotnet SDK' }
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.cache\nuget\packages' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:SIMOSA_BRM_ROOT = $root
$outDir = Join-Path $root (Join-Path $Out $Version)
$keys = Join-Path $root (Join-Path $Out 'dev-keys')
$pkgDir = Join-Path $outDir 'packages'
$notes = New-Object System.Collections.Generic.List[string]
$components = New-Object System.Collections.Generic.List[string]
function Note([string]$m) { Write-Host "  註記:$m"; $notes.Add($m) }
function Step([string]$m) { Write-Host ''; Write-Host "== $m" }
function Run([string]$exe, [string[]]$a) { & $exe @a; if ($LASTEXITCODE -ne 0) { throw "$exe $($a -join ' ') 失敗(結束碼 $LASTEXITCODE)" } }

Write-Host "Simosa BRM 發行組裝 $Version(git $Git $Branch;$Rid)→ $outDir"
if (Test-Path $outDir) { Write-Host "  清除既有 $outDir"; Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'app'), (Join-Path $outDir 'tools'), $pkgDir | Out-Null

Step '1/7 dotnet build SimosaBRM.sln -c Release'
Run 'dotnet' @('build', 'SimosaBRM.sln', '-c', 'Release', '-nologo', '-v', 'q', '-clp:ErrorsOnly')

Step '2/7 網頁 pnpm build(src\Instruments.Web → build\web\instruments)'
$webVersion = ((Get-Content 'src\Instruments.Web\package.json' -Raw | ConvertFrom-Json).version)
if ($SkipWeb) { Note '網頁未重新建置(-SkipWeb),沿用 build\web\instruments' }
elseif (Get-Command pnpm -ErrorAction SilentlyContinue) { Push-Location 'src\Instruments.Web'; try { Run 'pnpm' @('build') } finally { Pop-Location }; Write-Host '  pnpm build 完成' }
else { Note '找不到 pnpm,網頁沿用既有 build\web\instruments' }
if (-not (Test-Path 'build\web\instruments\index.html')) { throw '沒有網頁輸出 build\web\instruments\index.html' }

Step "3/7 dotnet publish(framework-dependent,$Rid)"
function Publish([string]$proj, [string]$dest) {
    Run 'dotnet' @('publish', $proj, '-c', 'Release', '-r', $Rid, '--self-contained', 'false', '-o', $dest, '-nologo', '-v', 'q')
    $n = (Get-ChildItem -Recurse -File $dest).Count
    Write-Host "  $([System.IO.Path]::GetFileNameWithoutExtension($proj)) → $($dest.Substring($outDir.Length + 1))($n 個檔案)"
}
Publish 'src\SimCore.Host\SimCore.Host.csproj' (Join-Path $outDir 'app\simcore')
Publish 'src\Sensors.Radar.Host\Sensors.Radar.Host.csproj' (Join-Path $outDir 'app\radar')
Publish 'tools\launch\StaticWeb\StaticWeb.csproj' (Join-Path $outDir 'app\web-server')
Publish 'src\Tools.Packaging.Cli\Tools.Packaging.Cli.csproj' (Join-Path $outDir 'tools\updater')
Get-ChildItem -Recurse -File -Include '*.pdb' (Join-Path $outDir 'app'), (Join-Path $outDir 'tools') | Remove-Item
$pkgDll = Join-Path $outDir 'tools\updater\simosa-pkg.dll'
function Pkg([string[]]$a) { & dotnet $pkgDll @a; if ($LASTEXITCODE -ne 0) { throw "simosa-pkg $($a[0]) 失敗(結束碼 $LASTEXITCODE)" } }
$components.AddRange([string[]]@("simcore=$Version=app/simcore", "radar=$Version=app/radar", "web-server=$Version=app/web-server", "simosa-pkg=$Version=tools/updater", "gateway-nmea=$Version=app/simcore=類別庫,隨 SimCore.Host 發布"))

Step '4/7 網頁、啟動腳本、資料、契約、文件、設定範例'
Copy-Item -Recurse 'build\web\instruments' (Join-Path $outDir 'app\web')
$components.Add("web=$webVersion=app/web")
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'app\launch') | Out-Null
Copy-Item 'tools\launch\*.ps1', 'tools\launch\*.sh' (Join-Path $outDir 'app\launch')
# data\:排除 ships\*\confidential\(機密係數與試俥原始檔不進發行版;規劃書第 11.4 節)
$dataOut = Join-Path $outDir 'data'
Get-ChildItem -Recurse -File 'data' | Where-Object { $_.FullName -notmatch '[\\/]ships[\\/][^\\/]+[\\/]confidential([\\/]|$)' } | ForEach-Object {
    $rel = $_.FullName.Substring((Join-Path $root 'data').Length + 1)
    $dst = Join-Path $dataOut $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
    Copy-Item $_.FullName $dst
}
foreach ($d in @('charts', 'environment')) { New-Item -ItemType Directory -Force -Path (Join-Path $dataOut $d) | Out-Null }
if (Get-ChildItem -Recurse -Directory $dataOut | Where-Object { $_.Name -eq 'confidential' }) { throw 'data\ 複製後仍含 confidential\' }
$paramVersion = ((Select-String -Path 'data\parameters\company-procedures.yaml' -Pattern '^version:\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value)
$components.Add("data=$paramVersion=data=參數集 company-procedures.yaml 版次;ships/*/confidential 已排除")
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'contracts') | Out-Null
Copy-Item 'src\Contracts\*' (Join-Path $outDir 'contracts')
$components.Add("contracts=$Version=contracts")
foreach ($d in @('docs\design', 'docs\ops', 'docs\驗收', 'docs\訓練', 'docs\components')) { New-Item -ItemType Directory -Force -Path (Join-Path $outDir $d) | Out-Null }
Copy-Item 'docs\Simosa-BRM-操船模擬器規劃書.md', 'README.md' (Join-Path $outDir 'docs')
Copy-Item 'docs\design\*.md' (Join-Path $outDir 'docs\design')
if (Test-Path 'docs\ops') { Copy-Item 'docs\ops\*.md' (Join-Path $outDir 'docs\ops') }
if (Test-Path 'docs\驗收') { Copy-Item 'docs\驗收\*.md' (Join-Path $outDir 'docs\驗收') }
if (Test-Path 'docs\訓練') { Copy-Item 'docs\訓練\*.md' (Join-Path $outDir 'docs\訓練') }
Copy-Item 'src\Tools.Calibration\README.md' (Join-Path $outDir 'docs\components\校正工具-Tools.Calibration.md')
Copy-Item 'src\Instruments.Web\README.md' (Join-Path $outDir 'docs\components\網頁儀器-Instruments.Web.md')
Copy-Item 'src\Instruments.Web\RADAR.md' (Join-Path $outDir 'docs\components\雷達PPI-RADAR.md')
Copy-Item 'src\SimCore\README.md' (Join-Path $outDir 'docs\components\模擬核心-SimCore.md')
Copy-Item 'src\Contracts\README.md' (Join-Path $outDir 'docs\components\契約-Contracts.md')
$components.Add("docs=$Version=docs")
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'config') | Out-Null
Copy-Item 'config\*.json', 'config\README.md' (Join-Path $outDir 'config')
Note 'Python 校正工具(src/Tools.Calibration)不隨發行版散布;係數檔 data/ships/*/coefficients.*.json 已由該工具產生並隨 data/ 發布,說明見 docs/components/校正工具-Tools.Calibration.md'
@"
Simosa BRM 發行版 $Version($Rid,framework-dependent:需 .NET 8 執行階段)git $Git $Branch
app\simcore      SimCore.Host(50 Hz 核心、WebSocket 8765、UDP 多播、NMEA)   app\radar        Sensors.Radar.Host(雷達/ARPA 8766)
app\web          網頁:index.html 儀器、instructor.html 教官站(?mode=simple 簡化教官模式)、radar.html、report.html
app\web-server   simosa-web 靜態網頁伺服                                  app\launch       start-ship-laptop / start-instructor-laptop / stop-all
app\visual       Godot 視景匯出檔(有匯出範本時)                           tools\updater    simosa-pkg(更新包、紀錄包、直連同步;放在 app\ 之外)
data\            船模、情境、區域、參數集、視景對照(不含 ships\*\confidential)   contracts\       JSON Schema   config\  設定範例(無金鑰)
docs\            規劃書、設計文件、ops\安裝與啟動.md、驗收\需求追溯表.md     packages\        簽章更新包(開發金鑰簽章,僅供測試)
manifest.json    發行清單(每檔 SHA-256):安裝前 simosa-pkg release verify --dir <本目錄>
安裝步驟:docs\ops\安裝與啟動.md
"@ | Set-Content -Path (Join-Path $outDir 'README.txt') -Encoding UTF8

Step '5/7 Godot 視景匯出'
if ($SkipGodot) {
    Note '視景未匯出(-SkipGodot);以 Godot 編輯器 --path src\Visual.Godot 或 tools\visual-run.ps1 執行'; $components.Add("visual=$Version==略過:-SkipGodot")
} else {
    if (-not $Godot) { $Godot = $env:GODOT; if (-not $Godot) { $Godot = $env:SIMOSA_GODOT } }
    if (-not $Godot) { $c = Get-ChildItem -Path (Join-Path $root '.cache\godot') -Recurse -Filter 'Godot_v4.*mono_win64.exe' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($c) { $Godot = $c.FullName } }
    $tplDir = Join-Path $root '.cache\godot\appdata\roaming\Godot\export_templates\4.5.1.stable.mono'
    $tpl = Join-Path $tplDir 'windows_release_x86_64.exe'
    $visOut = Join-Path $outDir 'app\visual\SimosaBRM.Visual.exe'
    if (-not $Godot -or -not (Test-Path $Godot)) {
        Note '視景未匯出:找不到 Godot 4.5.1 .NET 執行檔(-Godot 或 GODOT 環境變數;.cache\godot\<版本>\)'; $components.Add("visual=$Version==略過:無 Godot")
    } elseif (-not (Test-Path $tpl)) {
        Note "視景未匯出:缺匯出範本 $tpl(取得 Godot_v4.5.1-stable_mono_export_templates.tpz 後放入該目錄;docs\design\visual-client.md 第 9 節);船上以 Godot 編輯器 --path src\Visual.Godot 執行"
        $components.Add("visual=$Version==略過:缺匯出範本")
    } else {
        # Godot 編輯器/使用者資料留在專案內(.cache\godot\appdata;visual-client.md 第 8 節)
        $env:APPDATA = Join-Path $root '.cache\godot\appdata\roaming'; $env:LOCALAPPDATA = Join-Path $root '.cache\godot\appdata\local'
        New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, (Split-Path $visOut), 'build\visual' | Out-Null
        Run 'dotnet' @('build', 'src\Visual.Godot\Visual.Godot.csproj', '-c', 'Release', '-nologo', '-v', 'q')
        & $Godot --headless --path 'src\Visual.Godot' --import *> 'build\visual\release-import.log'
        & $Godot --headless --path 'src\Visual.Godot' --export-release 'Windows Desktop' $visOut *> 'build\visual\release-export.log'
        if ($LASTEXITCODE -eq 0 -and (Test-Path $visOut)) { Write-Host "  視景已匯出 → app\visual\SimosaBRM.Visual.exe"; $components.Add("visual=$Version=app/visual=已匯出(Windows Desktop)") }
        else { Note '視景匯出失敗(見 build\visual\release-export.log)'; $components.Add("visual=$Version==匯出失敗"); Remove-Item -Recurse -Force (Join-Path $outDir 'app\visual') -ErrorAction SilentlyContinue }
    }
}

Step '6/7 簽章更新包(app\ → packages\;開發用 PEM 金鑰,僅供測試)'
$swPkg = Join-Path $pkgDir "simosa-$Version-software.sbupd"
if (-not $NoPackage) {
    New-Item -ItemType Directory -Force -Path $keys | Out-Null
    if (-not (Test-Path (Join-Path $keys 'dev-primary.key.pem'))) {
        Pkg @('keygen', '--out', (Join-Path $keys 'dev-primary'), '--name', '開發用主金鑰(僅供測試)', '--role', 'primary', '--holder', '開發團隊')
        Pkg @('keygen', '--out', (Join-Path $keys 'dev-backup'), '--name', '開發用備援金鑰(僅供測試)', '--role', 'backup', '--holder', '開發團隊')
        @"
本目錄的 PEM 金鑰由 tools\build-release.* 產生,僅供開發/測試:正式更新包必須以訓練負責人的硬體金鑰(--cert)簽章(規劃書第 5.5 節、docs\design\packaging-sync.md 第 2、8 節)。
不得放入系統映像、船上信任清單(config\trusted-keys\)或儲存庫。
"@ | Set-Content -Path (Join-Path $keys 'README.txt') -Encoding UTF8
    }
    Pkg @('pack', '--kind', 'software', '--version', $Version, '--summary', "Simosa BRM 發行版 $Version 軟體(app/)", '--change', "git $Git $Branch", '--change', "執行階段 $Rid",
          '--dir', "app=$(Join-Path $outDir 'app')", '--key', (Join-Path $keys 'dev-primary.key.pem'), '--out', $swPkg)
    $components.Add("software-package=$Version=packages/$(Split-Path -Leaf $swPkg)=開發金鑰簽章,僅供測試")
    Note 'packages/*.sbupd 以 build/release/dev-keys/ 的開發用 PEM 金鑰簽章,僅供測試;正式派送以硬體金鑰 --cert 重新 pack'
} else { Note '未產生更新包(-NoPackage)' }

Step '7/7 發行清單與核對'
$mf = @('release', 'manifest', '--dir', $outDir, '--version', $Version, '--runtime', $Rid)
if ($Git) { $mf += @('--git', $Git) }
if ($Branch) { $mf += @('--branch', $Branch) }
foreach ($c in $components) { $mf += @('--component', $c) }
foreach ($n in $notes) { $mf += @('--note', $n) }
Pkg $mf
Pkg @('release', 'verify', '--dir', $outDir)
if (-not $NoPackage) {
    $vr = Join-Path $root (Join-Path $Out ".verify-$Version")
    if (Test-Path $vr) { Remove-Item -Recurse -Force $vr }
    New-Item -ItemType Directory -Force -Path (Join-Path $vr 'config\trusted-keys') | Out-Null
    Copy-Item (Join-Path $keys 'dev-primary.pub.pem') (Join-Path $vr 'config\trusted-keys\dev-primary.pem')
    '{ "role": "ship", "shipId": "FSB1", "deviceId": "FSB1-VERIFY" }' | Set-Content -Path (Join-Path $vr 'config\packaging.json') -Encoding UTF8
    Pkg @('verify', $swPkg, '--root', $vr)
    Remove-Item -Recurse -Force $vr
}
if ($Zip) {
    $zipFile = Join-Path $root (Join-Path $Out "simosa-brm-$Version-$Rid.zip")
    if (Test-Path $zipFile) { Remove-Item $zipFile }
    Compress-Archive -Path $outDir -DestinationPath $zipFile
    Write-Host "  壓縮檔 $zipFile($([math]::Round((Get-Item $zipFile).Length / 1MB, 1)) MB)"
}
Write-Host ''
Write-Host "完成:$outDir;安裝步驟見 docs\ops\安裝與啟動.md"
