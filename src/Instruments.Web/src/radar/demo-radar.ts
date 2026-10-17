// 雷達示範模式(?demo=1):不需雷達服務,本地合成影像幀與 ARPA 追蹤,供離線測試與教學展示。
// 影像:雜訊、海雜波(R⁻³、STC)、雨區(FTC)、陸地(西北側示意海岸)、3 艘船(T1 右舷橫越危險、T2 被追越、T3 漁船未追蹤)、艉向陰影扇區;
// 強度換算與 Sensors.Radar 相同的概念(增益門檻 40 − 0.43 × gain dB、門檻以上 0–30 dB → 0–255)。
// 亂數以固定種子雜湊(方位線全域序號, 距離格)產生:相同時間序列 → 相同影像(確定性)。
// 示範情境每 15 分鐘(模擬時間)重新開始。不可用於訓練或驗證。

import { encodeSpokeFrame, parseSpokeFrame, FLAG_HEADING_VALID, FLAG_TRANSMITTING } from './frame.js';
import {
  nearestRange,
  STANDARD_RANGES_NM,
  type GuardZone,
  type RadarAck,
  type RadarCommand,
  type RadarId,
  type RadarMessage,
  type RadarStatus,
  type RadarTrack,
  type RadarTracks,
  type TrialSetting,
} from './protocol.js';
import { computeCpa, isDangerous, trialCpa, velocityEnu } from './arpa.js';
import type { ConnListener, FrameListener, MessageListener, RadarConnStatus, RadarSource } from './radar-client.js';
import { bearingInSector, guardZoneTrueSector } from './geometry.js';

const NM = 1852;
const KN = 1852 / 3600;
const RAD = Math.PI / 180;
const SPOKES = 2048;
const CELLS = 512;
const RPM = 24;
const LOOP_S = 900;
const ACQUIRE_TO_TRACK_S = 30;

interface DemoShip {
  name: string;
  x0: number;
  y0: number;
  course: number;
  speed: number;
  length: number;
  /** 1 nm 處回波強度(dB,雜訊以上) */
  db1nm: number;
}

/** 示範船(t = 0 時相對自船的真方位/距離 → ENU) */
function ship(name: string, brg: number, rngNm: number, course: number, speed: number, length: number, db1nm: number): DemoShip {
  return { name, x0: rngNm * NM * Math.sin(brg * RAD), y0: rngNm * NM * Math.cos(brg * RAD), course, speed, length, db1nm };
}

export const DEMO_SHIPS: readonly DemoShip[] = [
  ship('T1', 75, 3.6, 290, 13, 180, 60),
  ship('T2', 20, 2.4, 44, 5, 120, 55),
  ship('T3', 320, 2.0, 100, 3, 25, 44),
];

export const DEMO_OWN = { course: 44, speed: 10 };

interface DemoTrack {
  id: number;
  ship: number;
  acquisition: 'manual' | 'auto';
  startT: number;
  ageOffset: number;
  trackingNotified: boolean;
}

interface PendingAcquire {
  id: number;
  x: number;
  y: number;
  t: number;
}

function hash01(seed: number, a: number, b: number): number {
  let h = Math.imul(a, 0x27d4eb2d) ^ Math.imul(b + 0x9e3779b9, 0x165667b1) ^ seed;
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b);
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35);
  h ^= h >>> 16;
  return ((h >>> 0) + 0.5) / 4294967296;
}

export interface DemoOptions {
  radar?: RadarId;
  seed?: number;
  /** 自動計時(setInterval);測試時設 false 並手動 tick() */
  autoTick?: boolean;
  tickMs?: number;
}

export class DemoRadarSource implements RadarSource {
  readonly url = 'demo://radar';
  readonly radar: RadarId;
  private readonly seed: number;
  private frameL = new Set<FrameListener>();
  private msgL = new Set<MessageListener>();
  private connL = new Set<ConnListener>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private readonly autoTick: boolean;
  private readonly tickMs: number;
  private lastWall = 0;
  private _status: RadarConnStatus = 'disconnected';

  /** 模擬時間(s) */
  t = 0;
  private spokeCursor = 0; // 下一條要產生的方位線全域序號
  private scan = 0;
  private nextTracksT = 0;
  private seq = 0;

