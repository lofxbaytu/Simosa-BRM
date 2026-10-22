namespace SimosaBRM.Visual.Shared.Area;

/// <summary>
/// 引擎無關的三角網格(場景座標 x 東、y 上、z 南):頂點、法線、索引(逆時針為正面,Godot 預設背面剔除)。
/// Godot 端以 ArrayMesh/SurfaceTool 建立;單元測試直接檢查幾何。
/// </summary>
public sealed class MeshData
{
    private readonly List<float> _vertices = new();
    private readonly List<float> _normals = new();
    private readonly List<int> _indices = new();

    public string Name { get; init; } = "";
    public int VertexCount => _vertices.Count / 3;
    public int TriangleCount => _indices.Count / 3;
    public IReadOnlyList<float> Vertices => _vertices;
    public IReadOnlyList<float> Normals => _normals;
    public IReadOnlyList<int> Indices => _indices;

    public int AddVertex(double x, double y, double z, double nx, double ny, double nz)
    {
        _vertices.Add((float)x); _vertices.Add((float)y); _vertices.Add((float)z);
        _normals.Add((float)nx); _normals.Add((float)ny); _normals.Add((float)nz);
        return VertexCount - 1;
    }

    public void AddTriangle(int a, int b, int c)
    {
        _indices.Add(a); _indices.Add(b); _indices.Add(c);
    }

    /// <summary>加入一個四邊形(a b c d 逆時針)。</summary>
    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    public (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) Bounds()
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (var i = 0; i < _vertices.Count; i += 3)
        {
            minX = Math.Min(minX, _vertices[i]); maxX = Math.Max(maxX, _vertices[i]);
            minY = Math.Min(minY, _vertices[i + 1]); maxY = Math.Max(maxY, _vertices[i + 1]);
            minZ = Math.Min(minZ, _vertices[i + 2]); maxZ = Math.Max(maxZ, _vertices[i + 2]);
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>以頂點 a、b、c 的叉積判斷三角形法線的 y 分量(>0 表示自上方看為逆時針,亦即朝上的面)。</summary>
    public double TriangleNormalY(int tri)
    {
        var a = tri * 3;
        var (ax, ay, az) = V(_indices[a]);
        var (bx, by, bz) = V(_indices[a + 1]);
        var (cx, cy, cz) = V(_indices[a + 2]);
        var ux = bx - ax; var uy = by - ay; var uz = bz - az;
        var vx = cx - ax; var vy = cy - ay; var vz = cz - az;
        return uz * vx - ux * vz;
    }

    private (double, double, double) V(int i) => (_vertices[i * 3], _vertices[i * 3 + 1], _vertices[i * 3 + 2]);
}
