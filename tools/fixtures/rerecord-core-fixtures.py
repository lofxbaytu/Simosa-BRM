#!/usr/bin/env python3
"""重錄核心紀錄夾具 src/Instruments.Web/tests/fixtures/record-*-core.jsonl(docs/修改指南.md 第 4 節;docs/design/scenario-library.md 第 7、14.5 節)。

何時要重錄:情境 YAML 改了(簡報在紀錄標頭 header.scenario.briefing,所以只改簡報文字也要)、它引用的參數集或區域檔改了、
物理或係數改動使狀態序列改變(此時是全部)。夾具由 C# 主控程序實跑產生,再「縮短」:保留 header、全部 event / summary / footer
與其他非狀態行,state 與 snapshot 只留既有夾具保留的那些 tick(長度與間距不可改:vitest 的 core-record.test.ts 與報告頁測試
對舊夾具 E01/E05/E07(300 s)、E08/E09(600 s)有逐項斷言)。歷來各輪都是一次性腳本,本檔把它固定下來。

每個情境的步驟:
  1. Host --validate(情境驗證器);
  2. Host --duration <秒> --timescale 10 --record build/tmp/fixtures/<id>.jsonl(無 WebSocket / UDP / NMEA / VHF / 錄影上傳);
  3. Host --replay(狀態雜湊、事件序列、summary 必須一致);
  4. 縮短後寫入夾具,並報告與舊夾具相比哪些種類的行變了(只改簡報時應該只有 header 與 footer 不同)。

用法(在儲存庫根目錄;先 dotnet build src/SimCore.Host -c Release):
  python3 tools/fixtures/rerecord-core-fixtures.py --all                 # 全部既有夾具
  python3 tools/fixtures/rerecord-core-fixtures.py E02_coastal_traffic_strait E05_pilot_traffic
  python3 tools/fixtures/rerecord-core-fixtures.py --changed             # 只重錄 YAML 與夾具標頭不一致的情境(以 Host --print-header 比對太慢,這裡比對 git 狀態)
  python3 tools/fixtures/rerecord-core-fixtures.py --new data/scenarios/E99_x.yaml   # 新情境:60 s、state 每 500 tick、snapshot 留 tick 500
  python3 tools/fixtures/rerecord-core-fixtures.py --all --check         # 不寫檔;有 header / footer 以外的差異時結束碼 1
選項:--jobs N(同時跑幾個,預設 2)、--scenario-dir <目錄>(改從別處讀情境檔,檔名仍為 <id>.yaml;核對本工具用)、--keep(保留完整紀錄)。
所有暫存在 build/tmp/fixtures/(專案資料夾內)。只用 Python 標準函式庫;Windows 與 Linux 皆可。
"""
from __future__ import annotations

import argparse
import collections
import concurrent.futures
import json
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / 'src' / 'Instruments.Web' / 'tests' / 'fixtures'
SCENARIOS = ROOT / 'data' / 'scenarios'
TMP = ROOT / 'build' / 'tmp' / 'fixtures'
HOST_DLL = ROOT / 'build' / 'bin' / 'SimCore.Host' / 'Release' / 'net8.0' / 'SimosaBRM.SimCore.Host.dll'
# 新夾具的預設:60 s(finalTick 3000)、state 自 tick 2 起每 500 tick、snapshot 留 tick 500
DEFAULT_FINAL_TICK = 3000
DEFAULT_STATE_TICKS = [2 + 500 * i for i in range(6)]
DEFAULT_SNAPSHOT_TICKS = [500]
# 比較新舊夾具時忽略的頂層欄位(每次錄製都不同):標頭的建立時刻、footer 的關閉時刻與檔案雜湊(檔案雜湊涵蓋標頭)
VOLATILE = {'header': {'createdUtc'}, 'footer': {'closedUtc', 'sha256'}}


def tick_of(line: dict):
    """state 行的 tick 在 state 物件內;snapshot / event / summary 在頂層。"""
    if line.get('kind') == 'state':
        return (line.get('state') or {}).get('tick')
    return line.get('tick')


def read_lines(path: pathlib.Path) -> list[dict]:
    with open(path, encoding='utf-8') as f:
        return [json.loads(line) for line in f if line.strip()]


class Plan:
    """一個夾具的錄製與縮短規則(由既有夾具推得,或用預設)。"""

    def __init__(self, fixture: pathlib.Path, scenario_id: str, final_tick: int, dt: float, state_ticks: list[int], snapshot_ticks: list[int]):
        self.fixture = fixture
        self.scenario_id = scenario_id
        self.final_tick = final_tick
        self.dt = dt
        self.state_ticks = set(state_ticks)
        self.snapshot_ticks = set(snapshot_ticks)

    @property
    def duration_s(self) -> int:
        return round(self.final_tick * self.dt)


