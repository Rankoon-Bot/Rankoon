using System.Text.RegularExpressions;

namespace Rankoon.Data.Utils;

/// <summary>
/// Utility for expanding environment variables in configuration
/// </summary>
public static class ConfigurationHelper
{
    /// <summary>
    /// Expand environment variables in configuration values
    /// </summary>
    public static void ExpandEnvironmentVariables(IConfiguration configuration)
    {
        var regex = new Regex(@"\$\{([^}]+)\}", RegexOptions.Compiled);
        
        ExpandSection(configuration, regex);
    }

    private static void ExpandSection(IConfiguration configuration, Regex regex)
    {
        foreach (var section in configuration.GetChildren())
        {
            if (section.Value != null)
            {
                var expandedValue = regex.Replace(section.Value, match =>
                {
                    var envVarName = match.Groups[1].Value;
                    return Environment.GetEnvironmentVariable(envVarName) ?? match.Value;
                });
                
                section.Value = expandedValue;
            }
            else
            {
                ExpandSection(section, regex);
            }
        }
    }
}
