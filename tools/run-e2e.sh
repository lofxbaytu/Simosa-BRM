#!/usr/bin/env bash
# 端對端測試一鍵腳本(docs/design/testing.md 第 4 節):建置網頁(Vite → build/web/instruments)與 .NET(SimCore.Host、StaticWeb、
# Sensors.Radar.Host;Release)→ 安裝 tests/e2e 套件 → Playwright(真實 Chromium headless + 真實核心/雷達/靜態網頁)→ 摘要。
# 全部輸出留在 build/e2e/(test-results、trace、截圖、report、results.json、records、media、logs;CLAUDE.md),結束碼 = Playwright 結束碼。
#
# 用法:bash tools/run-e2e.sh [--skip-build | --skip-web | --skip-dotnet] [--sln] [-- <playwright test 參數,例如 --grep 雷達 或 specs/01-bridge.spec.ts>]
#   --skip-build   不建置(用現有 build/web/instruments 與 build/bin/*/Release)
#   --sln          .NET 改建整個 SimosaBRM.sln(預設只建端對端需要的三個專案,較快且不受其他專案影響)
# 環境變數:
#   E2E_CHROMIUM              Chromium 執行檔(未設則用 Playwright 管理的 chromium:PLAYWRIGHT_BROWSERS_PATH 預設 /opt/pw-browsers 或 .cache/pw-browsers)
#   PLAYWRIGHT_BROWSERS_PATH  Playwright 瀏覽器目錄(CI 以 npx playwright install chromium --with-deps 裝到 .cache/pw-browsers)
#   E2E_PORT_BASE             固定埠(base..base+4);未設則取空埠
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if ! command -v dotnet >/dev/null 2>&1 && [[ -f /etc/profile.d/dotnet.sh ]]; then . /etc/profile.d/dotnet.sh; fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$ROOT/.cache/nuget/http}" NUGET_PLUGINS_CACHE_PATH="${NUGET_PLUGINS_CACHE_PATH:-$ROOT/.cache/nuget/plugins}" NUGET_SCRATCH="${NUGET_SCRATCH:-$ROOT/.cache/nuget/scratch}"  # NuGet HTTP/外掛快取與暫存只能用環境變數導回專案內(nuget.config 的註解)
# Playwright 的 TypeScript 轉譯快取預設在系統暫存目錄,改放專案內(CLAUDE.md)
export PWTEST_CACHE_DIR="${PWTEST_CACHE_DIR:-$ROOT/.cache/playwright-transform}"
if [[ -z "${E2E_CHROMIUM:-}" && -z "${PLAYWRIGHT_BROWSERS_PATH:-}" ]]; then
  if [[ -d /opt/pw-browsers ]]; then export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers
  elif [[ -d "$ROOT/.cache/pw-browsers" ]]; then export PLAYWRIGHT_BROWSERS_PATH="$ROOT/.cache/pw-browsers"
  else echo "[e2e] 未設定 PLAYWRIGHT_BROWSERS_PATH 且找不到 /opt/pw-browsers 或 .cache/pw-browsers;請先 PLAYWRIGHT_BROWSERS_PATH=.cache/pw-browsers npx playwright install chromium" >&2; fi
fi

SKIP_WEB=0; SKIP_DOTNET=0; USE_SLN=0; PW_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-build) SKIP_WEB=1; SKIP_DOTNET=1 ;;
    --skip-web) SKIP_WEB=1 ;;
    --skip-dotnet) SKIP_DOTNET=1 ;;
    --sln) USE_SLN=1 ;;
    --) shift; PW_ARGS=("$@"); break ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) PW_ARGS+=("$1") ;;
  esac
  shift
done

step() { echo; echo "=== [e2e] $1"; }
t_all=$SECONDS
mkdir -p build/e2e build/tmp

if [[ $SKIP_WEB -eq 0 ]]; then
  step "建置網頁儀器(src/Instruments.Web → build/web/instruments)"
  ( cd src/Instruments.Web
    [[ -d node_modules ]] || pnpm install --frozen-lockfile
    pnpm build )
fi

if [[ $SKIP_DOTNET -eq 0 ]]; then
  if [[ $USE_SLN -eq 1 ]]; then
    step "建置 SimosaBRM.sln(Release)"
    dotnet build SimosaBRM.sln -c Release --nologo -v q
  else
    step "建置 SimCore.Host、StaticWeb、Sensors.Radar.Host(Release)"
    for p in src/SimCore.Host tools/launch/StaticWeb src/Sensors.Radar.Host; do dotnet build "$p" -c Release --nologo -v q; done
  fi
fi

step "安裝端對端測試套件(tests/e2e;pnpm store 在 .cache/pnpm)"
( cd tests/e2e; [[ -d node_modules ]] || pnpm install --frozen-lockfile )

step "Playwright 端對端測試(輸出 build/e2e/)"
set +e
( cd tests/e2e && pnpm exec playwright test "${PW_ARGS[@]}" )
rc=$?
set -e

step "摘要"
if [[ -f build/e2e/results.json ]]; then
  node -e '
    const r = JSON.parse(require("fs").readFileSync("build/e2e/results.json", "utf8"));
    const s = r.stats || {};
    const names = [];
    const walk = (suite, prefix) => {
      for (const sp of suite.specs || []) for (const t of sp.tests || []) {
        // t.status:expected(通過;expectedStatus=failed 時為 test.fail 的預期失敗 xfail)| unexpected | flaky(重試後通過)| skipped
        const tag = t.status === "expected" ? (t.expectedStatus === "failed" ? "xfail " : "ok    ") : t.status === "flaky" ? "flaky " : t.status === "skipped" ? "skip  " : "FAIL  ";
        names.push(`${tag} ${prefix}${sp.title}`);
      }
      for (const c of suite.suites || []) walk(c, prefix);
    };
    for (const su of r.suites || []) walk(su, `${su.title} › `);
    console.log(names.join("\n"));
    const xfail = names.filter((n) => n.startsWith("xfail")).length;
    console.log(`案例 ${(s.expected|0) + (s.unexpected|0) + (s.flaky|0) + (s.skipped|0)}:通過 ${(s.expected|0) - xfail}、預期失敗(已知缺陷 test.fail) ${xfail}、失敗 ${s.unexpected|0}、不穩(重試後通過) ${s.flaky|0}、略過 ${s.skipped|0};Playwright 耗時 ${((s.duration||0)/1000).toFixed(1)} s`);
  '
else
  echo "(沒有 build/e2e/results.json:Playwright 未執行到報告階段,見上方輸出與 build/e2e/logs/)"
fi
echo "全程 $((SECONDS - t_all)) s;報告 build/e2e/report/index.html(pnpm --dir tests/e2e report),失敗截圖/trace build/e2e/test-results/,核心/雷達/網頁 log build/e2e/logs/"
exit $rc
