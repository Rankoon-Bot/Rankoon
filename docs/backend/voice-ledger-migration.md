# Legacy voice ledger migration

`VoiceLedgerMigrationService` snapshots the highest XP-ledger `_id`, copies that finite range in `_id` order, verifies parity, and then waits. It never removes ledger rows until `ApproveDeletionAsync` receives the fingerprint from the persisted successful parity report. Deletion is limited to `DeleteBatchSize` source rows per pass.

The migration accepts only `source == "voice"` entries that are fully projected (`ProjectionStatus == Applied` and `ProjectedAtUtc != null`), have effective kind `AutomaticGrant` according to `XpLedgerSemantics`, and contain a valid channel and positive-duration interval. It splits intervals at UTC midnight. `lv_` session IDs are the reversible base64url representation of the source ObjectId. The accumulator persists one `VoiceSessionCursor` per legacy session; migrated segments use the reserved `long.MinValue` settings revision and are marked fully projected. Verification checks exact global, guild/user, season, and guild/user/season XP and seconds, and reports generated day documents, segments, and elapsed duration.

`Copying`, `Verifying`, and `ParityFailed` retain legacy-ledger history authority; audit reads legacy voice rows and excludes only their migrated compressed copies while still showing new compressed activity. `AwaitingDeletionApproval` begins compressed authority after successful parity. `Deleting` additionally requires approval of the current parity fingerprint. `Completed` means the approved source range was removed and compressed voice remains authoritative.

## Required integration

Add this collection to `RankoonDbContext`:

```csharp
public IMongoCollection<VoiceLedgerMigrationState> VoiceLedgerMigrationStates =>
    _database.GetCollection<VoiceLedgerMigrationState>("voice_ledger_migration_states");
```

Register the options and worker. The service obtains all collections from `RankoonDbContext`.

```csharp
builder.Services.AddOptions<VoiceLedgerMigrationOptions>()
    .Bind(builder.Configuration.GetSection(VoiceLedgerMigrationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<VoiceLedgerMigrationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VoiceLedgerMigrationService>());
```

Create a migration-state operational index in `MongoIndexInitializer` before starting the worker:

```csharp
await database.VoiceLedgerMigrationStates.Indexes.CreateOneAsync(
    new CreateIndexModel<VoiceLedgerMigrationState>(
        Builders<VoiceLedgerMigrationState>.IndexKeys.Ascending(x => x.Phase).Ascending(x => x.UpdatedAtUtc),
        new CreateIndexOptions { Name = "phase_updated" }),
    cancellationToken: stoppingToken);
```

MongoDB's built-in unique `_id` index enforces the singleton migration state. The required unique destination index is `(guild_id, user_id, day_start_utc, part)` named `guild_user_day_part_unique`; `guild_user_day_session` supports persistent session-cursor lookup and the XP-ledger `_id` index supplies source ordering.

Suggested configuration:

```json
"VoiceLedgerMigration": {
  "DeleteBatchSize": 50,
  "MaxRetries": 8,
  "RetryDelaySeconds": 30,
  "IdleDelaySeconds": 60
}
```

Copying defaults to `VoiceActivity:MigrationBatchSize` (`500`). Set the nullable
specialized `VoiceLedgerMigration:CopyBatchSize` only when migration needs a
different batch size.

After `Phase` becomes `AwaitingDeletionApproval`, inspect `Parity`, retain a backup, and explicitly call `ApproveDeletionAsync(Parity.Fingerprint)` from an operator-only integration. A changed or failed report cannot authorize deletion. Do not configure automatic approval.