def plan_from_fixture(fixture: pathlib.Path) -> Plan:
    lines = read_lines(fixture)
    header = lines[0]
    if header.get('kind') != 'header':
        raise SystemExit(f'{fixture.name}:第一行不是 header')
    footer = next((l for l in lines if l.get('kind') == 'footer'), None)
    if footer is None or 'finalTick' not in footer:
        raise SystemExit(f'{fixture.name}:沒有 footer.finalTick,無法推得錄製長度')
    return Plan(fixture, header['scenario']['id'], int(footer['finalTick']), float(header.get('dt', 0.02)),
                [tick_of(l) for l in lines if l.get('kind') == 'state'], [tick_of(l) for l in lines if l.get('kind') == 'snapshot'])


def host_command() -> list[str]:
    if HOST_DLL.exists():
        return ['dotnet', str(HOST_DLL)]
    return ['dotnet', 'run', '--project', str(ROOT / 'src' / 'SimCore.Host'), '-c', 'Release', '--no-build', '--']


def run_host(args: list[str], what: str) -> str:
    env = dict(os.environ)
    # 本機資料守則:.NET / NuGet 的快取留在專案內(未設定時才補)
    env.setdefault('DOTNET_CLI_HOME', str(ROOT / '.cache' / 'dotnet'))
    env.setdefault('NUGET_PACKAGES', str(ROOT / '.cache' / 'nuget' / 'packages'))
    p = subprocess.run(host_command() + args, cwd=ROOT, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    out = (p.stdout or '') + (p.stderr or '')
    if p.returncode != 0:
        tail = '\n'.join(out.strip().splitlines()[-15:])
        raise RuntimeError(f'{what} 失敗(結束碼 {p.returncode}):\n{tail}')
    return out


def strip_volatile(line: dict) -> dict:
    skip = VOLATILE.get(str(line.get('kind')), set())
    return {k: v for k, v in line.items() if k not in skip}


def diff_kinds(old: list[dict], new: list[dict]) -> dict[str, int]:
    """新舊夾具逐行比較(忽略建立/關閉時刻與檔案雜湊),回傳各種類不同的行數;行數不同時以 'lines' 記總數差;
    footer 的狀態雜湊不同另記 'stateHash'(= 模擬結果變了,不只是標頭)。"""
    changed: collections.Counter[str] = collections.Counter()
    if len(old) != len(new):
        changed['lines'] = abs(len(old) - len(new))
    for a, b in zip(old, new):
        if strip_volatile(a) != strip_volatile(b):
            changed[str(b.get('kind'))] += 1
    old_footer = next((l for l in old if l.get('kind') == 'footer'), {})
    new_footer = next((l for l in new if l.get('kind') == 'footer'), {})
    if old_footer.get('stateHash') != new_footer.get('stateHash'):
        changed['stateHash'] = 1
    return dict(changed)


def rerecord(plan: Plan, scenario_dir: pathlib.Path, timescale: int, check: bool, keep: bool) -> dict:
    scenario = scenario_dir / f'{plan.scenario_id}.yaml'
    if not scenario.exists():
        raise RuntimeError(f'找不到情境檔 {scenario}')
    TMP.mkdir(parents=True, exist_ok=True)
    full = TMP / f'{plan.scenario_id}.jsonl'
    if full.exists():
        full.unlink()
    rel = os.path.relpath(scenario, ROOT)
    run_host(['--scenario', rel, '--validate'], f'{plan.scenario_id} --validate')
    run_host(['--scenario', rel, '--duration', str(plan.duration_s), '--timescale', str(timescale), '--record', str(full),
              '--no-ws', '--no-udp', '--no-nmea', '--no-vhf', '--no-media', '--no-stop-file'], f'{plan.scenario_id} 錄製')
    replay = run_host(['--replay', str(full)], f'{plan.scenario_id} 重播')
    if '不一致' in replay:
        raise RuntimeError(f'{plan.scenario_id} 重播不一致:\n' + '\n'.join(replay.strip().splitlines()[-8:]))
    kept: list[str] = []
    counts: collections.Counter[str] = collections.Counter()
    seen_state: set[int] = set()
    seen_snap: set[int] = set()
    final_tick = None
    with open(full, encoding='utf-8') as f:
        for line in f:
            if not line.strip():
                continue
            o = json.loads(line)
            kind = o.get('kind')
            if kind == 'state':
                if tick_of(o) not in plan.state_ticks:
                    continue
                seen_state.add(tick_of(o))
            elif kind == 'snapshot':
                if tick_of(o) not in plan.snapshot_ticks:
                    continue
                seen_snap.add(tick_of(o))
            elif kind == 'footer':
                final_tick = o.get('finalTick')
            counts[str(kind)] += 1
            kept.append(line.rstrip('\r\n'))
    problems = []
    if final_tick != plan.final_tick:
        problems.append(f'finalTick {final_tick}(預期 {plan.final_tick})')
    if seen_state != plan.state_ticks:
        problems.append(f'state tick 缺 {sorted(plan.state_ticks - seen_state)}')
    if seen_snap != plan.snapshot_ticks:
        problems.append(f'snapshot tick 缺 {sorted(plan.snapshot_ticks - seen_snap)}')
    if problems:
        raise RuntimeError(f'{plan.scenario_id}:' + ';'.join(problems))
    new_lines = [json.loads(l) for l in kept]
    changed = diff_kinds(read_lines(plan.fixture), new_lines) if plan.fixture.exists() else {'new': len(new_lines)}
    if not check:
        with open(plan.fixture, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(kept) + '\n')
    if not keep:
        full.unlink()
    return {'id': plan.scenario_id, 'fixture': plan.fixture.name, 'counts': dict(counts), 'changed': changed}


def main() -> int:
    ap = argparse.ArgumentParser(description='重錄核心紀錄夾具(見檔頭說明)')
    ap.add_argument('ids', nargs='*', help='情境 id(header.scenario.id)或夾具檔名')
    ap.add_argument('--all', action='store_true', help='全部既有夾具')
    ap.add_argument('--changed', action='store_true', help='只重錄情境檔在 git 中有未提交變更的夾具')
    ap.add_argument('--new', action='append', default=[], metavar='YAML', help='為新情境建立夾具(可重複)')
    ap.add_argument('--check', action='store_true', help='不寫檔;header / footer 以外有差異時結束碼 1')
    ap.add_argument('--jobs', type=int, default=2)
    ap.add_argument('--timescale', type=int, default=10)
    ap.add_argument('--scenario-dir', type=pathlib.Path, default=SCENARIOS)
    ap.add_argument('--keep', action='store_true', help='保留 build/tmp/fixtures/ 下的完整紀錄')
    a = ap.parse_args()

    plans = {p.scenario_id: p for p in (plan_from_fixture(f) for f in sorted(FIXTURES.glob('record-*-core.jsonl')))}
    by_fixture = {p.fixture.name: p for p in plans.values()}
    selected: list[Plan] = []
    if a.all:
        selected = list(plans.values())
    if a.changed:
        out = subprocess.run(['git', 'status', '--porcelain', '--', 'data/scenarios'], cwd=ROOT, capture_output=True, text=True, check=True).stdout
        for line in out.splitlines():
            sid = pathlib.Path(line[3:].strip().strip('"')).stem
            if sid in plans and plans[sid] not in selected:
                selected.append(plans[sid])
    for x in a.ids:
        p = plans.get(x) or by_fixture.get(x) or by_fixture.get(f'record-{x}-core.jsonl')
        if p is None:
            raise SystemExit(f'找不到夾具:{x}(新情境請用 --new <YAML>)')
        if p not in selected:
            selected.append(p)
    for y in a.new:
        sid = pathlib.Path(y).stem
        if sid in plans:
            raise SystemExit(f'{sid} 已有夾具 {plans[sid].fixture.name};直接指定 id 重錄')
        selected.append(Plan(FIXTURES / f'record-{sid}-core.jsonl', sid, DEFAULT_FINAL_TICK, 0.02, DEFAULT_STATE_TICKS, DEFAULT_SNAPSHOT_TICKS))
    if not selected:
        ap.print_usage()
        return 2

    print(f'重錄 {len(selected)} 個夾具(同時 {a.jobs} 個;timescale {a.timescale}){";只檢查" if a.check else ""}')
    failed = 0
    unexpected = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, a.jobs)) as ex:
        futs = {ex.submit(rerecord, p, a.scenario_dir, a.timescale, a.check, a.keep): p for p in selected}
        for fut in concurrent.futures.as_completed(futs):
            p = futs[fut]
            try:
                r = fut.result()
            except Exception as e:  # noqa: BLE001 — 個別情境失敗不影響其他
                failed += 1
                print(f'  FAIL {p.scenario_id}:{e}')
                continue
            other = {k: v for k, v in r['changed'].items() if k not in ('header', 'footer')}
            if other:
                unexpected += 1
            note = '與舊夾具相同' if not r['changed'] else '變更 ' + '、'.join(f'{k}×{v}' for k, v in sorted(r['changed'].items()))
            print(f'  ok   {r["fixture"]}:' + ' '.join(f'{k} {v}' for k, v in sorted(r['counts'].items())) + f';{note}')
    if failed:
        print(f'{failed} 個失敗')
        return 1
    if unexpected:
        print(f'{unexpected} 個夾具有 header / footer 以外的差異(狀態、事件或摘要變了:物理、時間表或評估有變動時屬正常,只改簡報時不應出現)')
        return 1 if a.check else 0
    return 0


if __name__ == '__main__':
    sys.exit(main())
