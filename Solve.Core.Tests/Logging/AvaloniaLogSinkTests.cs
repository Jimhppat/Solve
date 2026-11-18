using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Logging;
using AvaloniaLevel = Avalonia.Logging.LogEventLevel;

namespace Solve.Core.Tests.Logging;

[Collection(LoggingPipelineCollection.Name)]
public sealed class AvaloniaLogSinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly LoggingPipeline _pipeline;
    private readonly AvaloniaLogSink _sink;

    public AvaloniaLogSinkTests()
    {
        _pipeline = LoggingPipeline.Create(_directory, settingsPath: null);
        _sink = new AvaloniaLogSink(_pipeline.Logger);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string DisposeAndRead()
    {
        _pipeline.Dispose();
        return LogFiles.ReadAll(_directory);
    }

    private sealed class TextBlockStub;

    [Fact]
    public void IsEnabled_FollowsAvaloniaDefaultOfWarning()
    {
        Assert.False(_sink.IsEnabled(AvaloniaLevel.Verbose, "Layout"));
        Assert.False(_sink.IsEnabled(AvaloniaLevel.Information, "Binding"));
        Assert.True(_sink.IsEnabled(AvaloniaLevel.Warning, "Binding"));
        Assert.True(_sink.IsEnabled(AvaloniaLevel.Fatal, "Platform"));
    }

    [Fact]
    public void Log_WritesAreaAsSourceContext_AndPrefixesSourceType()
    {
        _sink.Log(AvaloniaLevel.Warning, "Binding", new TextBlockStub(),
            "Error in binding to {Target}.{Property}: {Message}", "TextBlock", "Text", "Could not find property");
        var log = DisposeAndRead();

        Assert.Contains("[WRN]", log);
        Assert.Contains("Avalonia.Binding: [TextBlockStub] Error in binding to TextBlock.Text: Could not find property", log);
    }

    [Fact]
    public void Log_WithoutSourceOrValues_WritesTemplateAsIs()
    {
        _sink.Log(AvaloniaLevel.Error, "Platform", null, "Window handle lost");
        var log = DisposeAndRead();

        Assert.Contains("[ERR]", log);
        Assert.Contains("Avalonia.Platform: Window handle lost", log);
    }

    [Fact]
    public void Log_BelowLevel_IsDropped()
    {
        _sink.Log(AvaloniaLevel.Debug, "Layout", null, "measure-noise");
        var log = DisposeAndRead();

        Assert.DoesNotContain("measure-noise", log);
    }
}
