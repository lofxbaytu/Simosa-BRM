using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>
/// <see cref="ConsoleMessage"/> → 核心指令(command.schema.json;console-serial.md 第 2 節右欄)。
/// 純函數;去抖在 <see cref="ConsoleSession"/> 先做,這裡只負責對應與單位(側推百分比 → −1…1)。
/// </summary>
public static class CommandMapper
{
    private static readonly IReadOnlyList<SimCommand> None = Array.Empty<SimCommand>();

    public static IReadOnlyList<SimCommand> Map(ConsoleMessage m) => m.Kind switch
    {
        ConsoleMessageKind.Rudder => One(SimCommand.Rudder(m.Value)),
        ConsoleMessageKind.Telegraph => One(SimCommand.Telegraph(Enum.Parse<TelegraphOrder>(m.Text!, ignoreCase: true))),
        ConsoleMessageKind.Rpm => One(SimCommand.Rpm(m.Value)),
        ConsoleMessageKind.Thruster => One(SimCommand.Thruster(Math.Round(m.Value / 100.0, 4))),
        ConsoleMessageKind.Button => MapButton(m),
        ConsoleMessageKind.Nfu => One(SimCommand.SteeringMode(SteeringMode.Nfu, direction: m.Text switch { "L" => "port", "R" => "starboard", _ => "stop" })),
        _ => None,
    };

    /// <summary>只有按下(1)產生指令;放開不送。</summary>
    private static IReadOnlyList<SimCommand> MapButton(ConsoleMessage m)
    {
        if (!m.Pressed) return None;
        return m.Name switch
        {
            "ACK" => One(SimCommand.AlertAcknowledge()),
            "SILENCE" => One(SimCommand.AlertSilence()),
            "FU" => One(SimCommand.SteeringMode(SteeringMode.Fu)),
            "NFU" => One(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "stop")),
            "EMERG" => One(SimCommand.SteeringMode(SteeringMode.Emergency)),
            "BNWAS" => One(SimCommand.BnwasReset()),
            _ => None,
        };
    }

    private static IReadOnlyList<SimCommand> One(SimCommand c) => new[] { c };
}
