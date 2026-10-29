"""區域水深覆寫(情境 ``zones[].waterDepth_m``;docs/design/scenario-engine.md 第 4.3 節;C# ``src/SimCore/Environment/DepthProviders.cs`` ``ZoneDepth``
與 ``src/SimCore/Geo/Polygon2D.cs`` 同演算法)。

有效水深 = 區域基底水深(自船在多邊形內時取區域值,多區重疊取**最淺**,否則情境 ``environment.waterDepth`` 基底)+ 潮高(t)
(``mmg.MMGShip._effective_depth``);``setEnvironment waterDepth`` 仍只改基底。

- 多邊形內外:ray casting(偶奇規則),**邊上視為內**(點到任一邊距離 < 1e-9 m);運算順序與 C# ``Polygon2D.Contains`` 逐項相同
  (先查邊距、再以 ``(yi > py) != (yj > py)`` 與 ``px < xi + (py - yi)(xj - xi)/(yj - yi)`` 翻轉),跨語言夾具 ``Golden/zone_depth.csv`` 比對到 1e-9。
- 區域只有 ``waterDepth_m``(> 0)者參與(任何 ``type``;C# ``ZoneTracker.DepthZones`` 相同);頂點 ``{x, y}`` 或 ``{lat, lon}`` 由
  ``scenario.normalise_depth_zones`` 以情境 origin 的 ``LocalTangentPlane`` 解析為 ENU(C# ``Scenario.Resolve``)。
- 區域由情境決定、練習中不變,不進快照(C# 同);無 ``waterDepth_m`` 區域的情境數值與舊版逐位元相同(不經此模組)。
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Sequence

Point = tuple[float, float]

ON_EDGE_M = 1e-9  # 邊上視為內的距離門檻(C# Polygon2D.Contains)


def distance_to_segment(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    """點到線段 AB 的距離(m;C# ``Polygon2D.DistanceToSegment``:退化線段(長度² < 1e-12)取端點 A)。"""
    dx = bx - ax
    dy = by - ay
    l2 = dx * dx + dy * dy
    if l2 < 1e-12:
        t = 0.0
    else:
        t = ((px - ax) * dx + (py - ay) * dy) / l2
        if t < 0.0:
            t = 0.0
        elif t > 1.0:
            t = 1.0
    cx = ax + t * dx
    cy = ay + t * dy
    return math.sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy))


def point_in_polygon(poly: Sequence[Point], px: float, py: float) -> bool:
    """點是否在多邊形內(含邊上;C# ``Polygon2D.Contains``:少於 3 點一律 False)。"""
    n = len(poly)
    if n < 3:
        return False
    inside = False
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if distance_to_segment(px, py, xi, yi, xj, yj) < ON_EDGE_M:
            return True
        if (yi > py) != (yj > py):
            x_cross = xi + (py - yi) * (xj - xi) / (yj - yi)
            if px < x_cross:
                inside = not inside
        j = i
    return inside


@dataclass(frozen=True)
class DepthZone:
    """一個水深覆寫區(C# ``ZoneDepth.Zone``):id、ENU 多邊形(m)、區域內水深(m)。"""

    id: str
    polygon: tuple[Point, ...]
    depth: float


def zone_depth(base: float, zones: Sequence[DepthZone], x_east: float, y_north: float) -> float:
    """位置的基底水深(不含潮高;C# ``ZoneDepth.DepthAt``):在內的區域中最淺者,皆不在內取 ``base``;區域比基底深者不覆寫。"""
    d = base
    for z in zones:
        if point_in_polygon(z.polygon, x_east, y_north) and z.depth < d:
            d = z.depth
    return d


def depth_zones_from_scenario(zones: Any) -> list[DepthZone]:
    """由正規化情境的 ``depthZones``(``scenario.normalise_depth_zones`` 的輸出:``[{id, waterDepth_m, polygon: [[x, y], …]}]``)建立;None/空 → []。"""
    out: list[DepthZone] = []
    for z in zones or []:
        out.append(DepthZone(str(z["id"]), tuple((float(p[0]), float(p[1])) for p in z["polygon"]), float(z["waterDepth_m"])))
    return out
