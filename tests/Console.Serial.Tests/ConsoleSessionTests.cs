using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>會話狀態機:握手 ACK(含校驗和)、拒絕行計數、去抖後的指令、狀態回寫、心跳逾時斷線與恢復、核心連線 LED。</summary>
public class ConsoleSessionTests
{
    private const string StateJson = "{\"tick\":10,\"rudder\":2.5,\"rpm\":60,\"heading\":45.2,\"telegraph\":\"HAH\",\"steering\":{\"mode\":\"fu\",\"pump\":\"1\"},\"alerts\":[],\"bnwas\":{\"mode\":\"off\",\"active\":false,\"stage\":0,\"dormant_s\":300}}";

    [Fact]
    public void Hello_WithCs_GetsAckWithChecksum_AndFullResend()
    {
        var s = new ConsoleSession();
        var logs = new List<string>();
        s.Log += logs.Add;
        s.SetCoreConnected(true, 0);
        s.HandleStateFrame(StateJson, 0);
        var o = s.HandleLine("HELLO,1.0.0,RUD+TEL+BTN+LED+DISP+CS*" + LineChecksum.Hex("HELLO,1.0.0,RUD+TEL+BTN+LED+DISP+CS"), 100);
        Assert.True(s.HelloReceived);
        Assert.Equal("1.0.0", s.FirmwareVersion);
        Assert.True(s.ChecksumRequired);
        Assert.Equal("ACK*" + LineChecksum.Hex("ACK"), o.DeviceLines[0]);
        Assert.Contains("LED,LINK,1*" + LineChecksum.Hex("LED,LINK,1"), o.DeviceLines);
        Assert.Contains("DISP,TEL,HAH*" + LineChecksum.Hex("DISP,TEL,HAH"), o.DeviceLines);
        Assert.Contains("DISP,HDG,045*" + LineChecksum.Hex("DISP,HDG,045"), o.DeviceLines);
        Assert.All(o.DeviceLines, l => Assert.Equal(ChecksumStatus.Valid, LineChecksum.Split(l, out _)));
        Assert.Empty(o.Commands);
        Assert.Contains(logs, l => l.Contains("HELLO"));
        // ACK 只出現一次(HELLO 自己的重送不與「第一行」重送重複)
        Assert.Single(o.DeviceLines, l => l.StartsWith("ACK"));
        Assert.Single(o.DeviceLines, l => l.StartsWith("DISP,TEL"));
    }

    [Fact]
    public void Hello_WithoutCs_LinesHaveNoChecksum()
    {
        var s = new ConsoleSession();
        var o = s.HandleLine("HELLO,0.9,RUD", 0);
        Assert.Equal("ACK", o.DeviceLines[0]);
        Assert.Contains("LED,LINK,0", o.DeviceLines);
    }

    [Fact]
    public void RejectedLines_AreCountedAndLogged_NotAnswered()
    {
        var s = new ConsoleSession();
        var logs = new List<string>();
        s.Log += logs.Add;
        s.HandleLine("HELLO,1,CS*" + LineChecksum.Hex("HELLO,1,CS"), 0);
        var o1 = s.HandleLine("RUD,10", 10);           // 缺校驗和
        var o2 = s.HandleLine("RUD,10*00", 20);        // 校驗和錯
        var o3 = s.HandleLine(LineChecksum.Append("XYZ,1"), 30);
        var o4 = s.HandleLine(LineChecksum.Append("RUD,99"), 40);
        var o5 = s.HandleLine(LineChecksum.Append("TEL,WARP"), 50);
        Assert.All(new[] { o1, o2, o3, o4, o5 }, o => Assert.True(o.IsEmpty));
        Assert.Equal(1, s.Stats.ChecksumMissing);
        Assert.Equal(1, s.Stats.ChecksumInvalid);
        Assert.Equal(1, s.Stats.UnknownCommand);
        Assert.Equal(1, s.Stats.OutOfRange);
        Assert.Equal(1, s.Stats.BadArgument);
        Assert.Equal(5, s.Stats.Rejected);
        Assert.Equal(6, s.Stats.Lines);
        Assert.Equal(5, logs.Count(l => l.StartsWith("拒絕")));
        Assert.True(s.DeviceConnected, "拒絕的行仍算裝置活動");
    }

    [Fact]
    public void Rudder_IsDebounced_PendingFlushedByPoll()
    {
        var s = new ConsoleSession();
        var o = s.HandleLine("RUD,10", 0);
        Assert.Single(o.Commands);
        Assert.Equal("{\"type\":\"rudder\",\"value\":10}", o.Commands[0].ToJson());
        Assert.Empty(s.HandleLine("RUD,10.3", 10).Commands);    // < 0.5°
        Assert.Empty(s.HandleLine("RUD,15", 20).Commands);      // 間隔未到:待送
        Assert.Empty(s.Poll(40).Commands);
        var flushed = s.Poll(50).Commands;
        Assert.Single(flushed);
        Assert.Equal(15.0, flushed[0].ValueAsDouble());
        Assert.Equal(2, s.CommandsProduced);
    }

