namespace Rankoon.Data.Xp.Import;

public sealed record XpImportMember(
    ulong UserId,
    string DisplayName,
    decimal ImportedXp,
    long MessageCount,
    decimal? VoiceSeconds);

public sealed record ParsedXpImport(
    XpImportFormat Format,
    IReadOnlyList<XpImportMember> Members,
    int SkippedInvalid,
    int SkippedForeignGuild,
    int DuplicateUsers);

public sealed record XpImportResult(
    XpImportFormat Format,
    int Imported,
    int SkippedInvalid,
    int SkippedForeignGuild,
    int DuplicateUsers);

public sealed class XpImportParseException(string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}