  // 操作員設定
  rangeNm = 6;
  gain = 70;
  sea = 50;
  rain = 30;
  tune = 50;
  autoTune = true;
  standby = false;
  guard: GuardZone = { enabled: false, inner_nm: 0.5, outer_nm: 3, from: 0, to: 360, relative: true };
  trial: TrialSetting | undefined;
  cpaLimit = 1;
  tcpaLimit = 12;
  seaState = 3;

  private tracks: DemoTrack[] = [];
  private pendingAcq: PendingAcquire[] = [];
  private dangerNotified = new Set<number>();

  constructor(o: DemoOptions = {}) {
    this.radar = o.radar ?? 'x';
    this.seed = o.seed ?? 20260505;
    this.autoTick = o.autoTick ?? true;
    this.tickMs = o.tickMs ?? 40;
    this.resetScenario();
  }

  get status(): RadarConnStatus {
    return this._status;
  }

  get band(): 'X' | 'S' {
    return this.radar === 's' ? 'S' : 'X';
  }

  private get beamwidth(): number {
    return this.radar === 's' ? 2 : 1;
  }

  private resetScenario(): void {
    this.t = 0;
    this.spokeCursor = 0;
    this.scan = 0;
    this.nextTracksT = 0;
    this.tracks = [
      { id: 1, ship: 0, acquisition: 'auto', startT: 0, ageOffset: 150, trackingNotified: true },
      { id: 2, ship: 1, acquisition: 'auto', startT: 0, ageOffset: 200, trackingNotified: true },
    ];
    this.pendingAcq = [];
    this.dangerNotified.clear();
  }

  start(): void {
    if (this._status === 'demo') return;
    this.setConn('demo', '示範模式(本地合成影像)');
    this.emit(this.statusMessage());
    this.emitTracks();
    if (this.autoTick && typeof setInterval !== 'undefined') {
      this.lastWall = typeof performance !== 'undefined' ? performance.now() : Date.now();
      this.timer = setInterval(() => {
        const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
        const dt = Math.min(0.25, Math.max(0, (now - this.lastWall) / 1000));
        this.lastWall = now;
        this.tick(dt);
      }, this.tickMs);
    }
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.setConn('disconnected', '已停止');
  }

  onFrame(l: FrameListener): () => void {
    this.frameL.add(l);
    return () => this.frameL.delete(l);
  }
  onMessage(l: MessageListener): () => void {
    this.msgL.add(l);
    return () => this.msgL.delete(l);
  }
  onConn(l: ConnListener): () => void {
    this.connL.add(l);
    l(this._status);
    return () => this.connL.delete(l);
  }

  // -------------------------------------------------------------------------------------------
  // 運動

  /** 自船 ENU 位置(m)、航向、COG、SOG */
  ownAt(t: number): { x: number; y: number; heading: number; cog: number; sog: number } {
    const v = velocityEnu(DEMO_OWN.course, DEMO_OWN.speed);
    return {
      x: v.vE * t,
      y: v.vN * t,
      heading: (DEMO_OWN.course + 1 + 1.5 * Math.sin((2 * Math.PI * t) / 40) + 360) % 360,
      cog: DEMO_OWN.course,
      sog: DEMO_OWN.speed,
    };
  }

  shipAt(i: number, t: number): { x: number; y: number } {
    const s = DEMO_SHIPS[i]!;
    const v = velocityEnu(s.course, s.speed);
    const own0 = this.ownAt(0);
    return { x: own0.x + s.x0 + v.vE * t, y: own0.y + s.y0 + v.vN * t };
  }

  // -------------------------------------------------------------------------------------------
  // 時間推進

