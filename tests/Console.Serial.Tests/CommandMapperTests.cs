using SimosaBRM.ConsoleSerial.Core;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>每種裝置行 → 正確的 SimCommand JSON(command.schema.json;console-serial.md 第 2 節右欄)。</summary>
public class CommandMapperTests
{
    private static string Json(string line)
    {
        var r = new ProtocolParser().Parse(line);
        Assert.True(r.Ok, r.Detail);
        var cmds = CommandMapper.Map(r.Message!);
        return string.Join("|", cmds.Select(c => c.ToJson()));
    }

    [Theory]
    [InlineData("RUD,10", "{\"type\":\"rudder\",\"value\":10}")]
    [InlineData("RUD,-12.5", "{\"type\":\"rudder\",\"value\":-12.5}")]
    [InlineData("TEL,HAH", "{\"type\":\"telegraph\",\"value\":\"HAH\"}")]
    [InlineData("TEL,efas", "{\"type\":\"telegraph\",\"value\":\"EFAS\"}")]
    [InlineData("RPM,85", "{\"type\":\"rpm\",\"value\":85}")]
    [InlineData("THR,50", "{\"type\":\"thruster\",\"value\":0.5}")]
    [InlineData("THR,-100", "{\"type\":\"thruster\",\"value\":-1}")]
    [InlineData("THR,33.33", "{\"type\":\"thruster\",\"value\":0.3333}")]
    [InlineData("BTN,ACK,1", "{\"type\":\"alertAcknowledge\"}")]
    [InlineData("BTN,SILENCE,1", "{\"type\":\"alertSilence\"}")]
    [InlineData("BTN,FU,1", "{\"type\":\"steeringMode\",\"value\":\"fu\"}")]
    [InlineData("BTN,NFU,1", "{\"type\":\"steeringMode\",\"value\":\"nfu\",\"args\":{\"direction\":\"stop\"}}")]
    [InlineData("BTN,EMERG,1", "{\"type\":\"steeringMode\",\"value\":\"emergency\"}")]
    [InlineData("BTN,BNWAS,1", "{\"type\":\"bnwasReset\"}")]
    [InlineData("NFU,L", "{\"type\":\"steeringMode\",\"value\":\"nfu\",\"args\":{\"direction\":\"port\"}}")]
    [InlineData("NFU,R", "{\"type\":\"steeringMode\",\"value\":\"nfu\",\"args\":{\"direction\":\"starboard\"}}")]
    [InlineData("NFU,0", "{\"type\":\"steeringMode\",\"value\":\"nfu\",\"args\":{\"direction\":\"stop\"}}")]
    public void LineMapsToCommandJson(string line, string expectedJson)
    {
        Assert.Equal(expectedJson, Json(line));
    }

    [Theory]
    [InlineData("BTN,ACK,0")]
    [InlineData("BTN,NFU,0")]
    [InlineData("HB")]
    [InlineData("HELLO,1.0.0,RUD")]
    public void ReleaseHeartbeatAndHelloProduceNoCommand(string line)
    {
        Assert.Equal("", Json(line));
    }

    [Fact]
    public void CommandTypesAreInCommandSchemaEnum()
    {
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Contracts", "command.schema.json")));
        var allowed = doc.RootElement.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var line in new[] { "RUD,1", "TEL,HAH", "RPM,1", "THR,1", "BTN,ACK,1", "BTN,SILENCE,1", "BTN,FU,1", "BTN,NFU,1", "BTN,EMERG,1", "BTN,BNWAS,1", "NFU,L" })
        {
            foreach (var cmd in CommandMapper.Map(new ProtocolParser().Parse(line).Message!))
            {
                using var j = System.Text.Json.JsonDocument.Parse(cmd.ToJson());
                Assert.Contains(j.RootElement.GetProperty("type").GetString(), allowed);
            }
        }
    }
}
