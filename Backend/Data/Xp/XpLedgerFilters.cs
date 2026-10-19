using MongoDB.Driver;
using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

internal static class XpLedgerFilters
{
    // The original ledger predates projection states. Those durable grants are
    // historical awards, not new pending requests that may be incremented again.
    internal static FilterDefinition<XpLedgerEntry> Applied =>
        Builders<XpLedgerEntry>.Filter.Eq(x => x.ProjectionStatus, SeasonProjectionStatus.Applied) |
        Builders<XpLedgerEntry>.Filter.Exists(x => x.ProjectionStatus, false);
}