  tick(dt: number): void {
    if (dt <= 0) return;
    this.t += dt;
    if (this.t >= LOOP_S) {
      this.resetScenario();
      this.emit(this.statusMessage());
    }
    // 方位線:全域序號 = ⌊t × rpm/60 × spokes⌋(與服務相同)
    const target = Math.floor((this.t * RPM * SPOKES) / 60);
    if (!this.standby) {
      // 一次最多補 1 圈(背景分頁節流後不補整段)
      if (target - this.spokeCursor > SPOKES) this.spokeCursor = target - SPOKES;
      while (this.spokeCursor < target) {
        const idx = this.spokeCursor % SPOKES;
        const count = Math.min(32, target - this.spokeCursor, SPOKES - idx);
        this.emitFrame(this.spokeCursor, count);
        this.spokeCursor += count;
      }
    } else {
      this.spokeCursor = target;
    }
    this.scan = Math.floor(this.spokeCursor / SPOKES);
    this.updateAcquisition();
    if (this.t >= this.nextTracksT) {
      this.nextTracksT = Math.floor(this.t) + 1;
      this.emitTracks();
    }
  }

  private emitFrame(globalFirst: number, count: number): void {
    const t = (globalFirst * 60) / (RPM * SPOKES);
    const own = this.ownAt(t);
    const rangeM = this.rangeNm * NM;
    const data = new Uint8Array(count * CELLS);
    // 天線位置:CCRP 後 30 m
    const ax = own.x - 30 * Math.sin(own.heading * RAD);
    const ay = own.y - 30 * Math.cos(own.heading * RAD);
    const ships = DEMO_SHIPS.map((_s, i) => {
      const p = this.shipAt(i, t);
      const dx = p.x - ax;
      const dy = p.y - ay;
      const r = Math.hypot(dx, dy);
      return { r, brg: (Math.atan2(dx, dy) / RAD + 360) % 360, len: DEMO_SHIPS[i]!.length, db: DEMO_SHIPS[i]!.db1nm };
    });
    const bw = this.beamwidth;
    const sigmaB = bw / 2.4;
    const thr = 40 - 0.43 * this.gain + (this.autoTune ? 0 : 20 * ((this.tune - 50) / 50) ** 2);
    const pulseM = this.rangeNm <= 0.75 ? 10.5 : this.rangeNm <= 3 ? 45 : this.rangeNm <= 12 ? 75 : 180;
    const seaDb1 = 5 + 4 * this.seaState - (this.radar === 's' ? 10 : 0);
    const ftc = 1 - (this.rain / 100) * 0.95;
    // 只與距離格有關的量(每幀一次):海雜波平均(線性)、STC(dB)
    const seaLin = new Float64Array(CELLS);
    const stcDb = new Float64Array(CELLS);
    for (let i = 0; i < CELLS; i++) {
      const rNm = ((i + 0.5) * rangeM) / CELLS / NM;
      const cdb = seaDb1 - 30 * Math.log10(Math.max(0.03, rNm)) - (rNm > 1.2 ? 40 * (rNm - 1.2) : 0);
      seaLin[i] = rNm < 2.5 ? Math.pow(10, cdb / 10) : 0;
      stcDb[i] = (this.sea / 100) * Math.max(0, Math.min(60, 40 * Math.log10(3 / Math.max(0.01, rNm))));
    }
    for (let j = 0; j < count; j++) {
      const g = globalFirst + j;
      const rel = ((g % SPOKES) * 360) / SPOKES;
      const trueBrg = (rel + own.heading) % 360;
      const shadow = rel >= 174 && rel <= 186 ? 1e-6 : 1;
      // 本線的目標角度因子
      const hits: { r0: number; r1: number; p1: number }[] = [];
      for (const s of ships) {
        let d = Math.abs(trueBrg - s.brg);
        if (d > 180) d = 360 - d;
        const half = ((s.len / Math.max(50, s.r)) / RAD) * 0.5 * 0.6;
        const off = Math.max(0, d - half);
        if (off > 4 * sigmaB) continue;
        const beam = Math.exp(-0.5 * (off / sigmaB) ** 2);
        const rnm = Math.max(0.05, s.r / NM);
        const p1 = Math.pow(10, (s.db - 40 * Math.log10(rnm)) / 10) * beam;
        hits.push({ r0: s.r - s.len * 0.3, r1: s.r + s.len * 0.3 + pulseM, p1 });
      }
      // 陸地(真方位 250–340°,海岸 4.2–5.4 nm)
      let landR = Infinity;
      if (trueBrg >= 250 && trueBrg <= 340) {
        landR = (4.6 + 0.8 * Math.sin(3 * trueBrg * RAD) + 0.3 * Math.sin(11 * trueBrg * RAD)) * NM;
      }
      // 雨區(真方位 120°、4 nm,半徑 0.8 nm),與自船位置無關的固定 ENU 中心
      const rainCx = 4 * NM * Math.sin(120 * RAD);
      const rainCy = 4 * NM * Math.cos(120 * RAD);
      const windLin = Math.pow(10, (3 * Math.cos((trueBrg - 30) * RAD)) / 10);
      const sinB = Math.sin(trueBrg * RAD);
      const cosB = Math.cos(trueBrg * RAD);
      const base = j * CELLS;
      for (let i = 0; i < CELLS; i++) {
        const rM = ((i + 0.5) * rangeM) / CELLS;
        const rNm = rM / NM;
        const u = hash01(this.seed, g, i);
        let p = -Math.log(u); // 雜訊 Exp(1)
        // 海雜波:R⁻³(迎風北東 +3 dB)
        const sl = seaLin[i]!;
        if (sl > 0) p += sl * windLin * -Math.log(hash01(this.seed ^ 0x5bd1e995, g, i));
        for (const h of hits) if (rM >= h.r0 && rM <= h.r1) p += h.p1 * (0.7 + 0.6 * hash01(this.seed ^ 0x1234567, g >> 2, i));
        if (rM >= landR && rM < landR + 1.6 * NM) {
          const ldb = 52 - 40 * Math.log10(rNm) - ((rM - landR) / NM) * 12;
          p += Math.pow(10, ldb / 10) * (0.3 + hash01(this.seed ^ 0x77, g >> 1, i >> 1));
        }
        const ex = ax + rM * sinB - rainCx;
        const ey = ay + rM * cosB - rainCy;
        if (ex * ex + ey * ey < (0.8 * NM) ** 2) {
          const rdb = (this.radar === 's' ? 2 : 22) - 20 * Math.log10(Math.max(1, rNm / 2));
          p += Math.pow(10, rdb / 10) * ftc * -Math.log(hash01(this.seed ^ 0x3c6ef372, g, i));
        }
        p *= shadow;
        const db = 10 * Math.log10(p) - stcDb[i]!;
        const v = ((db - thr) * 255) / 30;
        data[base + i] = v <= 0 ? 0 : v >= 255 ? 255 : v;
      }
    }
    const buf = encodeSpokeFrame({
      radarId: this.band,
      flags: FLAG_TRANSMITTING | FLAG_HEADING_VALID,
      scan: Math.floor(globalFirst / SPOKES),
      firstSpoke: globalFirst % SPOKES,
      spokeCount: count,
      spokesPerRev: SPOKES,
      cells: CELLS,
      rangeM,
      headingDeg: own.heading,
      cogDeg: own.cog,
      t,
    });
    new Uint8Array(buf, 40).set(data);
    const f = parseSpokeFrame(buf);
    for (const l of this.frameL) l(f);
  }

