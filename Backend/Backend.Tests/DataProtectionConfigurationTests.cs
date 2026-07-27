using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rankoon.Data.Diagnostics;
using Xunit;

namespace Backend.Tests;

public sealed class DataProtectionConfigurationTests
{
    [Fact]
    public void MissingKeyRingIsAllowedOnlyInDevelopment()
    {
        Assert.Null(DataProtectionOptions.Validate(new(), Environment("Development")));
        Assert.NotNull(DataProtectionOptions.Validate(new(), Environment("Production")));
    }

    [Fact]
    public void ProductionRejectsKeyRingInsideApplicationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.NotNull(DataProtectionOptions.Validate(new() { KeyRingPath = "keys" }, Environment("Production", root)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WritableProductionKeyRingIsAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var keyRing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(DataProtectionOptions.Validate(new() { KeyRingPath = keyRing }, Environment("Production", root)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(keyRing)) Directory.Delete(keyRing, true);
        }
    }

    private static TestHostEnvironment Environment(string name, string? root = null) => new()
    {
        EnvironmentName = name,
        ContentRootPath = root ?? Path.GetTempPath()
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Rankoon.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
