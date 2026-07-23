using Discord;
using MongoDB.Bson;
using Rankoon.Data.Model;

namespace Rankoon.Data.Discord;

/// <summary>Builds exactly the payload that is validated and sent to Discord.</summary>
public static class SelfRoleMessageRenderer
{
    public const int MaxEmbedsPerMessage = 10;
    public const int MaxEmbedTextLengthPerMessage = 6000;
    public const int MaxTitleLength = 256;
    public const int MaxDescriptionLength = 4096;
    public const int MaxFieldsPerEmbed = 25;
    public const int MaxFieldNameLength = 256;
    public const int MaxFieldValueLength = 1024;
    public const string DefaultColor = "#5865F2";

    public static void Normalize(SelfRolePanel panel)
    {
        panel.Mappings ??= [];
        if (panel.Embeds is null || panel.Embeds.Count == 0)
        {
            panel.Embeds = [new SelfRoleEmbed
            {
                Kind = SelfRoleEmbedKind.RoleMappings,
                Title = panel.Title ?? string.Empty,
                Description = panel.Description ?? string.Empty,
                Color = string.IsNullOrWhiteSpace(panel.Color) ? DefaultColor : panel.Color
            }];
        }

        foreach (var embed in panel.Embeds)
        {
            embed.Id ??= ObjectId.GenerateNewId().ToString();
            embed.Title ??= string.Empty;
            embed.Description ??= string.Empty;
            embed.Color = string.IsNullOrWhiteSpace(embed.Color) ? DefaultColor : embed.Color;
            embed.Fields ??= [];
            foreach (var field in embed.Fields)
            {
                field.Id ??= ObjectId.GenerateNewId().ToString();
                field.Name ??= string.Empty;
                field.Value ??= string.Empty;
            }
        }
    }

    public static string BuildLegend(SelfRolePanel panel) => string.Join('\n', panel.Mappings.Select(mapping => $"{ToEmote(mapping.Emoji)} <@&{mapping.RoleId}>"));

    public static string FinalDescription(SelfRolePanel panel, SelfRoleEmbed embed)
    {
        var description = (embed.Description ?? string.Empty).Trim();
        if (embed.Kind != SelfRoleEmbedKind.RoleMappings) return description;
        var legend = BuildLegend(panel);
        return string.IsNullOrWhiteSpace(description) ? legend : string.IsNullOrWhiteSpace(legend) ? description : $"{description}\n\n{legend}";
    }

    public static int TextLength(SelfRolePanel panel) => panel.Embeds.Sum(embed =>
        (embed.Title ?? string.Empty).Trim().Length + FinalDescription(panel, embed).Length + embed.Fields.Sum(field =>
            (field.Name ?? string.Empty).Trim().Length + (field.Value ?? string.Empty).Trim().Length));

    public static void ValidateStructure(SelfRolePanel panel)
    {
        if (panel.Embeds.Count == 0) throw new SelfRoleValidationException("selfRoles.invalidEmbedStructure");
        if (panel.Embeds.Count > MaxEmbedsPerMessage) throw new SelfRoleValidationException("selfRoles.tooManyEmbeds");
        if (panel.Embeds.Count(embed => embed.Kind == SelfRoleEmbedKind.RoleMappings) != 1) throw new SelfRoleValidationException("selfRoles.missingRoleMappingsEmbed");
        if (!panel.Embeds.Any(embed => !string.IsNullOrWhiteSpace(embed.Title) || !string.IsNullOrWhiteSpace(embed.Description) || embed.Fields.Any() || (embed.Kind == SelfRoleEmbedKind.RoleMappings && panel.Mappings.Any()))) throw new SelfRoleValidationException("selfRoles.invalidEmbedStructure");
        foreach (var embed in panel.Embeds)
        {
            if (!TryColor(embed.Color, out _)) throw new SelfRoleValidationException("selfRoles.invalidPanel");
            if (embed.Title.Trim().Length > MaxTitleLength) throw new SelfRoleValidationException("selfRoles.embedTitleTooLong");
            if (embed.Fields.Count > MaxFieldsPerEmbed) throw new SelfRoleValidationException("selfRoles.tooManyFields");
            if (FinalDescription(panel, embed).Length > MaxDescriptionLength) throw new SelfRoleValidationException("selfRoles.embedDescriptionTooLong");
            foreach (var field in embed.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name)) throw new SelfRoleValidationException("selfRoles.fieldNameRequired");
                if (string.IsNullOrWhiteSpace(field.Value)) throw new SelfRoleValidationException("selfRoles.fieldValueRequired");
                if (field.Name.Trim().Length > MaxFieldNameLength) throw new SelfRoleValidationException("selfRoles.fieldNameTooLong");
                if (field.Value.Trim().Length > MaxFieldValueLength) throw new SelfRoleValidationException("selfRoles.fieldValueTooLong");
            }
        }
        if (TextLength(panel) > MaxEmbedTextLengthPerMessage) throw new SelfRoleValidationException("selfRoles.embedTextTooLong");
    }

    public static Embed[] BuildEmbeds(SelfRolePanel panel) => panel.Embeds.Select(embed =>
    {
        TryColor(embed.Color, out var color);
        var builder = new EmbedBuilder().WithColor(color);
        var title = embed.Title.Trim();
        if (!string.IsNullOrEmpty(title)) builder.WithTitle(title);
        var description = FinalDescription(panel, embed);
        if (!string.IsNullOrEmpty(description)) builder.WithDescription(description);
        foreach (var field in embed.Fields) builder.AddField(field.Name.Trim(), field.Value.Trim(), field.Inline);
        return builder.Build();
    }).ToArray();

    public static bool TryColor(string value, out Color color)
    {
        color = Color.Default;
        var hex = (value ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        color = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static IEmote ToEmote(SelfRoleEmoji emoji)
    {
        if (emoji.Kind == SelfRoleEmojiKind.Unicode && !string.IsNullOrWhiteSpace(emoji.Value)) return new Emoji(emoji.Value);
        if (emoji.Kind == SelfRoleEmojiKind.Custom && ulong.TryParse(emoji.Value, out var id) && !string.IsNullOrWhiteSpace(emoji.Name)) return new Emote(id, emoji.Name, false);
        throw new ArgumentException("Invalid emoji.");
    }
}
