using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Sync;

/// <summary>船上筆電端的服務組合(直連 serve 與隨身碟匯入/匯出共用)。</summary>
public sealed class ShipNode
{
    public ShipNode(InstallLayout layout, TimeProvider? clock = null)
    {
        Layout = layout;
        Clock = clock ?? TimeProvider.System;
        Updates = new UpdateApplier(layout, Clock);
        Records = new ShipRecordManager(layout, Clock);
    }

    public InstallLayout Layout { get; }
    public TimeProvider Clock { get; }
    public UpdateApplier Updates { get; }
    public ShipRecordManager Records { get; }

    public DeviceInfo Device => DeviceKeyStore.LoadInfo(Layout);

    public string ShipId => Layout.Config.ShipId ?? Device.ShipId;

    public ShipStatus Status()
    {
        var installed = InstalledState.LoadOrNew(Layout.InstalledStatePath);
        DeviceInfo? dev = DeviceKeyStore.Exists(Layout) ? DeviceKeyStore.LoadInfo(Layout) : null;
        using var trust = TrustStore.LoadDirectory(Layout.TrustedKeysDir);
        var warnings = new List<string>();
        if (dev is null) warnings.Add("尚未產生裝置金鑰(simosa-pkg device init)");
        else if (dev.Protection != "dpapi") warnings.Add("裝置私鑰以 PEM 保存(僅限開發/測試)");
        if (trust.Keys.Count == 0) warnings.Add("信任清單為空(config/trusted-keys/),無法驗證更新包");
        else if (!trust.Keys.Any(k => k.Role == KeyRoles.Backup)) warnings.Add("信任清單沒有備援金鑰");
        warnings.AddRange(Records.RetentionWarnings());
        return new ShipStatus
        {
            ShipId = Layout.Config.ShipId ?? dev?.ShipId, DeviceId = dev?.DeviceId,
            Version = installed.Version, PackageId = installed.PackageId, AppliedUtc = installed.AppliedUtc, Signer = installed.Signer,
            HighestVersion = installed.HighestVersion, Components = installed.Components, ParameterSet = installed.ParameterSet, SmsDocuments = installed.SmsDocuments,
            TrustedKeys = trust.Keys.Select(k => new TrustedKeyInfo(k.KeyId, k.Name, k.Role, k.Holder)).ToList(),
            OutstandingRecordPackages = Records.Outstanding().Count, VisitSeq = Records.LoadLedger().CurrentVisit,
            RollbackAvailable = File.Exists(Path.Combine(Layout.PreviousDir, "previous.json")),
            Warnings = warnings,
        };
    }
}
