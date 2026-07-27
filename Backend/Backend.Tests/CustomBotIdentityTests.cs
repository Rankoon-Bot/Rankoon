using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Rankoon.Data.Discord;
using Xunit;

namespace Backend.Tests;

public sealed class CustomBotIdentityTests
{
    [Fact]
    public void DisabledFeatureBlocksActivation() =>
        Assert.Equal(CustomBotAccessReason.FeatureDisabled, Decide(new() { Enabled = false }).Reason);

    [Fact]
    public void EmptyAllowlistAllowsGuild() =>
        Assert.True(Decide(new() { Enabled = true }).CanActivate);

    [Fact]
    public void AllowlistAllowsIncludedGuild() =>
        Assert.True(Decide(new() { Enabled = true, AllowedGuildIds = [42] }).CanActivate);

    [Fact]
    public void AllowlistBlocksOtherGuild() =>
        Assert.Equal(CustomBotAccessReason.GuildNotAllowed, Decide(new() { Enabled = true, AllowedGuildIds = [7] }).Reason);

    [Fact]
    public void CapacityBlocksNewReservation() =>
        Assert.Equal(CustomBotAccessReason.CapacityReached, Decide(new() { Enabled = true, MaxActiveGuilds = 1 }, count: 1).Reason);

    [Fact]
    public void ExistingReservationSurvivesReducedLimit()
    {
        var decision = Decide(new() { Enabled = true, MaxActiveGuilds = 1 }, reservation: true, count: 2);
        Assert.True(decision.CanActivate);
        Assert.Equal(CustomBotAccessReason.AlreadyReserved, decision.Reason);
    }

    [Fact]
    public void TokenProtectorEncryptsAndFingerprintsWithoutExposingToken()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var provider = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Rankoon.Tests"));
            var service = new CustomBotTokenProtector(provider, Options.Create(new CustomBotIdentityOptions { FingerprintKey = new string('k', 32) }));
            const string token = "secret.discord.bot.token";
            var encrypted = service.Protect(token);
            Assert.DoesNotContain(token, encrypted, StringComparison.Ordinal);
            Assert.Equal(token, service.Unprotect(encrypted));
            Assert.Equal(service.CreateFingerprint(token), service.CreateFingerprint(token));
        }
        finally { if (directory.Exists) directory.Delete(true); }
    }

    [Fact]
    public void TokenProtectorDecryptsCiphertextFromTheSameKeyRing()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var first = new CustomBotTokenProtector(DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Rankoon")), Options.Create(new CustomBotIdentityOptions { FingerprintKey = new string('k', 32) }));
            var second = new CustomBotTokenProtector(DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Rankoon")), Options.Create(new CustomBotIdentityOptions { FingerprintKey = new string('k', 32) }));

            Assert.Equal("secret.discord.bot.token", second.Unprotect(first.Protect("secret.discord.bot.token")));
        }
        finally { if (directory.Exists) directory.Delete(true); }
    }

    [Fact]
    public void TokenProtectorRejectsCiphertextFromAnotherKeyRing()
    {
        var firstDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var secondDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var first = new CustomBotTokenProtector(DataProtectionProvider.Create(firstDirectory, builder => builder.SetApplicationName("Rankoon")), Options.Create(new CustomBotIdentityOptions { FingerprintKey = new string('k', 32) }));
            var second = new CustomBotTokenProtector(DataProtectionProvider.Create(secondDirectory, builder => builder.SetApplicationName("Rankoon")), Options.Create(new CustomBotIdentityOptions { FingerprintKey = new string('k', 32) }));

            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => second.Unprotect(first.Protect("secret.discord.bot.token")));
        }
        finally
        {
            if (firstDirectory.Exists) firstDirectory.Delete(true);
            if (secondDirectory.Exists) secondDirectory.Delete(true);
        }
    }

    private static CustomBotAccessDecision Decide(CustomBotIdentityOptions options, bool reservation = false, int count = 0) =>
        CustomBotIdentityAccessPolicy.Decide(options, 42, false, reservation, count);
}
