using System.Globalization;

namespace FieldTrack.Simulator;

/// <summary>The <c>--name value</c> pairs after a command, with the environment as a fallback for secrets.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values) => _values = values;

    /// <summary>Reads <paramref name="args"/> as <c>--name value</c> pairs.</summary>
    /// <exception cref="SimulatorException">An argument that is not a <c>--name</c> with a value.</exception>
    public static Options Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new SimulatorException($"Expected '--name value', got '{args[i]}'.");
            }

            values[args[i][2..]] = args[i + 1];
        }

        return new Options(values);
    }

    /// <summary>The value of <paramref name="name"/>, or null when it was not given.</summary>
    public string? Get(string name) => _values.GetValueOrDefault(name);

    /// <summary>The numeric value of <paramref name="name"/>, or null when it was not given.</summary>
    /// <exception cref="SimulatorException">The value is not a number.</exception>
    public double? GetNumber(string name) =>
        Get(name) is not { } text ? null
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
        : throw new SimulatorException($"--{name} must be a number, got '{text}'.");

    /// <summary>The value of <paramref name="name"/>.</summary>
    /// <exception cref="SimulatorException">The option was not given.</exception>
    public string Require(string name) => Get(name) ?? throw new SimulatorException($"Missing --{name}.");
}
