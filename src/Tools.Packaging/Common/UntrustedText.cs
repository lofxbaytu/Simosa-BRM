using System.Globalization;
using System.Text;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 對方提供、未經驗證的文字(船上 statusResult / 隨身碟 status.json 裡的船舶與裝置識別與其他文字欄位、解析失敗的訊息、
/// 同步過程中船端送來的警告與錯誤訊息)印到終端機、寫進 fleet-versions.json 之前的處理:去掉會改變終端機顯示的字元並截斷。與船端稽核用的 <c>SyncServer.Clip</c> 同一種作法(去控制字元、截斷加「…」),另外多去掉
/// 格式字元與行 / 段分隔字元 — 終端機上它們能把後面的文字倒過來排(U+202E)、藏起來(零寬字元)或另起一行冒充程式自己的輸出。
/// <list type="bullet">
/// <item>控制字元(Cc:含 ESC,ANSI 跳脫序列少了 ESC 只剩普通文字;CR / LF / TAB;C1 控制字元 U+0080–U+009F)</item>
/// <item>格式字元(Cf:雙向控制、零寬字元、BOM)、行分隔 U+2028、段分隔 U+2029</item>
/// <item>不成對的代理字元換成 U+FFFD</item>
/// </list>
/// 版本欄位不經過這裡:只接受 1 至 4 段數字(<see cref="Sync.ShipReleaseReport.IsVersion"/>),其他內容整個不用。
/// </summary>
public static class UntrustedText
{
    /// <param name="max">最多保留的字數(以 Unicode 純量計);超過時截斷並加「…」</param>
    public static string Clean(string? text, int max = 128)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max) + 1);
        var kept = 0;
        foreach (var rune in text.EnumerateRunes())   // 不成對的代理字元在這裡已是 U+FFFD
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            if (kept == max)
            {
                sb.Append('…');
                break;
            }
            sb.Append(rune.ToString());
            kept++;
        }
        return sb.ToString();
    }
}
