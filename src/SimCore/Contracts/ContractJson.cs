using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

/// <summary>
/// 所有對外 JSON(狀態廣播、指令、紀錄、快照)共用的 System.Text.Json 設定:camelCase、省略 null、列舉以字串輸出。
/// 欄位名稱以 src/Contracts/*.schema.json 為準。
/// </summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            // 中文註解與船名不轉義,方便人工閱讀紀錄檔
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(ContractEnumNamingPolicy.Instance));
        return o;
    }

    /// <summary>
    /// 列舉命名:一般列舉用 camelCase(full/ballast、running、setEnvironment);
    /// 全大寫代號(車鐘 HAH、NAVF…)維持原樣,符合 state.schema.json 的 telegraph 列舉。
    /// 讀取時 JsonStringEnumConverter 不分大小寫。
    /// </summary>
    private sealed class ContractEnumNamingPolicy : JsonNamingPolicy
    {
        public static readonly ContractEnumNamingPolicy Instance = new();
        public override string ConvertName(string name)
            => name.All(c => char.IsUpper(c) || char.IsDigit(c)) ? name : CamelCase.ConvertName(name);
    }

    /// <summary>列舉值在 JSON 中的名稱(與序列化一致;供狀態欄位以字串輸出列舉)。</summary>
    public static string EnumName<T>(T value) where T : struct, Enum => ContractEnumNamingPolicy.Instance.ConvertName(value.ToString());

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
