using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 內建 VHF 角色扮演頻道(需求追溯表 R1「VHF/DSC」、R6「角色扮演」;docs/design/vhf-roleplay.md、src/Contracts/vhf-protocol.md):
/// 以程序內的 VhfRelayServer + 多個 ClientWebSocket(loopback)驗證同頻道轉送(文字框與二進位音訊框)、不同頻道不轉送、
/// 雙守聽、PTT 佔用(後發者收到 busy)、DSC 全站廣播、斷線自動結束通話、紀錄 vhf 行(帶外:重播雜湊與事件序列不受影響)。
/// </summary>
public class VhfRelayTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// 一個 VHF 站(ClientWebSocket):背景讀取迴圈把收到的框放進通道(文字 JSON 或二進位),等待時逾時只取消「等通道」而不取消 ReceiveAsync
    /// (取消 ClientWebSocket.ReceiveAsync 會把連線設為 Aborted)。
    /// </summary>
    private sealed class Radio : IAsyncDisposable
    {
        public readonly ClientWebSocket Socket = new();
        public string Id = "";
        public string Name;
        private readonly System.Threading.Channels.Channel<(JsonElement? Json, byte[]? Binary)> _inbox = System.Threading.Channels.Channel.CreateUnbounded<(JsonElement?, byte[]?)>();
        private Task? _reader;

        public Radio(string name) { Name = name; }

        public async Task ConnectAsync(VhfRelayServer server)
        {
            await Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            _reader = Task.Run(ReadLoopAsync);
        }

        public async Task JoinAsync(VhfRelayServer server, string channel, params string[] watch)
        {
            await ConnectAsync(server);
            await SendAsync(new { type = "vhf", kind = "join", station = Name, channel, watch });
            var joined = await ExpectAsync("joined");
            Id = joined.GetProperty("id").GetString()!;
            Assert.Equal(channel, joined.GetProperty("channel").GetString());
        }

        public Task SendAsync(object frame) => Socket.SendAsync(ContractJson.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
        public Task SendAudioAsync(byte[] frame) => Socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (Socket.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await Socket.ReceiveAsync(buffer, CancellationToken.None);
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Binary) _inbox.Writer.TryWrite((null, ms.ToArray()));
                    else
                    {
                        using var doc = JsonDocument.Parse(ms.ToArray());
                        _inbox.Writer.TryWrite((doc.RootElement.Clone(), null));
                    }
                }
            }
            catch (Exception) { /* 連線關閉 */ }
            _inbox.Writer.TryComplete();
        }

        /// <summary>讀一框(逾時或連線結束回 null)。</summary>
        public async Task<(JsonElement? Json, byte[]? Binary)?> ReceiveAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) return null;
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                if (!await _inbox.Reader.WaitToReadAsync(cts.Token)) return null;
                return _inbox.Reader.TryRead(out var item) ? item : null;
            }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>等到指定 kind 的文字框(略過 roster 等其他框);逾時失敗。</summary>
        public async Task<JsonElement> ExpectAsync(string kind, double timeoutS = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
            while (DateTime.UtcNow < deadline)
            {
                var f = await ReceiveAsync(deadline - DateTime.UtcNow);
                if (f is null) break;
                if (f.Value.Json is { } j && j.GetProperty("kind").GetString() == kind) return j;
            }
            throw new Xunit.Sdk.XunitException($"{Name} 在 {timeoutS} s 內未收到 {kind}");
        }

        /// <summary>等到二進位框;逾時失敗。</summary>
        public async Task<byte[]> ExpectBinaryAsync(double timeoutS = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
            while (DateTime.UtcNow < deadline)
            {
                var f = await ReceiveAsync(deadline - DateTime.UtcNow);
                if (f is null) break;
                if (f.Value.Binary is { } b) return b;
            }
            throw new Xunit.Sdk.XunitException($"{Name} 在 {timeoutS} s 內未收到二進位框");
        }

        /// <summary>在時限內不得收到 roster 以外的任何框(不同頻道不轉送)。</summary>
        public async Task ExpectNothingAsync(double seconds = 0.4)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                var f = await ReceiveAsync(deadline - DateTime.UtcNow);
                if (f is null) return;
                if (f.Value.Binary is not null) throw new Xunit.Sdk.XunitException($"{Name} 不應收到二進位框");
                var kind = f.Value.Json!.Value.GetProperty("kind").GetString();
                if (kind != "roster") throw new Xunit.Sdk.XunitException($"{Name} 不應收到 {kind}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(1000).Token);
            }
            catch (Exception) { }
            if (_reader is not null) { try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } }
            Socket.Dispose();
        }
    }

    private static async Task<(VhfRelayServer Server, List<VhfLogEntry> Logged)> StartAsync()
    {
        var server = new VhfRelayServer(FreePort());
        var logged = new List<VhfLogEntry>();
        server.Logged += e => { lock (logged) logged.Add(e); };
        server.Start();
        await Task.Yield();
        return (server, logged);
    }

    private static async Task WaitLoggedAsync(List<VhfLogEntry> logged, int count)
    {
        for (var i = 0; i < 300; i++)
        {
            lock (logged) if (logged.Count >= count) return;
            await Task.Delay(10);
        }
        lock (logged) Assert.True(logged.Count >= count, $"紀錄項 {logged.Count} < {count}");
    }

    [Fact]
    public async Task SameChannel_RelaysPttAndAudio_OtherChannelDoesNot_CallLogged()
    {
        var (server, logged) = await StartAsync();
        await using var _ = server;
        await using var a = new Radio("bridge");
        await using var b = new Radio("instructor");
        await using var c = new Radio("ecr");
        await a.JoinAsync(server, "16");
        await b.JoinAsync(server, "16");
        await c.JoinAsync(server, "12");
        Assert.Equal(3, server.StationCount);

        await a.SendAsync(new { type = "vhf", kind = "pttStart" });
        var start = await b.ExpectAsync("pttStart");
        Assert.Equal(a.Id, start.GetProperty("id").GetString());
        Assert.Equal("bridge", start.GetProperty("station").GetString());
        Assert.Equal("16", start.GetProperty("channel").GetString());

        var payload = Encoding.UTF8.GetBytes("webm-chunk-1");
        var frame = VhfRelayServer.EncodeAudioFrame(a.Id, payload);
        await a.SendAudioAsync(frame);
        var got = await b.ExpectBinaryAsync();
        Assert.Equal(frame, got);
        Assert.True(VhfRelayServer.HeaderMatches(got, a.Id));
        // 其他連線的 id 不轉送(標頭 id 須為發話者自己的)
        await a.SendAudioAsync(VhfRelayServer.EncodeAudioFrame(b.Id, payload));
        await Task.Delay(100);
        await a.SendAsync(new { type = "vhf", kind = "pttEnd" });
        var end = await b.ExpectAsync("pttEnd");
        Assert.Equal(a.Id, end.GetProperty("id").GetString());
        Assert.True(end.GetProperty("duration_s").GetDouble() >= 0);
        Assert.Equal("pttEnd", end.GetProperty("reason").GetString());
        // 不同頻道的站什麼都沒收到(roster 除外)
        await c.ExpectNothingAsync();
        await WaitLoggedAsync(logged, 1);
        lock (logged)
        {
            var call = Assert.Single(logged);
            Assert.Equal(VhfRelayServer.KindCall, call.Kind);
            Assert.Equal("bridge", call.Station);
            Assert.Equal("16", call.Channel);
            Assert.NotNull(call.DurationS);
            var ev = call.ToEvent(123, 2.46);
            Assert.Equal("vhfCall", ev.Kind);
            Assert.Equal("vhf", ev.Source);
            Assert.Equal("16", ev.ItemId);
            Assert.Equal(123, ev.Tick);
            Assert.Equal(-1, ev.Seq);
            Assert.True(ev.Visible);
            Assert.Equal("bridge", ev.DataString("station"));
        }
        Assert.Equal(1, Interlocked.Read(ref server.AudioFramesRelayed));
        Assert.Equal(1, Interlocked.Read(ref server.AudioFramesDropped));
    }

    [Fact]
    public async Task SimultaneousPtt_SecondGetsBusy_AudioNotRelayed_ThenFreeAfterFirstEnds()
    {
        var (server, logged) = await StartAsync();
        await using var _ = server;
        await using var a = new Radio("bridge");
        await using var b = new Radio("pilot");
        await a.JoinAsync(server, "12");
        await b.JoinAsync(server, "12");

        await a.SendAsync(new { type = "vhf", kind = "pttStart" });
        await b.ExpectAsync("pttStart");
        await b.SendAsync(new { type = "vhf", kind = "pttStart" });
        var busy = await b.ExpectAsync("busy");
        Assert.Equal("12", busy.GetProperty("channel").GetString());
        Assert.Equal(a.Id, busy.GetProperty("by").GetString());
        Assert.Equal("bridge", busy.GetProperty("byStation").GetString());
        // 被拒者的音訊不轉送
        await b.SendAudioAsync(VhfRelayServer.EncodeAudioFrame(b.Id, new byte[] { 1, 2, 3 }));
        await a.ExpectNothingAsync();
        Assert.Equal(1, Interlocked.Read(ref server.AudioFramesDropped));
        // 被拒者的 pttEnd 不產生通話紀錄
        await b.SendAsync(new { type = "vhf", kind = "pttEnd" });
        await a.SendAsync(new { type = "vhf", kind = "pttEnd" });
        await b.ExpectAsync("pttEnd");
        await WaitLoggedAsync(logged, 1);
        await Task.Delay(100);
        lock (logged) Assert.Single(logged);
        // 頻道釋放後 B 可以發話
        await b.SendAsync(new { type = "vhf", kind = "pttStart" });
        var start = await a.ExpectAsync("pttStart");
        Assert.Equal(b.Id, start.GetProperty("id").GetString());
        await b.SendAsync(new { type = "vhf", kind = "pttEnd" });
        await a.ExpectAsync("pttEnd");
        await WaitLoggedAsync(logged, 2);
    }

    [Fact]
    public async Task DualWatch_TextOnEitherChannelReceived_OtherStationFiltered_DscToAll()
    {
        var (server, logged) = await StartAsync();
        await using var _ = server;
        await using var instructor = new Radio("instructor");
        await using var bridge = new Radio("bridge");
        await using var tug = new Radio("tug");
        await instructor.JoinAsync(server, "16", "12");
        await bridge.JoinAsync(server, "12");
        await tug.JoinAsync(server, "67");

        await bridge.SendAsync(new { type = "vhf", kind = "text", text = "Port Control, bridge on 12" });
        var echo = await bridge.ExpectAsync("text"); // 發話者自己也收到(訊息列一致)
        Assert.Equal("12", echo.GetProperty("channel").GetString());
        var rx = await instructor.ExpectAsync("text");
        Assert.Equal("bridge", rx.GetProperty("station").GetString());
        Assert.Equal("Port Control, bridge on 12", rx.GetProperty("text").GetString());
        await tug.ExpectNothingAsync();

        await instructor.SendAsync(new { type = "vhf", kind = "text", channel = "16", text = "All ships, securité" });
        await instructor.ExpectAsync("text");
        await bridge.ExpectNothingAsync(); // bridge 只在 12
        // 換頻道到 16 後收得到
        await bridge.SendAsync(new { type = "vhf", kind = "channel", channel = "16" });
        var ch = await bridge.ExpectAsync("channel");
        Assert.Equal("16", ch.GetProperty("channel").GetString());
        await instructor.SendAsync(new { type = "vhf", kind = "text", text = "bridge, instructor on 16" });
        await instructor.ExpectAsync("text");
        var rx2 = await bridge.ExpectAsync("text");
        Assert.Equal("16", rx2.GetProperty("channel").GetString());

        // DSC:不分頻道送給所有站(含發話者),類別須合法
        await tug.SendAsync(new { type = "vhf", kind = "dsc", category = "DISTRESS", text = "MAYDAY tug fire" });
        foreach (var r in new[] { instructor, bridge, tug })
        {
            var d = await r.ExpectAsync("dsc");
            Assert.Equal("distress", d.GetProperty("category").GetString());
            Assert.Equal("70", d.GetProperty("channel").GetString());
            Assert.Equal("tug", d.GetProperty("station").GetString());
        }
        await tug.SendAsync(new { type = "vhf", kind = "dsc", category = "panic" });
        var err = await tug.ExpectAsync("error");
        Assert.Contains("category", err.GetProperty("message").GetString());
        await WaitLoggedAsync(logged, 4);
        lock (logged)
        {
            Assert.Equal(new[] { "vhfText", "vhfText", "vhfText", "vhfDsc" }, logged.Select(l => l.Kind));
            Assert.Equal("distress", logged[3].Category);
            Assert.Equal("MAYDAY tug fire", logged[3].Text);
            Assert.Contains("DISTRESS", logged[3].ToEvent(0, 0).Message);
        }
    }

    [Fact]
    public async Task NotJoined_Rejected_UnknownChannel_Rejected_DisconnectEndsTransmit()
    {
        var (server, logged) = await StartAsync();
        await using var _ = server;
        await using var a = new Radio("bridge");
        await a.ConnectAsync(server);
        await a.SendAsync(new { type = "vhf", kind = "pttStart" });
        var err = await a.ExpectAsync("error");
        Assert.Contains("join", err.GetProperty("message").GetString());
        await a.SendAsync(new { type = "vhf", kind = "join", station = "bridge", channel = "99" });
        var joined = await a.ExpectAsync("joined");
        Assert.Equal("16", joined.GetProperty("channel").GetString()); // 不支援的頻道退回 16
        await a.SendAsync(new { type = "vhf", kind = "channel", channel = "70" });
        Assert.Contains("頻道", (await a.ExpectAsync("error")).GetProperty("message").GetString());

        await using var b = new Radio("instructor");
        await b.JoinAsync(server, "16");
        await a.SendAsync(new { type = "vhf", kind = "pttStart" });
        await b.ExpectAsync("pttStart");
        // 發話中斷線:聽者收到 pttEnd(reason disconnected)、通話仍記錄
        await a.DisposeAsync();
        var end = await b.ExpectAsync("pttEnd");
        Assert.Equal("disconnected", end.GetProperty("reason").GetString());
        await WaitLoggedAsync(logged, 1);
        lock (logged) Assert.Equal("disconnected", logged[0].EndReason);
        for (var i = 0; i < 200 && server.StationCount != 1; i++) await Task.Delay(10);
        Assert.Equal(1, server.StationCount);
    }

    /// <summary>紀錄 vhf 行:寫進開啟中的紀錄、RecordReader 讀回、不影響重播的狀態雜湊與事件序列(帶外行)。</summary>
    [Fact]
    public async Task VhfLines_WrittenToRecord_ReplayHashAndEventSequenceUnaffected()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"vhf-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        var writer = new RecordWriter(path, engine);
        var (server, _) = await StartAsync();
        await using var _ = server;
        var written = 0;
        server.Logged += entry => { if (writer.WriteVhf(entry.ToEvent(engine.Tick, engine.Time))) Interlocked.Increment(ref written); };

        engine.Enqueue(SimCommand.Rudder(10));
        engine.ProcessPendingCommands();
        TestData.RunTo(engine, 1.0);
        await using var a = new Radio("bridge");
        await using var b = new Radio("vts");
        await a.JoinAsync(server, "16");
        await b.JoinAsync(server, "16");
        await a.SendAsync(new { type = "vhf", kind = "pttStart" });
        await b.ExpectAsync("pttStart");
        await a.SendAsync(new { type = "vhf", kind = "pttEnd" });
        await b.ExpectAsync("pttEnd");
        await b.SendAsync(new { type = "vhf", kind = "text", text = "Bridge, this is Port VTS, proceed to pilot station" });
        await a.ExpectAsync("text");
        await a.SendAsync(new { type = "vhf", kind = "dsc", category = "urgency", text = "PAN PAN steering failure" });
        await b.ExpectAsync("dsc");
        for (var i = 0; i < 300 && Volatile.Read(ref written) < 3; i++) await Task.Delay(10);
        Assert.Equal(3, Volatile.Read(ref written));
        TestData.RunTo(engine, 2.0);
        var footer = writer.Close();
        Assert.Equal(3, writer.VhfLines);
        Assert.False(writer.WriteVhf(new VhfLogEntry { Kind = "vhfText", Id = "x", Station = "x", Channel = "16", Text = "late", Utc = DateTime.UtcNow }.ToEvent(0, 0)));

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(3, rec.Vhf.Count);
        Assert.Equal(new[] { "vhfCall", "vhfText", "vhfDsc" }, rec.Vhf.Select(v => v.Event.Kind));
        Assert.All(rec.Vhf, v => Assert.Equal("vhf", v.Event.Source));
        Assert.All(rec.Vhf, v => Assert.True(v.Tick >= 50 && v.T >= 1.0));
        Assert.Equal("vts", rec.Vhf[1].Event.DataString("station"));
        Assert.Equal("urgency", rec.Vhf[2].Event.DataString("category"));
        Assert.DoesNotContain(rec.Events, e => e.Event.Source == "vhf"); // 不在 event 行的事件序列內
        Assert.Equal(footer.Lines, rec.Footer!.Lines);

        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.HashMatches, "vhf 行不得影響重播雜湊");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.True(result.IntegrityOk);
    }
}