  // -------------------------------------------------------------------------------------------
  // ARPA(示範:真值 = 追蹤值)

  private trackReport(tr: DemoTrack): RadarTrack {
    const own = this.ownAt(this.t);
    const p = this.shipAt(tr.ship, this.t);
    const s = DEMO_SHIPS[tr.ship]!;
    const dE = p.x - own.x;
    const dN = p.y - own.y;
    const range = Math.hypot(dE, dN) / NM;
    const brg = (Math.atan2(dE, dN) / RAD + 360) % 360;
    const age = this.t - tr.startT + tr.ageOffset;
    const tracking = this.t - tr.startT >= (tr.ageOffset > 0 ? 0 : ACQUIRE_TO_TRACK_S);
    const out: RadarTrack = {
      id: tr.id,
      label: String(tr.id).padStart(2, '0'),
      state: tracking ? 'tracking' : 'acquiring',
      acquisition: tr.acquisition,
      age_s: Math.round(age * 10) / 10,
      stable: tracking && age >= 180,
      manoeuvring: false,
      dangerous: false,
      swapRisk: false,
      misses: 0,
      x: p.x,
      y: p.y,
      lat: 22.5 + p.y / 111120,
      lon: 120.2 + p.x / (111120 * Math.cos(22.5 * RAD)),
      range_nm: range,
      bearing_deg: brg,
      relBearing_deg: (brg - own.heading + 360) % 360,
      past: [],
    };
    for (let k = 1; k <= 10; k++) {
      const pt = this.t - k * 30;
      if (pt < tr.startT - tr.ageOffset) break;
      const pp = this.shipAt(tr.ship, pt);
      out.past.push({ t: pt, x: pp.x, y: pp.y, lat: 0, lon: 0 });
    }
    if (!tracking) return out;
    const vt = velocityEnu(s.course, s.speed);
    const vo = velocityEnu(own.cog, own.sog);
    const rvE = vt.vE - vo.vE;
    const rvN = vt.vN - vo.vN;
    const cpa = computeCpa(dE, dN, rvE, rvN);
    out.course_deg = s.course;
    out.speed_kn = s.speed;
    out.relCourse_deg = (Math.atan2(rvE, rvN) / RAD + 360) % 360;
    out.relSpeed_kn = Math.hypot(rvE, rvN) / KN;
    out.cpa_nm = cpa.cpaNm;
    out.tcpa_min = cpa.tcpaMin;
    out.dangerous = isDangerous(cpa.cpaNm, cpa.tcpaMin, this.cpaLimit, this.tcpaLimit);
    // BCR:目標相對軌跡與自船艏向線的交點(正 = 艏前)
    const hE = Math.sin(own.heading * RAD);
    const hN = Math.cos(own.heading * RAD);
    const den = hE * rvN - hN * rvE;
    if (Math.abs(den) > 1e-6) {
      const tb = -(hE * dN - hN * dE) / den;
      if (tb >= 0) {
        out.bcr_nm = ((dE + rvE * tb) * hE + (dN + rvN * tb) * hN) / NM;
        out.bct_min = tb / 60;
      }
    }
    if (this.trial) {
      const tv = velocityEnu(this.trial.course, this.trial.speed);
      const tc = trialCpa(dE, dN, vt, vo, tv, this.trial.delay_min);
      out.trialCpa_nm = tc.cpaNm;
      out.trialTcpa_min = tc.tcpaMin;
    }
    return out;
  }

