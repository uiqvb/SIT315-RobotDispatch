namespace RobotLoadGen;

// "--name value" pairs from the command line.
public sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values) => _values = values;

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Expected --name before '{args[i]}'.");
            values[args[i][2..]] = args[i + 1];
        }

        return new Options(values);
    }

    public string Get(string name, string fallback) => _values.TryGetValue(name, out var value) ? value : fallback;

    public string Require(string name) => _values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

    public int Int(string name) => int.Parse(Require(name));
}
