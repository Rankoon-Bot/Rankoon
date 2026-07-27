using MongoDB.Bson;
using Rankoon.Data.MongoDb;
using Xunit;

namespace Backend.Tests;

public sealed class AuthDataIntegrityInitializerTests
{
    [Fact]
    public void Auth_indexes_have_stable_names_for_health_and_operations()
    {
        Assert.Equal("discord_id_unique", AuthDataIntegrityInitializer.DiscordIdIndexName);
        Assert.Equal("refresh_token_hash_unique", AuthDataIntegrityInitializer.TokenHashIndexName);
        Assert.Equal("refresh_family", AuthDataIntegrityInitializer.FamilyIndexName);
        Assert.Equal("refresh_user", AuthDataIntegrityInitializer.UserIndexName);
        Assert.Equal("refresh_expires_ttl", AuthDataIntegrityInitializer.ExpiresIndexName);
    }

    [Fact]
    public void Refresh_token_hash_is_stable_SHA256_hex_without_retaining_the_value()
    {
        var hash = AuthDataIntegrityInitializer.HashRefreshToken("test-value");

        Assert.Equal(64, hash.Length);
        Assert.NotEqual("test-value", hash);
        Assert.Equal(hash, AuthDataIntegrityInitializer.HashRefreshToken("test-value"));
    }

    [Fact]
    public void Partial_index_filter_excludes_missing_and_empty_values()
    {
        var filter = AuthDataIntegrityInitializer.NonEmptyStringFilter("token_hash");

        Assert.Equal(new BsonDocument("token_hash", new BsonDocument("$gt", string.Empty)), filter);
    }
}
