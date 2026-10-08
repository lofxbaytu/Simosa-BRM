using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>YAML 情境讀取(YamlDotNet,camelCase 鍵名;未知鍵忽略以便日後擴充)。</summary>
public static class ScenarioLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static Scenario Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var scenario = FromYaml(yaml);
        if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = Path.GetFileNameWithoutExtension(path);
        return scenario.Validate();
    }

    public static Scenario FromYaml(string yaml)
    {
        var scenario = Deserializer.Deserialize<Scenario>(yaml) ?? throw new InvalidDataException("情境 YAML 為空");
        return scenario;
    }

    public static string ToYaml(Scenario scenario) => Serializer.Serialize(scenario);
}
