namespace Hail.Sdk;

/// <summary>
/// Something the host would not start, open or copy (Hail.md §9). The message is a sentence
/// written for the user, and the host shows it in the box as it is.
/// </summary>
public sealed class LaunchRefusedException : InvalidOperationException
{
    public LaunchRefusedException()
        : base("Hail will not open that.")
    {
    }

    public LaunchRefusedException(string message)
        : base(message)
    {
    }

    public LaunchRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