    [Fact]
    public void Telegraph_Button_Nfu_MapImmediately_RpmThrusterDebounced()
    {
        var s = new ConsoleSession();
        Assert.Equal("{\"type\":\"telegraph\",\"value\":\"SAH\"}", s.HandleLine("TEL,SAH", 0).Commands.Single().ToJson());
        Assert.Equal("{\"type\":\"bnwasReset\"}", s.HandleLine("BTN,BNWAS,1", 1).Commands.Single().ToJson());
        Assert.Empty(s.HandleLine("BTN,BNWAS,0", 2).Commands);
        Assert.Equal("{\"type\":\"steeringMode\",\"value\":\"nfu\",\"args\":{\"direction\":\"port\"}}", s.HandleLine("NFU,L", 3).Commands.Single().ToJson());
        Assert.Equal("{\"type\":\"rpm\",\"value\":80}", s.HandleLine("RPM,80", 4).Commands.Single().ToJson());
        Assert.Empty(s.HandleLine("RPM,80.5", 5).Commands);
        Assert.Equal("{\"type\":\"thruster\",\"value\":0.4}", s.HandleLine("THR,40", 6).Commands.Single().ToJson());
        Assert.Empty(s.HandleLine("THR,40.5", 7).Commands);
        Assert.Empty(s.HandleLine("HB", 8).Commands);
    }

    [Fact]
    public void HeartbeatTimeout_DisconnectsStopsFeedback_RecoveryResendsAll()
    {
        var s = new ConsoleSession(heartbeatTimeoutMs: 3000);
        var logs = new List<string>();
        s.Log += logs.Add;
        s.SetCoreConnected(true, 0);
        s.HandleLine("HELLO,1,RUD+LED+DISP", 0);
        Assert.True(s.DeviceConnected);
        Assert.NotEmpty(s.HandleStateFrame(StateJson, 100).DeviceLines);
        Assert.Empty(s.Poll(3000).DeviceLines);
        Assert.True(s.DeviceConnected, "剛好 3 s 不算逾時");
        s.Poll(3001);
        Assert.False(s.DeviceConnected);
        Assert.Equal(1, s.HeartbeatTimeouts);
        Assert.Contains(logs, l => l.Contains("心跳逾時"));
        // 斷線期間狀態仍接收但不回寫
        var during = s.HandleStateFrame(StateJson.Replace("\"heading\":45.2", "\"heading\":90"), 3500);
        Assert.Empty(during.DeviceLines);
        Assert.Empty(s.SetCoreConnected(false, 3600).DeviceLines);
        Assert.Empty(s.Poll(4000).DeviceLines);
        // 再收到任一行即恢復並重送全部(含斷線期間改變的值)
        var back = s.HandleLine("HB", 5000);
        Assert.True(s.DeviceConnected);
        Assert.Contains("DISP,HDG,090", back.DeviceLines);
        Assert.Contains("LED,LINK,0", back.DeviceLines);
        Assert.Contains("LED,FU,1", back.DeviceLines);
        Assert.Contains(logs, l => l.Contains("恢復"));
    }

    [Fact]
    public void StateFrames_OnlyChangesAreWritten_NonStateFramesIgnored()
    {
        var s = new ConsoleSession();
        s.HandleLine("HELLO,1,LED+DISP", 0);
        var first = s.HandleStateFrame(StateJson, 10).DeviceLines;
        Assert.Contains("DISP,RUD,2.5", first);
        Assert.Empty(s.HandleStateFrame(StateJson, 300).DeviceLines);
        Assert.Empty(s.HandleStateFrame("{\"type\":\"event\",\"event\":{\"kind\":\"x\"}}", 310).DeviceLines);
        var changed = s.HandleStateFrame(StateJson.Replace("\"rudder\":2.5", "\"rudder\":-7.25"), 600).DeviceLines;
        Assert.Equal(new[] { "DISP,RUD,-7.3" }, changed);
    }

    [Fact]
    public void CoreConnectionChange_EmitsLinkLedOnlyWhenDeviceConnected()
    {
        var s = new ConsoleSession();
        Assert.Empty(s.SetCoreConnected(true, 0).DeviceLines);   // 裝置尚未連線:不回寫
        var o = s.HandleLine("HB", 1);
        Assert.Contains("LED,LINK,1", o.DeviceLines);
        Assert.Equal(new[] { "LED,LINK,0" }, s.SetCoreConnected(false, 2).DeviceLines);
        Assert.Empty(s.SetCoreConnected(false, 3).DeviceLines);
    }
}
