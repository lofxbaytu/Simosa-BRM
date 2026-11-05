using SimosaBRM.ConsoleSerial.Core;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>協定解析(console-serial.md 第 1–2 節):校驗和、空行/註解、未知命令、各行種類、範圍與夾限。</summary>
public class ProtocolParserTests
{
    [Theory]
    [InlineData("RUD,-12.5", "5A")]
    [InlineData("HB", "0A")]
    public void Checksum_MatchesProtocolExamples(string payload, string hex)
    {
        Assert.Equal(hex, LineChecksum.Hex(payload));
        Assert.Equal($"{payload}*{hex}", LineChecksum.Append(payload));
        Assert.Equal(ChecksumStatus.Valid, LineChecksum.Split($"{payload}*{hex}", out var p));
        Assert.Equal(payload, p);
        Assert.Equal(ChecksumStatus.Valid, LineChecksum.Split($"{payload}*{hex.ToLowerInvariant()}", out _));
    }

    [Theory]
    [InlineData("RUD,-12.5*5B")]
    [InlineData("RUD,-12.5*5")]
    [InlineData("RUD,-12.5*ZZ")]
    public void Checksum_InvalidIsRejected(string line)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.False(r.Ok);
        Assert.Equal(ParseError.ChecksumInvalid, r.Error);
    }

    [Fact]
    public void Checksum_RequiredAfterHelloDeclaresCs()
    {
        var p = new ProtocolParser();
        Assert.True(p.Parse("RUD,10").Ok, "HELLO 前校驗和選填");
        var hello = p.Parse(LineChecksum.Append("HELLO,1.0.0,RUD+TEL+CS"));
        Assert.True(hello.Ok);
        Assert.Equal(new[] { "RUD", "TEL", "CS" }, hello.Message!.Caps);
        Assert.Equal("1.0.0", hello.Message.Text);
        Assert.True(p.RequireChecksum);
        var missing = p.Parse("RUD,10");
        Assert.Equal(ParseError.ChecksumMissing, missing.Error);
        Assert.True(p.Parse(LineChecksum.Append("RUD,10")).Ok);
        // 之後再收到不含 CS 的 HELLO(韌體重新上電)解除必帶
        Assert.True(p.Parse(LineChecksum.Append("HELLO,1.0.1,RUD")).Ok);
        Assert.False(p.RequireChecksum);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 韌體除錯輸出")]
    [InlineData("\r\n")]
    public void EmptyAndCommentLinesAreIgnored(string line)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.False(r.Ok);
        Assert.True(r.Ignored);
        Assert.Equal(ParseError.Empty, r.Error);
    }

    [Fact]
    public void NullIsIgnored()
    {
        Assert.True(new ProtocolParser().Parse(null).Ignored);
    }

    [Theory]
    [InlineData("FOO,1", ParseError.UnknownCommand)]
    [InlineData("RUD", ParseError.BadArgument)]
    [InlineData("RUD,abc", ParseError.BadArgument)]
    [InlineData("RUD,NaN", ParseError.BadArgument)]
    [InlineData("RUD,40", ParseError.OutOfRange)]
    [InlineData("RUD,-36.5", ParseError.OutOfRange)]
    [InlineData("TEL,WARP", ParseError.BadArgument)]
    [InlineData("RPM,1500", ParseError.OutOfRange)]
    [InlineData("THR,150", ParseError.OutOfRange)]
    [InlineData("BTN,FIRE,1", ParseError.BadArgument)]
    [InlineData("BTN,ACK,2", ParseError.BadArgument)]
    [InlineData("BTN,ACK", ParseError.BadArgument)]
    [InlineData("NFU,X", ParseError.BadArgument)]
    public void BadLinesAreRejectedWithReason(string line, ParseError expected)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.False(r.Ok);
        Assert.Equal(expected, r.Error);
        Assert.NotEmpty(r.Detail);
    }

    [Theory]
    [InlineData("RUD,10", 10.0)]
    [InlineData("rud, -12.5 ", -12.5)]
    [InlineData("RUD,35.8", 35.0)]     // ±35–36 夾到 ±35
    [InlineData("RUD,-36", -35.0)]
    [InlineData("RUD,0\r", 0.0)]
    public void Rudder_ParsesAndClamps(string line, double expected)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(ConsoleMessageKind.Rudder, r.Message!.Kind);
        Assert.Equal(expected, r.Message.Value, 6);
    }

    [Theory]
    [InlineData("TEL,HAH", "HAH")]
    [InlineData("tel,stop", "STOP")]
    [InlineData("TEL,EFAS", "EFAS")]
    [InlineData("TEL,NAVF", "NAVF")]
    [InlineData("TEL,DSAS", "DSAS")]
    public void Telegraph_AcceptsStateSchemaEnum(string line, string expected)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(ConsoleMessageKind.Telegraph, r.Message!.Kind);
        Assert.Equal(expected, r.Message.Text);
    }

    [Fact]
    public void Rpm_Thruster_Button_Nfu_Heartbeat()
    {
        var p = new ProtocolParser();
        var rpm = p.Parse("RPM,-120.5").Message!;
        Assert.Equal(ConsoleMessageKind.Rpm, rpm.Kind);
        Assert.Equal(-120.5, rpm.Value);
        var thr = p.Parse("THR,100.5").Message!;
        Assert.Equal(ConsoleMessageKind.Thruster, thr.Kind);
        Assert.Equal(100.0, thr.Value);
        var btn = p.Parse("BTN,ack,1").Message!;
        Assert.Equal(ConsoleMessageKind.Button, btn.Kind);
        Assert.Equal("ACK", btn.Name);
        Assert.True(btn.Pressed);
        Assert.False(p.Parse("BTN,EMERG,0").Message!.Pressed);
        var nfu = p.Parse("NFU,l").Message!;
        Assert.Equal(ConsoleMessageKind.Nfu, nfu.Kind);
        Assert.Equal("L", nfu.Text);
        Assert.Equal(ConsoleMessageKind.Heartbeat, p.Parse("HB*0A").Message!.Kind);
        Assert.Equal(ConsoleMessageKind.Heartbeat, p.Parse("hb").Message!.Kind);
    }

    [Fact]
    public void Hello_WithoutCapsStillAccepted()
    {
        var r = new ProtocolParser().Parse("HELLO");
        Assert.True(r.Ok);
        Assert.Empty(r.Message!.Caps);
        Assert.Equal("", r.Message.Text);
    }
}
