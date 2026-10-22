using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>測試用路徑:專案根目錄與專案內的暫存輸出(build/visual/tests;CLAUDE.md:不寫到專案外)。</summary>
internal static class TestPaths
{
    public static string Root { get; } = RepositoryPaths.FindRoot();

    public static string Scratch(string name)
    {
        var dir = Path.Combine(Root, "build", "visual", "tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
