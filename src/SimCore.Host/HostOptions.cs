using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Host;

/// <summary>指令列參數。</summary>
public sealed class HostOptions
{
    public string? Ship { get; set; }
    public LoadingCondition? Loading { get; set; }
    public string Scenario { get; set; } = "data/scenarios/E01_baseline.yaml";
    public bool Record { get; set; }
    public string? Replay { get; set; }
    public int Port { get; set; } = 8765;
    public bool EnableWebSocket { get; set; } = true;
    public bool EnableUdpBus { get; set; } = true;
    public bool EnableNmea { get; set; } = true;
    public string MulticastGroup { get; set; } = "239.255.70.1";
    public int MulticastPort { get; set; } = 7001;
    public int NmeaPort { get; set; } = 10110;
    public string NmeaMulticastGroup { get; set; } = "239.192.0.1";
    public int NmeaMulticastPort { get; set; } = 60001;
    public double? TimeScale { get; set; }
    /// <summary>模擬秒數到達後自動結束(0 = 不限)</summary>
    public double Duration { get; set; }
    /// <summary>動力學模型:mmg(預設,讀 data/ships/&lt;ID&gt;/coefficients.&lt;loading&gt;.json)或 placeholder(Nomoto 暫代)</summary>
    public string Dynamics { get; set; } = "mmg";
    public bool Help { get; set; }

    public const string Usage = """
        用法:SimosaBRM.SimCore.Host [選項]
          --ship FSB1|FSB2            覆寫情境的自船
          --loading full|ballast|intermediate
          --scenario <path.yaml>      情境檔(預設 data/scenarios/E01_baseline.yaml)
          --record                    紀錄到 build/records/<yyyyMMdd-HHmmss>-<scenario>.jsonl
          --replay <file.jsonl>       離線重播紀錄並比對狀態雜湊(不啟動網路)
          --port 8765                 WebSocket 埠(ws://0.0.0.0:8765)
          --timescale 1               初始時間倍率(0.1–10)
          --duration <秒>             模擬時間到達後自動結束(煙霧測試用)
          --dynamics mmg|placeholder  動力學模型(預設 mmg:MMG 完整模型,係數 data/ships/<ID>/coefficients.<loading>.json)
          --no-ws | --no-udp | --no-nmea
          --multicast 239.255.70.1:7001     狀態匯流排多播群組
          --nmea-port 10110                 NMEA 單播埠(127.0.0.1,OpenCPN 預設)
          --nmea-multicast 239.192.0.1:60001
          -h | --help
        """;

    public static HostOptions Parse(string[] args)
    {
        var o = new HostOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 需要參數值");
            switch (a)
            {
                case "--ship": o.Ship = Next(); break;
                case "--loading": o.Loading = Enum.Parse<LoadingCondition>(Next(), ignoreCase: true); break;
                case "--scenario": o.Scenario = Next(); break;
                case "--record": o.Record = true; break;
                case "--replay": o.Replay = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--timescale": o.TimeScale = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--duration": o.Duration = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dynamics":
                    o.Dynamics = Next().ToLowerInvariant();
                    if (o.Dynamics is not ("mmg" or "placeholder")) throw new ArgumentException($"--dynamics 須為 mmg 或 placeholder:{o.Dynamics}");
                    break;
                case "--no-ws": o.EnableWebSocket = false; break;
                case "--no-udp": o.EnableUdpBus = false; break;
                case "--no-nmea": o.EnableNmea = false; break;
                case "--multicast": (o.MulticastGroup, o.MulticastPort) = SplitEndpoint(Next(), o.MulticastPort); break;
                case "--nmea-port": o.NmeaPort = int.Parse(Next()); break;
                case "--nmea-multicast": (o.NmeaMulticastGroup, o.NmeaMulticastPort) = SplitEndpoint(Next(), o.NmeaMulticastPort); break;
                case "-h": case "--help": o.Help = true; break;
                default: throw new ArgumentException($"未知參數:{a}\n{Usage}");
            }
        }
        return o;
    }

    private static (string, int) SplitEndpoint(string s, int defaultPort)
    {
        var idx = s.LastIndexOf(':');
        return idx < 0 ? (s, defaultPort) : (s[..idx], int.Parse(s[(idx + 1)..]));
    }
}
