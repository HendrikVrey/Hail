namespace Hail.Core.Ports;

/// <summary>Hail's own log. <c>Hail.Persistence</c> writes it to disk.</summary>
/// <remarks>
/// Nothing the user typed is ever written here (Hail.md §9): a query is logged by its length,
/// never its text. Every caller is held to that by convention and by review, because no type
/// can tell a query from any other string.
/// </remarks>
public interface IHostLog
{
    void LogInfo(string message);

    void LogError(string message, Exception? exception = null);
}
