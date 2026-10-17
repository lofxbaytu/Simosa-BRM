using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Updates;

/// <summary>
/// 船上筆電 state/installed.json:現行更新包版本、曾套用最高版本(防降版/重播)、各元件版次(船上版次顯示與教官端兩船版次清單)。
/// </summary>
public sealed class InstalledState
{
    public const string FormatId = "simosa-installed/1";

    public string Format { get; set; } = FormatId;
    public string? Version { get; set; }
    public string? PackageId { get; set; }
    public string? Kind { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public InstalledSigner? Signer { get; set; }
    /// <summary>曾套用過的最高版本(rollback 後不下降)</summary>
    public string? HighestVersion { get; set; }
    /// <summary>各種類的現行版次(software/scenarios/parameters/charts/trust)</summary>
    public Dictionary<string, ComponentVersion> Components { get; set; } = new();
    public ParameterSetRef? ParameterSet { get; set; }
    public List<SmsDocumentRef> SmsDocuments { get; set; } = new();
    /// <summary>最近套用的更新包(新到舊,最多 50 筆)</summary>
    public List<AppliedPackage> History { get; set; } = new();

    public static InstalledState LoadOrNew(string path)
        => File.Exists(path) ? PackageJson.Load<InstalledState>(path, "installed.json") : new InstalledState();

    public void Save(string path) => PackageJson.SaveAtomic(path, this);

    public bool HasApplied(string packageId) => History.Any(h => h.PackageId == packageId);
}

public sealed class InstalledSigner
{
    public string KeyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class ComponentVersion
{
    public string Version { get; set; } = "";
    public string PackageId { get; set; } = "";
    public DateTime AppliedUtc { get; set; }
    public string? Summary { get; set; }
}

public sealed class AppliedPackage
{
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime AppliedUtc { get; set; }
    public string Signer { get; set; } = "";
    public string? Summary { get; set; }
    public bool RolledBack { get; set; }
}
