namespace FieldTrack.Simulator;

/// <summary>
/// A problem the developer can fix (a bad fleet file, a refused sign-in, an API error). The command line
/// prints its message without a stack trace.
/// </summary>
internal sealed class SimulatorException : Exception
{
    public SimulatorException(string message)
        : base(message)
    {
    }

    public SimulatorException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
