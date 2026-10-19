# Bot Operations server history

The operator-only `/api/bot-management/server-history` endpoint stores one permanent
MongoDB document per bot, guild and Discord join timestamp. Initial gateway
observations are baseline records, not new installations. Reinstallations and
custom bot identities have separate records. Runtime shutdowns and temporary
guild unavailability do not count as removals.

Direct `LeftGuild` events carry an observed removal time. A complete shard READY
snapshot can detect memberships that disappeared while offline or were replaced
by another join timestamp. These records show the detection time, never an
invented uninstall time. Events before tracking began cannot be reconstructed;
Discord does not supply the installer/remover or a removal reason.

Run persistence regression tests against a disposable MongoDB with
`RANKOON_SEASON_TEST_MONGO` set, filtering `BotGuildHistoryTests`.

Manual acceptance checks:

- As a bot operator, open Bot Operations → Server history from either navigation.
  Verify 24-hour, 7-day, 30-day and 90-day filters, search by server name/ID,
  installed/removed filters, refresh and pagination.
- Restart the backend: existing installations retain their initial timestamps
  and are not counted again. Rename a server: its last known name updates.
- Install, remove, then reinstall a bot. Verify two installation records, an
  observed removal and distinct custom bot IDs on the same server.
- Remove a bot while the backend is offline. On restart, verify a detected
  removal with the unknown-time explanation. An unavailable guild must stay
  installed. A shard must never close memberships belonging to another shard.
- Deny a history write and verify the failure counter, safe retries and recovery
  on a later gateway observation. Previously unobserved departures may leave gaps.
- Confirm ordinary server users cannot open the page or read its API.
- At desktop, 768px and 390px, verify no horizontal page overflow, keyboard form
  submission, visible focus, readable statuses and an actionable empty state.
- Rapidly change the range or retry a failed request: stale responses must not
  overwrite the latest range. Current memberships and period activity are
  explicitly described as different measurements on the overview.
