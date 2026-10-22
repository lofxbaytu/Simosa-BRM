using Godot;
using SimosaBRM.Visual.Shared.Area;

namespace SimosaBRM.Visual.Godot;

/// <summary>Visual.Shared 的 MeshData → Godot ArrayMesh;材質以簡單 StandardMaterial3D(無貼圖資產,避免匯入步驟)。</summary>
public static class MeshFactory
{
    public static ArrayMesh ToArrayMesh(MeshData data, Material? material = null)
    {
        var mesh = new ArrayMesh();
        if (data.TriangleCount == 0) return mesh;
        var verts = new Vector3[data.VertexCount];
        var normals = new Vector3[data.VertexCount];
        for (var i = 0; i < data.VertexCount; i++)
        {
            verts[i] = new Vector3(data.Vertices[i * 3], data.Vertices[i * 3 + 1], data.Vertices[i * 3 + 2]);
            normals[i] = new Vector3(data.Normals[i * 3], data.Normals[i * 3 + 1], data.Normals[i * 3 + 2]);
        }
        var indices = new int[data.Indices.Count];
        for (var i = 0; i < indices.Length; i++) indices[i] = data.Indices[i];
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (material is not null) mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    public static MeshInstance3D Instance(MeshData data, Material material, string name)
        => new() { Mesh = ToArrayMesh(data, material), Name = name };

    public static StandardMaterial3D Flat(Color albedo, float roughness = 0.8f)
        => new() { AlbedoColor = albedo, Roughness = roughness, Metallic = 0.0f };

    public static StandardMaterial3D Emissive(Color color, float energy = 4.0f)
        => new() { AlbedoColor = color, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = energy, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

    /// <summary>小球(航行燈、導燈)。</summary>
    public static MeshInstance3D Sphere(float radius, Material material, string name)
        => new() { Mesh = new SphereMesh { Radius = radius, Height = radius * 2, RadialSegments = 8, Rings = 4, Material = material }, Name = name };
}
