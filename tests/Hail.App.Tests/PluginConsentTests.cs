using Hail.Core.Plugins;

namespace Hail.App.Tests;

/// <summary>What the question before enabling a plugin says it will answer.</summary>
public sealed class PluginConsentTests
{
    [Theory]
    [InlineData(true, new string[0], "Every search")]
    [InlineData(true, new[] { "e" }, "Every search, and alone when one starts with \"e\"")]
    [InlineData(false, new[] { "e", "ev" }, "Only searches that start with \"e\" or \"ev\"")]
    [InlineData(false, new string[0], "Nothing: it declares no keyword and does not join every search")]
    public void The_question_says_which_searches_the_plugin_joins(bool global, string[] keywords, string said)
    {
        var manifest = new PluginManifest("t.p", "P", "Tests", "1", new SdkVersion(1, 0), "P.dll", "P.Provider", keywords, global, TimeSpan.Zero, null, []);

        Assert.Equal(said, PluginConsentWindow.DescribeAnswers(manifest));
    }
}
