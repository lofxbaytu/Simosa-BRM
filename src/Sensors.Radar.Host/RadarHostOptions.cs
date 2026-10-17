using System.Globalization;

namespace SimosaBRM.Sensors.Radar.Host;

/// <summary>雷達主控程式的指令列參數。</summary>
public sealed class RadarHostOptions
{
    public Uri Sim { get; set; } = new("ws://127.0.0.1:8765/");
    public int Port { get; set; } = 8766;
    public bool LocalhostOnly { get; set; }
    public List<string> Radars { get; } = new() { "x", "s" };
    public ulong? Seed { get; set; }
    public string? Areas { get; set; } = "data/areas";
    public string? Environment { get; set; }
    public bool Nmea { get; set; } = true;
    public string NmeaHost { get; set; } = "127.0.0.1";
    public int NmeaPortX { get; set; } = 10111;
    public int NmeaPortS { get; set; } = 10112;
    public string Talker { get; set; } = "RA";
    public double Rpm { get; set; } = 24.0;
    public int Cells { get; set; } = 512;
    public (double Inner, double Outer)? Guard { get; set; }
    /// <summary>牆鐘秒數到達後結束(0 = 不限)</summary>
    public double Duration { get; set; }
    /// <summary>每隔幾模擬秒列印 ARPA 與真值比對(0 = 不列印)</summary>
    public double TruthReport { get; set; }
    public bool ForceDisplay { get; set; }
    public bool Help { get; set; }

    public const string Usage = """
        用法:SimosaBRM.Sensors.Radar.Host [選項]
          --sim ws://127.0.0.1:8765/      SimCore.Host 狀態 WebSocket
          --port 8766                     雷達 WebSocket 埠(ws://0.0.0.0:8766/radar/x、/radar/s)
          --localhost-only                只監聽 localhost
          --radars x,s                    啟用的雷達(X 頻、S 頻)
          --seed <n>                      亂數種子(預設:情境檔 seed;讀不到時以情境代號雜湊)
          --areas data/areas              陸地多邊形資料夾或檔案(*.json;不存在則無陸地)
          --no-areas                      不載入陸地
          --environment <file.json>       附加雷達環境(雨區、海況覆寫;radar.schema.json $defs/environment)
          --rpm 24                        天線轉速
          --cells 512                     顯示影像每線距離格數
          --guard <inner>,<outer>         啟用保護區自動擷取(nm,全周)
          --no-nmea                       不送 NMEA TTM/TLL
          --nmea-host 127.0.0.1           NMEA UDP 目的地
          --nmea-port-x 10111 --nmea-port-s 10112
          --talker RA                     NMEA talker(與閘道真值 TTM 以埠區分;可改 AR 等)
          --duration <秒>                 牆鐘時間到達後結束(煙霧測試用)
          --truth-report <秒>             每隔幾模擬秒列印 ARPA 與真值比對(教官/驗收用)
          --force-display                 無客戶端時仍產生顯示影像(效能量測)
          -h | --help
        """;

    public static RadarHostOptions Parse(string[] args)
    {
        var o = new RadarHostOptions();
        var inv = CultureInfo.InvariantCulture;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 需要參數值");
            switch (a)
            {
                case "--sim": o.Sim = new Uri(Next()); break;
                case "--port": o.Port = int.Parse(Next(), inv); break;
                case "--localhost-only": o.LocalhostOnly = true; break;
                case "--radars":
                    o.Radars.Clear();
                    o.Radars.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToLowerInvariant()));
                    if (o.Radars.Any(r => r is not ("x" or "s"))) throw new ArgumentException("--radars 只接受 x、s");
                    break;
                case "--seed": o.Seed = ulong.Parse(Next(), inv); break;
                case "--areas": o.Areas = Next(); break;
                case "--no-areas": o.Areas = null; break;
                case "--environment": o.Environment = Next(); break;
                case "--rpm": o.Rpm = double.Parse(Next(), inv); break;
                case "--cells": o.Cells = int.Parse(Next(), inv); break;
                case "--guard":
                {
                    var p = Next().Split(',');
                    o.Guard = (double.Parse(p[0], inv), double.Parse(p[1], inv));
                    break;
                }
                case "--no-nmea": o.Nmea = false; break;
                case "--nmea-host": o.NmeaHost = Next(); break;
                case "--nmea-port-x": o.NmeaPortX = int.Parse(Next(), inv); break;
                case "--nmea-port-s": o.NmeaPortS = int.Parse(Next(), inv); break;
                case "--talker": o.Talker = Next().ToUpperInvariant(); break;
                case "--duration": o.Duration = double.Parse(Next(), inv); break;
                case "--truth-report": o.TruthReport = double.Parse(Next(), inv); break;
                case "--force-display": o.ForceDisplay = true; break;
                case "-h": case "--help": o.Help = true; break;
                default: throw new ArgumentException($"未知參數:{a}\n{Usage}");
            }
        }
        if (o.Talker.Length != 2) throw new ArgumentException("--talker 須為 2 個字元");
        return o;
    }
}
