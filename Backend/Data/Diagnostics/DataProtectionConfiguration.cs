using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Rankoon.Data.Diagnostics;

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    public string? KeyRingPath { get; init; }

    public static string? Validate(DataProtectionOptions options, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(options.KeyRingPath))
            return environment.IsProduction() ? "DataProtection:KeyRingPath must be configured in Production." : null;

        var keyRingPath = ResolveKeyRingPath(options, environment)!;
        if (environment.IsProduction() && IsWithin(keyRingPath, environment.ContentRootPath))
            return "DataProtection:KeyRingPath must be outside the application image/content directory.";

        try
        {
            Directory.CreateDirectory(keyRingPath);
            var probePath = Path.Combine(keyRingPath, $".rankoon-write-probe-{Guid.NewGuid():N}");
            using (File.Open(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(probePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "DataProtection:KeyRingPath must reference a readable and writable directory.";
        }

        return null;
    }

    public static string? ResolveKeyRingPath(DataProtectionOptions options, IHostEnvironment environment) =>
        string.IsNullOrWhiteSpace(options.KeyRingPath) ? null : Path.GetFullPath(options.KeyRingPath, environment.ContentRootPath);

    private static bool IsWithin(string candidate, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var relative = Path.GetRelativePath(normalizedParent, candidate);
        return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar) && relative != ".." && !Path.IsPathRooted(relative));
    }
}

public static class DataProtectionConfiguration
{
    public static void AddRankoonDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<DataProtectionOptions>()
            .Bind(configuration.GetSection(DataProtectionOptions.SectionName))
            .Validate(options => DataProtectionOptions.Validate(options, environment) is null, "Data Protection key-ring configuration is invalid.")
            .ValidateOnStart();

        var options = configuration.GetSection(DataProtectionOptions.SectionName).Get<DataProtectionOptions>() ?? new();
        var keyRingPath = DataProtectionOptions.ResolveKeyRingPath(options, environment);
        var dataProtection = services.AddDataProtection().SetApplicationName("Rankoon");
        if (keyRingPath != null)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
    }
}
