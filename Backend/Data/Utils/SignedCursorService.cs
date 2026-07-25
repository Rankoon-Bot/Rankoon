using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;

namespace Rankoon.Data.Utils;

public sealed record SignedCursor(DateTimeOffset Timestamp, string Id, string? SortValue = null);

public interface ISignedCursorService
{
    string Create(DateTimeOffset timestamp, string id, string binding, string? sortValue = null);
    bool TryRead(string? token, string binding, out SignedCursor cursor);
}

public sealed class SignedCursorService(IOptions<JwtSettings> options) : ISignedCursorService
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.SecretKey);

    public string Create(DateTimeOffset timestamp, string id, string binding, string? sortValue = null)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Payload(timestamp.ToUnixTimeMilliseconds(), id, Binding(binding), sortValue)));
        return payload + "." + Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)));
    }

    public bool TryRead(string? token, string binding, out SignedCursor cursor)
    {
        cursor = new(default, string.Empty);
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || !TryBase64(parts[1], out var supplied)) return false;
        var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied) || !TryBase64(parts[0], out var bytes)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(bytes);
            if (payload is null || payload.Binding != Binding(binding) || string.IsNullOrWhiteSpace(payload.Id)) return false;
            cursor = new(DateTimeOffset.FromUnixTimeMilliseconds(payload.Timestamp), payload.Id, payload.SortValue);
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static string Binding(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryBase64(string value, out byte[] result)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            result = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException) { result = []; return false; }
    }

    private sealed record Payload(long Timestamp, string Id, string Binding, string? SortValue = null);
}
