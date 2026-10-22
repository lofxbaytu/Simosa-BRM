using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.Visual.Shared.State;

/// <summary>
/// 由狀態幀的 pos(lat/lon 與 ENU x/y 同時給出)反推情境的 ENU 原點,讓視景能把區域資料(經緯度)投影到同一個 ENU。
/// 平面近似下 x = (lon − lon0)·k_lon(lat0)、y = (lat − lat0)·k_lat(lat0),尺度係數取決於原點緯度,故以原點緯度迭代 4 次(收斂到 1e-9°)。
/// </summary>
public static class OriginSolver
{
    public static LocalTangentPlane FromPosition(GeoPosition p) => FromPosition(p.Lat, p.Lon, p.X, p.Y);

    public static LocalTangentPlane FromPosition(double lat, double lon, double xEast, double yNorth)
    {
        var lat0 = lat;
        var lon0 = lon;
        for (var i = 0; i < 4; i++)
        {
            var k = new LocalTangentPlane(lat0, lon0);
            var metresPerDegLat = 1.0 / (k.ToGeodetic(0, 1).Lat - lat0);
            var metresPerDegLon = 1.0 / (k.ToGeodetic(1, 0).Lon - lon0);
            lat0 = lat - yNorth / metresPerDegLat;
            lon0 = lon - xEast / metresPerDegLon;
        }
        return new LocalTangentPlane(lat0, lon0);
    }
}
