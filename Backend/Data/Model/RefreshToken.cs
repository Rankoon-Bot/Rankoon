using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rankoon.Data.Model;

/// <summary>
/// Represents a refresh token for our JWT system
/// </summary>
public class RefreshToken
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// The refresh token value
    /// </summary>
    [BsonElement("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// User ID this token belongs to
    /// </summary>
    [BsonElement("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// When this token was created
    /// </summary>
    [BsonElement("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this token expires
    /// </summary>
    [BsonElement("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this token has been revoked
    /// </summary>
    [BsonElement("revoked")]
    public bool Revoked { get; set; } = false;

    /// <summary>
    /// When this token was revoked (if applicable)
    /// </summary>
    [BsonElement("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Why this token was revoked (if applicable)
    /// </summary>
    [BsonElement("revoked_reason")]
    public string? RevokedReason { get; set; }

    /// <summary>
    /// The IP address this token was issued from
    /// </summary>
    [BsonElement("issued_ip")]
    public string? IssuedIp { get; set; }

    /// <summary>
    /// The IP address this token was last used from
    /// </summary>
    [BsonElement("last_used_ip")]
    public string? LastUsedIp { get; set; }

    /// <summary>
    /// When this token was last used
    /// </summary>
    [BsonElement("last_used_at")]
    public DateTime? LastUsedAt { get; set; }
}
