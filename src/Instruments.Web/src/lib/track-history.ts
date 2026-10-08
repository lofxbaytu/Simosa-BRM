// 航跡紀錄:每隔固定秒數存一點,保留最近 N 點(鳥瞰圖畫航跡線用)。

export interface TrackPoint {
  x: number;
  y: number;
  t: number;
}

export class TrackHistory {
  private readonly points: TrackPoint[] = [];
  private lastT = Number.NEGATIVE_INFINITY;

  constructor(
    private readonly intervalS = 1,
    private readonly maxPoints = 3600,
  ) {}

  /** 加入一點;模擬時間倒退(重設)時清空。 */
  push(x: number, y: number, t: number): void {
    if (t < this.lastT) this.clear();
    if (t - this.lastT < this.intervalS && this.points.length > 0) return;
    this.points.push({ x, y, t });
    this.lastT = t;
    if (this.points.length > this.maxPoints) this.points.splice(0, this.points.length - this.maxPoints);
  }

  clear(): void {
    this.points.length = 0;
    this.lastT = Number.NEGATIVE_INFINITY;
  }

  get length(): number {
    return this.points.length;
  }

  all(): readonly TrackPoint[] {
    return this.points;
  }
}
