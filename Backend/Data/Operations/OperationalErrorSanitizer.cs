using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rankoon.Data.Operations;

public static partial class OperationalErrorSanitizer
{
    public const string Redacted = "[REDACTED]";
    public const int MaxMessageLength = 2_000;
    public const int MaxStackTraceLength = 16_000;
    public const int MaxContextValueLength = 1_000;

    private static readonly string[] SensitiveKeyParts =
    [
        "authorization", "cookie", "credential", "password", "passwd", "secret", "signature", "token", "api-key", "apikey", "connectionstring"
    ];
    private static readonly HashSet<string> AllowedContextKeys = new(StringComparer.Ordinal)
    {
        "cause", "channelId", "eventId", "hubId", "identityId", "method", "shardId", "state", "userId", "worker"
    };

    public static string Redact(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var redacted = MongoCredentialRegex().Replace(value, "$1" + Redacted + "@");
        redacted = AuthorizationRegex().Replace(redacted, "$1" + Redacted);
        redacted = CookieRegex().Replace(redacted, "$1" + Redacted);
        redacted = KeyValueSecretRegex().Replace(redacted, "$1" + Redacted);
        redacted = QuerySecretRegex().Replace(redacted, "$1" + Redacted);
        redacted = JwtRegex().Replace(redacted, Redacted);
        redacted = DiscordTokenRegex().Replace(redacted, Redacted);
        return redacted[..Math.Min(redacted.Length, maxLength)];
    }

    public static Dictionary<string, string> RedactContext(IReadOnlyDictionary<string, object?>? context, int maxEntries = 20)
    {
        if (context is null) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in context)
        {
            if (result.Count >= Math.Clamp(maxEntries, 0, 50)) break;
            if (!AllowedContextKeys.Contains(key)) continue;
            var normalizedKey = Redact(key, 80);
            if (normalizedKey.Length == 0 || value is null) continue;
            result[normalizedKey] = IsSensitiveKey(key)
                ? Redacted
                : Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), MaxContextValueLength);
        }
        return result;
    }

    public static string CreateFingerprint(string exceptionType, string? message, string? source = null, string? operation = null)
    {
        var material = string.Join('|', NormalizeFingerprintPart(exceptionType), NormalizeFingerprintPart(source),
            NormalizeFingerprintPart(operation), NormalizeFingerprintPart(Redact(message, MaxMessageLength)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static string NormalizeFingerprintPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant();
        normalized = GuidRegex().Replace(normalized, "<id>");
        normalized = ObjectIdRegex().Replace(normalized, "<id>");
        normalized = NumberRegex().Replace(normalized, "<n>");
        normalized = WhitespaceRegex().Replace(normalized, " ");
        return normalized[..Math.Min(normalized.Length, 2_000)];
    }

    private static bool IsSensitiveKey(string key) => SensitiveKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"((?:mongodb(?:\+srv)?):\/\/)[^\s\/@:]+(?::[^\s\/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MongoCredentialRegex();
    [GeneratedRegex(@"(?i)\b(authorization\s*[:=]\s*(?:bearer|basic)\s+)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();
    [GeneratedRegex(@"(?im)\b((?:set-)?cookie\s*:\s*)[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CookieRegex();
    [GeneratedRegex(@"(?i)(\b(?:password|passwd|secret|token|api[_-]?key|client_secret|connectionstring)\s*[:=]\s*)('(?:[^']*)'|""(?:[^""]*)""|[^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();
    [GeneratedRegex(@"(?i)([?&](?:access_token|auth|code|key|password|secret|signature|token)=)[^&#\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();
    [GeneratedRegex(@"\b(?:mfa\.[A-Za-z0-9_-]{20,}|[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DiscordTokenRegex();
    [GeneratedRegex(@"\b[0-9a-f]{24}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdRegex();
    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
    [GeneratedRegex(@"(?<![a-z])\d+(?:\.\d+)?(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
