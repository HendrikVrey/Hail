using System.Globalization;
using System.Text;

namespace Hail.Core.Hosting;

/// <summary>
/// An exception as the log may record it: its type, its HResult and where it was thrown, and
/// never its message (Hail.md §9).
/// </summary>
/// <remarks>
/// A message can carry what the user typed: .NET's own failure to start a process names the
/// target, so a failed web search would log the address with the search in it, and a plugin's
/// exception can say anything. Types and stack frames are the code's, not the user's.
/// </remarks>
public static class Redaction
{
    /// <summary>Inner exceptions followed at most this deep.</summary>
    private const int MaxDepth = 4;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var text = new StringBuilder();
        var current = exception;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++, current = current.InnerException)
        {
            if (depth > 0)
            {
                text.Append(" <- ");
            }

            text.Append(current.GetType().FullName)
                .Append(CultureInfo.InvariantCulture, $" (0x{current.HResult:X8})");
        }

        if (exception.StackTrace is { Length: > 0 } stack)
        {
            text.AppendLine().Append(stack);
        }

        return text.ToString();
    }
}