  private updateAcquisition(): void {
    // 擷取中 → 追蹤中
    for (const tr of this.tracks) {
      if (!tr.trackingNotified && this.t - tr.startT >= ACQUIRE_TO_TRACK_S) {
        tr.trackingNotified = true;
        this.event('tracking', `目標 ${String(tr.id).padStart(2, '0')} 追蹤中(向量有效)`, tr.id);
      }
    }
    // 擷取失敗(3 圈無回波)
    for (const pa of [...this.pendingAcq]) {
      if (this.t - pa.t >= 7.5) {
        this.pendingAcq = this.pendingAcq.filter((x) => x !== pa);
        this.event('acquireFailed', `目標 ${String(pa.id).padStart(2, '0')} 擷取失敗(無回波)`, pa.id);
      }
    }
    // 保護區自動擷取(只對未追蹤的船)
    if (this.guard.enabled && !this.standby) {
      const own = this.ownAt(this.t);
      const sec = guardZoneTrueSector(this.guard, own.heading);
      DEMO_SHIPS.forEach((_s, i) => {
        if (this.tracks.some((t) => t.ship === i)) return;
        const p = this.shipAt(i, this.t);
        const r = Math.hypot(p.x - own.x, p.y - own.y) / NM;
        const b = (Math.atan2(p.x - own.x, p.y - own.y) / RAD + 360) % 360;
        if (r < this.guard.inner_nm || r > this.guard.outer_nm) return;
        if (!sec.full && !bearingInSector(b, sec.from, sec.to)) return;
        const id = this.nextId();
        if (id === null) return;
        this.tracks.push({ id, ship: i, acquisition: 'auto', startT: this.t, ageOffset: 0, trackingNotified: false });
        this.event('newTarget', `保護區新目標 ${String(id).padStart(2, '0')}:${b.toFixed(1)}° ${r.toFixed(2)} nm(自動擷取)`, id);
      });
    }
  }

  private nextId(): number | null {
    const used = new Set([...this.tracks.map((t) => t.id), ...this.pendingAcq.map((p) => p.id)]);
    for (let i = 1; i <= 99; i++) if (!used.has(i)) return i;
    return null;
  }

