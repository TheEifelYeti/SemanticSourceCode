using Xunit;
using System.IO;
using System.Reflection;
using SemanticSourceCode;

namespace SemanticSourceCode.Tests.Services;

public class QueryExpansionTests
{
    [Fact]
    public void ExpandQuery_Db_Should_Expand_To_Database_Terms()
    {
        // Arrange
        var query = "db connection";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("database", result);
        Assert.Contains("db", result); // Original sollte erhalten bleiben
    }
    
    [Fact]
    public void ExpandQuery_Http_Should_Expand_To_Web_Terms()
    {
        // Arrange
        var query = "http api";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("web", result);
        Assert.Contains("api", result);
    }
    
    [Fact]
    public void ExpandQuery_Async_Should_Expand_To_Task_Terms()
    {
        // Arrange
        var query = "async task";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("asynchronous", result);
        Assert.Contains("task", result);
    }
    
    [Fact]
    public void ExpandQuery_Sensor_Should_Expand_To_Specific_Sensor_Terms()
    {
        // Arrange
        var query = "sensor distance";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("ultrasonic", result);
        Assert.Contains("distance", result);
    }
    
    [Fact]
    public void ExpandQuery_File_Should_Expand_To_IO_Terms()
    {
        // Arrange
        var query = "file read";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("io", result);
        Assert.Contains("read", result);
    }
    
    [Fact]
    public void ExpandQuery_No_Matching_Terms_Should_Return_Original()
    {
        // Arrange
        var query = "unknown term";
        
        // Act
        var result = Program.ExpandQuery(query);
        
        // Assert
        Assert.Equal("unknown term", result);
    }
}