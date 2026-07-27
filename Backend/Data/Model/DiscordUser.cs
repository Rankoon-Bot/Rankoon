using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

/// <summary>
/// Represents a Discord user in our system
/// </summary>
public class DiscordUser
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Discord user ID (snowflake)
    /// </summary>
    [BsonElement("discord_id")]
    public string DiscordId { get; set; } = string.Empty;

    /// <summary>
    /// Discord username
    /// </summary>
    [BsonElement("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Discord discriminator (legacy, may be null for new users)
    /// </summary>
    [BsonElement("discriminator")]
    public string? Discriminator { get; set; }

    /// <summary>
    /// Discord display name
    /// </summary>
    [BsonElement("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// User's email address
    /// </summary>
    [BsonElement("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Avatar hash from Discord
    /// </summary>
    [BsonElement("avatar")]
    public string? Avatar { get; set; }

    /// <summary>
    /// Whether the user's email is verified on Discord
    /// </summary>
    [BsonElement("verified")]
    public bool Verified { get; set; }

    /// <summary>
    /// Protected Discord access token. Plaintext values are accepted only while the
    /// one-time migration is in progress.
    /// </summary>
    [BsonElement("access_token")]
    public string? ProtectedAccessToken { get; set; }

    /// <summary>
    /// Protected Discord refresh token. Plaintext values are accepted only while the
    /// one-time migration is in progress.
    /// </summary>
    [BsonElement("refresh_token")]
    public string? ProtectedRefreshToken { get; set; }

    /// <summary>
    /// Version of the data-protection format applied to the OAuth token fields.
    /// </summary>
    [BsonElement("oauth_token_protection_version")]
    public int? OAuthTokenProtectionVersion { get; set; }

    /// <summary>
    /// When the Discord token expires
    /// </summary>
    [BsonElement("token_expires_at")]
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>
    /// When this user was first created in our system
    /// </summary>
    [BsonElement("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this user was last updated
    /// </summary>
    [BsonElement("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the user last logged in
    /// </summary>
    [BsonElement("last_login")]
    public DateTime? LastLogin { get; set; }
}