  private emitTracks(): void {
    const own = this.ownAt(this.t);
    const targets = this.standby ? [] : this.tracks.map((t) => this.trackReport(t));
    for (const tr of targets) {
      if (tr.dangerous && !this.dangerNotified.has(tr.id)) {
        this.dangerNotified.add(tr.id);
        this.event('dangerousTarget', `危險目標 ${tr.label}:CPA ${tr.cpa_nm?.toFixed(2)} nm、TCPA ${tr.tcpa_min?.toFixed(1)} 分`, tr.id);
      }
      if (!tr.dangerous) this.dangerNotified.delete(tr.id);
    }
    const msg: RadarTracks = {
      type: 'tracks',
      radar: this.radar,
      t: Math.round(this.t * 1000) / 1000,
      scan: this.scan,
      transmitting: !this.standby,
      ownShip: { x: own.x, y: own.y, lat: 22.5, lon: 120.2, heading: own.heading, cog: own.cog, sog: own.sog },
      cpaLimit_nm: this.cpaLimit,
      tcpaLimit_min: this.tcpaLimit,
      targets,
    };
    if (this.trial) msg.trial = { ...this.trial };
    this.emit(msg);
  }

  statusMessage(): RadarStatus {
    const st: RadarStatus = {
      type: 'status',
      radar: this.radar,
      band: this.band,
      frequency_GHz: this.radar === 's' ? 3.05 : 9.41,
      transmitting: !this.standby,
      standby: this.standby,
      failed: false,
      degraded: false,
      headingValid: true,
      range_nm: this.rangeNm,
      rangeScales: [...STANDARD_RANGES_NM],
      pulse: this.rangeNm <= 0.75 ? 'short' : this.rangeNm <= 12 ? 'medium' : 'long',
      pulseLength_m: this.rangeNm <= 0.75 ? 10.5 : this.rangeNm <= 3 ? 45 : this.rangeNm <= 12 ? 75 : 180,
      gain: this.gain,
      sea: this.sea,
      rain: this.rain,
      tune: this.tune,
      autoTune: this.autoTune,
      rpm: RPM,
      spokes: SPOKES,
      cells: CELLS,
      beamwidth_deg: this.beamwidth,
      antennaHeight_m: 25,
      antennaFwd_m: -30,
      antennaStbd_m: 0,
      shadowSectors: [{ from: 174, to: 186, attenuation_dB: 60 }],
      guardZone: { ...this.guard },
      cpaLimit_nm: this.cpaLimit,
      tcpaLimit_min: this.tcpaLimit,
      arpaRange_nm: 16,
      maxTracks: 40,
      seaState: this.seaState,
      rainCells: 1,
      landPolygons: 1,
    };
    if (this.trial) st.trial = { ...this.trial };
    return st;
  }

  // -------------------------------------------------------------------------------------------
  // 指令

  send(cmd: RadarCommand): string | null {
    const reqId = `d${++this.seq}`;
    const ack = this.apply(cmd);
    ack.reqId = reqId;
    const after: RadarMessage[] = [ack];
    if (cmd.cmd === 'getStatus') after.length = 0; // getStatus 只回 status(與服務相同)
    if (ack.ok && cmd.cmd !== 'acquire' && cmd.cmd !== 'cancel' && cmd.cmd !== 'getTracks') after.push(this.statusMessage());
    queueMicrotask(() => {
      for (const m of after) this.emit(m);
      if (cmd.cmd === 'getTracks' || (ack.ok && (cmd.cmd === 'cancel' || cmd.cmd === 'trialManoeuvre' || cmd.cmd === 'cpaLimits'))) this.emitTracks();
    });
    return reqId;
  }

