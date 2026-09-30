# Hail.Sdk

The plugin contract for [Hail](https://github.com/HendrikVrey/Hail), a keyboard launcher for
Windows: press Alt+Space, type, press Enter.

A plugin is a .NET class library with one class that implements `IProvider`. Hail asks it for
results as the user types, ranks them with everyone else's, and runs the one the user picks.

```xml
<PackageReference Include="Hail.Sdk" Version="1.0.0" ExcludeAssets="runtime" />
```

`ExcludeAssets="runtime"` keeps `Hail.Sdk.dll` out of your plugin's folder: Hail supplies its
own copy when it loads you, so the types you return are the types it reads.

```csharp
public sealed class HelloProvider : IProvider
{
    private IPluginContext? _context;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Result> QueryAsync(Query query, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = $"Hello, {query.Search}";
        yield return new Result(
            Id: "hello",
            Title: text,
            Subtitle: "Enter copies it",
            Icon: IconSource.ForGlyph("\uE8BD"),
            Relevance: 0.9,
            Primary: new ResultAction("Copy", Gesture.Enter, async (_, token) =>
            {
                await _context!.Clipboard.SetTextAsync(text, token);
                return ActionOutcome.Hide;
            }),
            Secondary: [],
            Highlight: null);
        await Task.CompletedTask;
    }
}
```

Beside the assembly goes a `plugin.json` that names it, its publisher, its keywords and its
settings; Hail reads that before it loads any code. The full walk-through, from an empty folder
to a result on screen, is `docs/plugins.md` in Hail's repository, and `dotnet new hail-plugin`
gives you all of it ready to build.

Versioning: 1.x only adds. A plugin built against 1.0 loads in every Hail that speaks 1.x.

MIT licensed.
