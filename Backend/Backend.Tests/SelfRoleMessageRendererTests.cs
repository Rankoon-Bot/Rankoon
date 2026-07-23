using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class SelfRoleMessageRendererTests
{
    [Fact]
    public void Renders_single_embed_with_legend()
    {
        var panel = Panel("Title", "Choose");
        var embeds = SelfRoleMessageRenderer.BuildEmbeds(panel);
        Assert.Single(embeds);
        Assert.Equal("Title", embeds[0].Title);
        Assert.Equal("Choose\n\n✅ <@&42>", embeds[0].Description);
    }

    [Fact]
    public void Renders_embeds_and_fields_in_configured_order()
    {
        var panel = Panel();
        panel.Embeds.Insert(0, new SelfRoleEmbed { Kind = SelfRoleEmbedKind.Content, Title = "First", Fields = [new() { Name = "A", Value = "B", Inline = true }] });
        var embeds = SelfRoleMessageRenderer.BuildEmbeds(panel);
        Assert.Equal(["First", null], embeds.Select(embed => embed.Title));
        Assert.True(embeds[0].Fields.Single().Inline);
        Assert.Equal("A", embeds[0].Fields.Single().Name);
    }

    [Fact]
    public void Accepts_ten_embeds_and_25_fields()
    {
        var panel = Panel();
        panel.Embeds.AddRange(Enumerable.Range(0, 9).Select(i => new SelfRoleEmbed { Kind = SelfRoleEmbedKind.Content, Title = i.ToString() }));
        panel.Embeds[0].Fields = Enumerable.Range(0, 25).Select(i => new SelfRoleEmbedField { Name = $"N{i}", Value = "V" }).ToList();
        SelfRoleMessageRenderer.ValidateStructure(panel);
    }

    [Theory]
    [InlineData(11, "selfRoles.tooManyEmbeds")]
    [InlineData(26, "selfRoles.tooManyFields")]
    public void Rejects_excess_embeds_or_fields(int count, string error)
    {
        var panel = Panel();
        if (error.EndsWith("Embeds")) panel.Embeds.AddRange(Enumerable.Range(0, count - 1).Select(_ => new SelfRoleEmbed { Kind = SelfRoleEmbedKind.Content, Title = "x" }));
        else panel.Embeds[0].Fields = Enumerable.Range(0, count).Select(_ => new SelfRoleEmbedField { Name = "n", Value = "v" }).ToList();
        Assert.Equal(error, Assert.Throws<SelfRoleValidationException>(() => SelfRoleMessageRenderer.ValidateStructure(panel)).ErrorKey);
    }

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public void Enforces_title_limit(int length, bool invalid)
    {
        var panel = Panel(new string('x', length));
        if (invalid) Assert.Equal("selfRoles.embedTitleTooLong", Assert.Throws<SelfRoleValidationException>(() => SelfRoleMessageRenderer.ValidateStructure(panel)).ErrorKey);
        else SelfRoleMessageRenderer.ValidateStructure(panel);
    }

    [Theory]
    [InlineData(4086, false)] // Two separators plus eight characters for "✅ <@&42>".
    [InlineData(4087, true)]
    public void Enforces_final_description_limit(int manualLength, bool invalid)
    {
        var panel = Panel(description: new string('x', manualLength));
        if (invalid) Assert.Equal("selfRoles.embedDescriptionTooLong", Assert.Throws<SelfRoleValidationException>(() => SelfRoleMessageRenderer.ValidateStructure(panel)).ErrorKey);
        else SelfRoleMessageRenderer.ValidateStructure(panel);
    }

    [Theory]
    [InlineData(1640, false)]
    [InlineData(1641, true)]
    public void Enforces_global_text_limit_including_legend(int descriptionLength, bool invalid)
    {
        var panel = Panel(new string('x', 256));
        panel.Embeds.Add(new SelfRoleEmbed { Kind = SelfRoleEmbedKind.Content, Description = new string('x', 4096) });
        panel.Embeds.Add(new SelfRoleEmbed { Kind = SelfRoleEmbedKind.Content, Description = new string('x', descriptionLength) });
        if (invalid) Assert.Equal("selfRoles.embedTextTooLong", Assert.Throws<SelfRoleValidationException>(() => SelfRoleMessageRenderer.ValidateStructure(panel)).ErrorKey);
        else SelfRoleMessageRenderer.ValidateStructure(panel);
    }

    [Fact]
    public void Requires_exactly_one_role_mappings_embed()
    {
        var panel = Panel();
        panel.Embeds[0].Kind = SelfRoleEmbedKind.Content;
        Assert.Equal("selfRoles.missingRoleMappingsEmbed", Assert.Throws<SelfRoleValidationException>(() => SelfRoleMessageRenderer.ValidateStructure(panel)).ErrorKey);
    }

    [Fact]
    public void Normalizes_legacy_panel_and_counts_actual_custom_emoji_syntax()
    {
        var panel = new SelfRolePanel { Title = " Legacy ", Description = " Text ", Color = "", Mappings = [new() { RoleId = 123456789012345678, Emoji = new() { Kind = SelfRoleEmojiKind.Custom, Value = "987654321098765432", Name = "wave" } }] };
        SelfRoleMessageRenderer.Normalize(panel);
        Assert.Equal(SelfRoleEmbedKind.RoleMappings, panel.Embeds.Single().Kind);
        Assert.NotNull(panel.Embeds.Single().Id);
        Assert.Equal("Text\n\n<:wave:987654321098765432> <@&123456789012345678>", SelfRoleMessageRenderer.FinalDescription(panel, panel.Embeds.Single()));
        Assert.Equal(panel.Embeds.Single().Title.Trim().Length + SelfRoleMessageRenderer.FinalDescription(panel, panel.Embeds.Single()).Length, SelfRoleMessageRenderer.TextLength(panel));
    }

    private static SelfRolePanel Panel(string title = "", string description = "") => new()
    {
        Mappings = [new() { RoleId = 42, Emoji = new() { Kind = SelfRoleEmojiKind.Unicode, Value = "✅", Name = "check" } }],
        Embeds = [new() { Kind = SelfRoleEmbedKind.RoleMappings, Title = title, Description = description, Color = "#5865F2" }]
    };
}
