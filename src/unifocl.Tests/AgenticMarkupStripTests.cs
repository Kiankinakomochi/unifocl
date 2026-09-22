using Spectre.Console;
using Xunit;

/// <summary>
/// Agentic logs are the stream log with Spectre markup stripped. Escaped brackets are content, not
/// tags; custom-tool results render JSON arrays through Markup.Escape and must survive intact.
/// </summary>
public class AgenticMarkupStripTests
{
    [Fact]
    public void EscapedJsonArray_KeepsItsBrackets()
    {
        var line = $"[grey]{Markup.Escape("\"faces\": [4, 5],")}[/]";

        Assert.Equal("\"faces\": [4, 5],", AgenticFormatter.StripMarkup(line));
    }

    [Fact]
    public void EscapedEmptyArray_KeepsItsBrackets()
    {
        var line = $"[grey]{Markup.Escape("\"faces\": [],")}[/]";

        Assert.Equal("\"faces\": [],", AgenticFormatter.StripMarkup(line));
    }

    [Fact]
    public void MarkupTags_AreStillRemoved()
    {
        Assert.Equal("error: probuilder: target not found", AgenticFormatter.StripMarkup("[red]error[/]: probuilder: target not found"));
        Assert.Equal("usage: /probuilder", AgenticFormatter.StripMarkup("[x] usage: /probuilder"));
    }
}