  private apply(cmd: RadarCommand): RadarAck {
    const ack: RadarAck = { type: 'ack', radar: this.radar, cmd: cmd.cmd, ok: true };
    const pct = (v: number) => Math.max(0, Math.min(100, v));
    switch (cmd.cmd) {
      case 'range':
        this.rangeNm = nearestRange(cmd.value);
        ack.value = this.rangeNm;
        break;
      case 'gain':
        this.gain = pct(cmd.value);
        ack.value = this.gain;
        break;
      case 'sea':
        this.sea = pct(cmd.value);
        ack.value = this.sea;
        break;
      case 'rain':
        this.rain = pct(cmd.value);
        ack.value = this.rain;
        break;
      case 'tune':
        if ('auto' in cmd) this.autoTune = true;
        else {
          this.autoTune = false;
          this.tune = pct(cmd.value);
          ack.value = this.tune;
        }
        break;
      case 'standby':
        this.standby = true;
        this.tracks = [];
        this.pendingAcq = [];
        break;
      case 'transmit':
        this.standby = false;
        break;
      case 'acquire': {
        if (this.standby) return { ...ack, ok: false, error: '待機中無法擷取' };
        const own = this.ownAt(this.t);
        const brg = cmd.relative ? cmd.bearing + own.heading : cmd.bearing;
        const x = own.x + cmd.range * NM * Math.sin(brg * RAD);
        const y = own.y + cmd.range * NM * Math.cos(brg * RAD);
        const id = this.nextId();
        if (id === null) return { ...ack, ok: false, error: '追蹤目標已達上限 99' };
        let best = -1;
        let bestD = 300;
        DEMO_SHIPS.forEach((_s, i) => {
          const p = this.shipAt(i, this.t);
          const d = Math.hypot(p.x - x, p.y - y);
          if (d < bestD && !this.tracks.some((t) => t.ship === i)) {
            bestD = d;
            best = i;
          }
        });
        if (best >= 0) this.tracks.push({ id, ship: best, acquisition: 'manual', startT: this.t, ageOffset: 0, trackingNotified: false });
        else this.pendingAcq.push({ id, x, y, t: this.t });
        ack.id = id;
        queueMicrotask(() => this.event('acquireStarted', `目標 ${String(id).padStart(2, '0')} 手動擷取`, id));
        break;
      }
      case 'cancel': {
        if (cmd.id === 'all') {
          this.tracks = [];
          this.pendingAcq = [];
        } else {
          const id = cmd.id;
          const before = this.tracks.length + this.pendingAcq.length;
          this.tracks = this.tracks.filter((t) => t.id !== id);
          this.pendingAcq = this.pendingAcq.filter((t) => t.id !== id);
          if (before === this.tracks.length + this.pendingAcq.length) return { ...ack, ok: false, error: `無目標 ${id}` };
          ack.id = id;
          queueMicrotask(() => this.event('cancelled', `目標 ${String(id).padStart(2, '0')} 已取消`, id));
        }
        break;
      }
      case 'guardZone':
        this.guard = {
          enabled: cmd.enabled,
          inner_nm: cmd.inner_nm ?? this.guard.inner_nm,
          outer_nm: cmd.outer_nm ?? this.guard.outer_nm,
          from: cmd.from ?? this.guard.from,
          to: cmd.to ?? this.guard.to,
          relative: cmd.relative ?? this.guard.relative,
        };
        if (this.guard.outer_nm <= this.guard.inner_nm) return { ...ack, ok: false, error: '保護區外緣須大於內緣' };
        break;
      case 'trialManoeuvre':
        if ('active' in cmd) this.trial = undefined;
        else this.trial = { course: cmd.course, speed: cmd.speed, delay_min: Math.max(0, cmd.delay) };
        break;
      case 'cpaLimits':
        this.cpaLimit = cmd.cpa_nm;
        this.tcpaLimit = cmd.tcpa_min;
        break;
      case 'environment':
        if (typeof cmd.seaState === 'number') this.seaState = Math.max(0, Math.min(6, cmd.seaState));
        break;
      case 'getStatus':
      case 'getTracks':
        break;
    }
    return ack;
  }

  private event(kind: string, message: string, targetId?: number): void {
    const ev: RadarMessage = { type: 'event', radar: this.radar, event: { kind, t: Math.round(this.t * 10) / 10, message } };
    if (targetId !== undefined) ev.event.targetId = targetId;
    this.emit(ev);
  }

  private emit(m: RadarMessage): void {
    for (const l of this.msgL) l(m);
  }

  private setConn(s: RadarConnStatus, detail?: string): void {
    this._status = s;
    for (const l of this.connL) l(s, detail);
  }
}
