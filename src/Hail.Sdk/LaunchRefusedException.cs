namespace Hail.Sdk;

/// <summary>
/// Something the host would not start, open or copy (Hail.md §9). The message is a sentence
/// written for the user, and the host shows it in the box as it is.
/// </summary>
public sealed class LaunchRefusedException : InvalidOperationException
{
    /// <summary>A refusal with a general sentence.</summary>
    public LaunchRefusedException()
        : base("Hail will not open that.")
    {
    }

    /// <summary>A refusal the user reads as <paramref name="message"/>.</summary>
    public LaunchRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>A refusal the user reads as <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public LaunchRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
