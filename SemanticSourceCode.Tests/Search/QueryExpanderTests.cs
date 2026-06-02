using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class QueryExpanderTests
{
    [Fact]
    public void Expand_EmptyQuery_ReturnsEmpty()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Expand_WhitespaceQuery_ReturnsWhitespace()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("   ");
        Assert.Equal("   ", result);
    }

    [Fact]
    public void Expand_UnknownWord_ReturnsUnchanged()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("unknown term");
        Assert.Equal("unknown term", result);
    }

    [Fact]
    public void Expand_Db_Should_Expand_To_Database_Terms()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("db connection");

        Assert.NotNull(result);
        Assert.Contains("database", result);
        Assert.Contains("db", result);
        Assert.Contains("sql", result);
    }

    [Fact]
    public void Expand_Http_Should_Expand_To_Web_Terms()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("http api");

        Assert.NotNull(result);
        Assert.Contains("web", result);
        Assert.Contains("api", result);
        Assert.Contains("rest", result);
    }

    [Fact]
    public void Expand_Async_Should_Expand_To_Task_Terms()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("async task");

        Assert.NotNull(result);
        Assert.Contains("asynchronous", result);
        Assert.Contains("task", result);
    }

    [Fact]
    public void Expand_Sensor_Should_Expand_To_Specific_Sensor_Terms()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("sensor distance");

        Assert.NotNull(result);
        Assert.Contains("ultrasonic", result);
        Assert.Contains("distance", result);
    }

    [Fact]
    public void Expand_File_Should_Expand_To_IO_Terms()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("file read");

        Assert.NotNull(result);
        Assert.Contains("io", result);
        Assert.Contains("read", result);
    }

    [Fact]
    public void Expand_CaseInsensitive_MatchesMixedCase()
    {
        var expander = new QueryExpander();
        var result = expander.Expand("DB Connection");

        Assert.Contains("database", result);
        Assert.Contains("db", result);
    }

    [Fact]
    public void Expand_WithCustomConfig_OverridesDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueryExpansion:db"] = "mysql,postgres"
            })
            .Build();

        var expander = new QueryExpander(config);
        var result = expander.Expand("db");

        Assert.Contains("mysql", result);
        Assert.Contains("postgres", result);
        // "database" should NOT be present because config overrides the default synonyms
        Assert.DoesNotContain("database", result);
    }

    [Fact]
    public void Expand_WithPartialCustomConfig_MergesWithDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueryExpansion:custom"] = "value1,value2"
            })
            .Build();

        var expander = new QueryExpander(config);

        var customResult = expander.Expand("custom");
        Assert.Contains("value1", customResult);
        Assert.Contains("value2", customResult);

        // Default still works
        var dbResult = expander.Expand("db");
        Assert.Contains("database", dbResult);
    }
}
