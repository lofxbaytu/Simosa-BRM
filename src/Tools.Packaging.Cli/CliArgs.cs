namespace SimosaBRM.Packaging.Cli;

/// <summary>簡易參數解析:<c>--name value</c>(可重複)、<c>--flag</c>(無值)、其餘為位置參數。</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _opts = new(StringComparer.Ordinal);

    public CliArgs(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var name = a[2..];
                string value = "true";
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = list[++i];
                }
                if (!_opts.TryGetValue(name, out var l)) _opts[name] = l = new List<string>();
                l.Add(value);
            }
            else Positional.Add(a);
        }
    }

    public List<string> Positional { get; } = new();

    public string? Get(string name) => _opts.TryGetValue(name, out var l) ? l[^1] : null;

    public string Require(string name) => Get(name) ?? throw new CliUsageException($"缺少 --{name}");

    public List<string> All(string name) => _opts.TryGetValue(name, out var l) ? l : new List<string>();

    public bool Flag(string name) => _opts.TryGetValue(name, out var l) && l[^1] is not ("false" or "0" or "no");

    public int Int(string name, int fallback) => Get(name) is { } s ? (int.TryParse(s, out var v) ? v : throw new CliUsageException($"--{name} 須為整數")) : fallback;

    public string Pos(int i, string what) => i < Positional.Count ? Positional[i] : throw new CliUsageException($"缺少{what}");
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}
