using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Geo;

/// <summary>
/// 本地 ENU(東 x、北 y,公尺)與 WGS-84 經緯度的換算。
/// 採平面近似(等距圓柱投影,以原點緯度的子午圈/卯酉圈曲率半徑為尺度),
/// 在港區與沿岸數十公里範圍內誤差小於數公尺,足夠儀器與 NMEA 輸出使用;
/// 跨海區航行(數百公里)時應改以 ECEF 或逐步更新原點(規劃書第 11.3 節)。
/// </summary>
public sealed class LocalTangentPlane
{
    private const double A = 6378137.0;              // WGS-84 長半徑
    private const double F = 1.0 / 298.257223563;    // 扁率
    private static readonly double E2 = F * (2 - F); // 第一偏心率平方

    public double OriginLat { get; }
    public double OriginLon { get; }
    private readonly double _metresPerDegLat;
    private readonly double _metresPerDegLon;

    public LocalTangentPlane(double originLatDeg, double originLonDeg)
    {
        OriginLat = originLatDeg;
        OriginLon = originLonDeg;
        var phi = Units.DegToRad(originLatDeg);
        var s2 = Math.Sin(phi) * Math.Sin(phi);
        var m = A * (1 - E2) / Math.Pow(1 - E2 * s2, 1.5); // 子午圈曲率半徑
        var n = A / Math.Sqrt(1 - E2 * s2);                // 卯酉圈曲率半徑
        _metresPerDegLat = m * Units.RadiansPerDegree;
        _metresPerDegLon = n * Math.Cos(phi) * Units.RadiansPerDegree;
    }

    public (double Lat, double Lon) ToGeodetic(double xEast, double yNorth)
        => (OriginLat + yNorth / _metresPerDegLat, OriginLon + xEast / _metresPerDegLon);

    public (double X, double Y) ToLocal(double latDeg, double lonDeg)
        => ((lonDeg - OriginLon) * _metresPerDegLon, (latDeg - OriginLat) * _metresPerDegLat);
}
