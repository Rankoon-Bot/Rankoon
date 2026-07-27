using System.Collections.Concurrent;

namespace Rankoon.Data.Auth;

public sealed record OAuthState(string Value, string? ReturnUrl);

public interface IOAuthStateStore
{
    void Store(string state, string? returnUrl, DateTimeOffset expiresAt);
    bool TryConsume(string state, out OAuthState? oauthState);
}

/// <summary>Process-local OAuth state store. A state is removed before it is returned.</summary>
public sealed class OAuthStateStore(TimeProvider timeProvider) : IOAuthStateStore
{
    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public void Store(string state, string? returnUrl, DateTimeOffset expiresAt)
    {
        RemoveExpiredEntries();
        entries[state] = new(new OAuthState(state, returnUrl), expiresAt);
    }

    public bool TryConsume(string state, out OAuthState? oauthState)
    {
        oauthState = null;
        if (!entries.TryRemove(state, out var entry) || entry.ExpiresAt <= timeProvider.GetUtcNow()) return false;

        oauthState = entry.State;
        return true;
    }

    private void RemoveExpiredEntries()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in entries)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                ((ICollection<KeyValuePair<string, Entry>>)entries).Remove(entry);
            }
        }
    }

    private sealed record Entry(OAuthState State, DateTimeOffset ExpiresAt);
}
