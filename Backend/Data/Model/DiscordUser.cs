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
    /// Discord access token
    /// </summary>
    [BsonElement("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>
    /// Discord refresh token
    /// </summary>
    [BsonElement("refresh_token")]
    public string? RefreshToken { get; set; }

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
