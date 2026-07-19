using System.Runtime.CompilerServices;
using System.Text.Json;
using Rankoon.Api;
using Xunit;

namespace Backend.Tests;

public sealed class ApiErrorTranslationParityTests
{
    [Theory]
    [InlineData("en.json")]
    [InlineData("de.json")]
    public void Frontend_catalog_contains_every_backend_error_key(string localeFile)
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalogPath = Path.Combine(repositoryRoot, "Frontend", "src", "assets", "i18n", localeFile);
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var apiErrors = catalog.RootElement.GetProperty("apiErrors");
        var missingKeys = ApiErrorCatalog.All
            .Select(error => error.Key)
            .Where(key => !HasNonEmptyString(apiErrors, key.Split('.')))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingKeys.Length == 0,
            $"{localeFile} is missing translations: {string.Join(", ", missingKeys.Select(key => $"apiErrors.{key}"))}");
    }

    private static bool HasNonEmptyString(JsonElement current, IReadOnlyList<string> path)
    {
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return false;
        }

        return current.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(current.GetString());
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var startingDirectories = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(sourceFile),
            Directory.GetCurrentDirectory()
        };

        foreach (var startingDirectory in startingDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            for (var directory = new DirectoryInfo(startingDirectory!); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Backend", "Rankoon.csproj")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "Frontend", "src", "assets", "i18n")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing Backend and Frontend.");
    }
}
