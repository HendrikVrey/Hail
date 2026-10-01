using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hail.Core.Ports;
using Hail.Core.Updates;

namespace Hail.Persistence;

/// <summary>
/// Reads and writes <c>update.json</c>: when GitHub was last asked, which version the user
/// skipped, and when Hail asked whether it may check. Nothing here throws and nothing is
/// reported; the worst a lost file costs is one extra check or one question asked again.
/// </summary>
public sealed class UpdateStateStore(HailPaths paths) : IUpdateStateStore
{
    private const long MaxBytes = 16L * 1024;

    /// <summary>One write at a time: a check's save and a Skip can land together.</summary>
    private readonly Lock _gate = new();

    public UpdateState Load()
    {
        try
        {
            var info = new FileInfo(paths.UpdateState);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return UpdateState.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(info.FullName));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateState.Empty;
            }

            var skipped = root.TryGetProperty("skippedVersion", out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

            return new UpdateState(
                Time(root, "lastCheckedUtc"),
                skipped is { Length: > 0 and <= 64 } ? skipped : null,
                Time(root, "askedUtc"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdateState.Empty;
        }
    }

    public bool Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = new JsonObject();
        if (state.LastCheckedUtc is { } lastChecked)
        {
            document["lastCheckedUtc"] = Format(lastChecked);
        }

        if (state.SkippedVersion is { Length: > 0 and <= 64 } skipped)
        {
            document["skippedVersion"] = skipped;
        }

        if (state.AskedUtc is { } asked)
        {
            document["askedUtc"] = Format(asked);
        }

        lock (_gate)
        {
            try
            {
                AtomicFile.WriteAllText(paths.UpdateState, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
