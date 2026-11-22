#!/usr/bin/env node
// 從一次 soak 執行目錄(build/soak/<run>/)擷取 RSS 準則需要的欄位,寫成 tests/soak/report.test.mjs 用的小型夾具(docs/design/testing.md 第 6.6 節)。
// 只留:取樣的 elapsed_s / 核心 RSS / 客戶端是否存活,counters.csv 每次刷新的 GC 次數、GC 已提交量、gen0 預算、gen1 / gen2 / LOH / POH 大小;
// 單位維持 dotnet-counters 的原樣(次數、10^6 B 的 MB),大小以 KiB 整數存(夾具數 KB 以內)。--until-s 截到前 T 秒(同懷疑者的 trunc.mjs:
// 取樣 ≤ T,counters 時間戳 ≤ 最後一筆取樣的牆鐘;時間戳格式為容器的 en-US「M/D/YYYY H:M:S」UTC)。
// 用法:node tests/soak/fixtures/extract.mjs <執行目錄> <輸出.json> [--until-s T] [--note 說明] [--expect pass|fail|undetermined]
import fs from 'node:fs';
import path from 'node:path';
import { groupCounterRows } from '../report.mjs';

const [runDir, outFile, ...rest] = process.argv.slice(2);
if (!runDir || !outFile) { console.error('用法:extract.mjs <執行目錄> <輸出.json> [--until-s T] [--note 說明] [--expect …]'); process.exit(2); }
const opt = {};
for (let i = 0; i < rest.length; i += 2) opt[rest[i].replace(/^--/, '')] = rest[i + 1];
const meta = JSON.parse(fs.readFileSync(path.join(runDir, 'meta.json'), 'utf8'));
const T = opt['until-s'] ? Number(opt['until-s']) : Infinity;
const samples = fs.readFileSync(path.join(runDir, 'stats/host.jsonl'), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l))
  .filter((s) => s.kind === 'sample' && s.host && s.elapsed_s <= T);
const lastWall = Date.parse(samples[samples.length - 1].wall);
const tsMs = (s) => { const m = s.match(/^(\d+)\/(\d+)\/(\d+) (\d+):(\d+):(\d+)/); return m ? Date.UTC(+m[3], +m[1] - 1, +m[2], +m[4], +m[5], +m[6]) : NaN; };
const want = { 'Gen 0 GC Count': 'gc', 'GC Committed Bytes': 'committedMB', 'Gen 0 GC Budget': 'budgetMB', 'Gen 1 Size': 'gen1KiB', 'Gen 2 Size': 'gen2KiB', 'LOH Size': 'lohKiB', 'POH (Pinned Object Heap) Size': 'pohKiB' };
const fields = Object.values(want);
// 刷新分組與 report.mjs 相同(時間戳排序;dotnet-counters 偶爾把第一次刷新拆成兩段寫,見 groupCounterRows)
const csvRows = fs.readFileSync(path.join(runDir, 'stats/counters.csv'), 'utf8').split('\n').slice(1).map((l) => l.split(','))
  .filter((r) => r.length >= 5 && tsMs(r[0]) <= lastWall);
const rows = groupCounterRows(csvRows, meta.sample_s ?? 10).map((g) => {
  const cur = Object.fromEntries(fields.map((f) => [f, null]));
  for (const r of g.rows) {
    const name = r[2].trim();
    const base = Object.keys(want).find((b) => name === b || name.startsWith(`${b} (`));
    if (!base) continue;
    const v = Number(r[4]);
    cur[want[base]] = want[base].endsWith('KiB') ? Math.round(v / 1024) : Math.round(v * 1000) / 1000;
  }
  return cur;
});
const out = {
  source: `${path.relative(process.cwd(), runDir)}${Number.isFinite(T) ? `(截到前 ${T} s)` : ''}`,
  note: opt.note ?? '',
  expect: opt.expect ?? null,
  meta: { run: meta.run, minutes: Number.isFinite(T) ? T / 60 : meta.minutes, sample_s: meta.sample_s, clients: meta.clients, dotnet: meta.dotnet, gc: meta.gc ?? null },
  host: { fields: ['elapsed_s', 'rss_mb', 'clientsAlive'], rows: samples.map((s) => [s.elapsed_s, s.host.rss_mb, s.clients?.alive === false ? 0 : 1]) },
  counters: { intervalS: meta.sample_s ?? 10, fields, rows: rows.map((x) => fields.map((f) => x[f])) },
};
fs.writeFileSync(outFile, JSON.stringify(out).replace(/\],\[/g, '],\n[') + '\n');
console.log(`${outFile}:取樣 ${out.host.rows.length} 筆、counters ${out.counters.rows.length} 次刷新、${fs.statSync(outFile).size} B`);
