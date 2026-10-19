# Feature reference

For setup and configuration, use the [documentation index](../README.md).

## Features

### XP and leveling

Rankoon records discrete non-zero XP grants, such as messages, reactions, events,
and manual adjustments, in an immutable MongoDB ledger before projecting them
into member, season, and guild-stat totals. Unique grant keys and projection
leases make retries idempotent. Voice intervals do not create XP-ledger grants:
they are stored as exact, compacted segments in daily `voice_activity_days`
documents. The MEE6-compatible cumulative curve calculates levels from the
persistent projected total.

### XP history and adjustments

`XP-Verlauf & Korrekturen` / `XP History & Adjustments` is an XP audit feature,
not general user management. `xp-audit` permits reading the guild-scoped member
history; `xp-adjustments` additionally permits immutable manual corrections.
Corrections contain the actor and a reason in the permanent XP ledger, are never
edited or deleted, and are neutralized only by a linked reversal entry. Non-owner
actors cannot correct their own XP. The activity log receives a supplementary
event, while the ledger remains the audit record. No message contents are stored.

The following sources are implemented and independently configurable:

| Source | Default behavior |
| --- | --- |
| Messages | 5-50 XP based on message length, with a 60-second member cooldown |
| Reactions | 2 XP when a member adds a reaction, with a 30-second cooldown |
| Thread creation | 15 XP for creating a thread |
| Thread messages | 5 XP, with a 60-second cooldown |
| Scheduled events | 10 XP when marking interest; removed interest reverses the grant |
| Voice activity | 10 XP per minute after a 60-second minimum session |

Administrators can enable or disable the complete XP system or individual
sources, adjust point values and cooldowns, exclude roles/channels/categories,
and configure voice-channel multipliers. Channel multipliers currently apply to
voice XP.

Voice XP can be configured to:

- ignore the server AFK channel;
- require more than one qualifying human in the channel;
- exclude deafened members;
- respect excluded roles, channels, and categories;
- grant fractional XP according to eligible connected time.

Every eligible watchdog slice is persisted before projection. Adjacent slices
with the same session, channel, season, effective rate, multipliers, and settings
revision compact into one exact segment; changes preserve a segment boundary.
Daily documents retain total and projected revisions, eligible seconds, XP, and
season totals. Active users project at most once per configured 15-30 second
window, while leave, channel, UTC-day, season, and module-disable boundaries
request immediate projection. A batch repair worker resumes pending or expired
projections after interruption without relying on an in-memory throttle.

Message, reaction, and thread-message cooldowns are acquired atomically on the
member record after a ledger entry is written. Cooldown-denied entries are retained
without projection, so retries cannot later award them. Reaction removal can
reverse its original award when enabled; scheduled-event interest removal also
creates an idempotent reversal. Reversals retain the original season attribution.

Voice activity checkpoints and full Discord reconciliation have separate cadences.
The checked-in defaults checkpoint every 30 seconds and reconcile every 60 seconds.
Self-hosters can override `VOICEACTIVITY__CHECKPOINTINTERVALSECONDS` and
`VOICEWATCHDOG__RECONCILIATIONINTERVALSECONDS`; these are global settings.

Eligible intervals are split at persisted season and UTC-day boundaries, producing
exact compressed voice segments rather than interval ledger grants. The first
qualifying settlement includes time since joining, including the configured
minimum-session interval.

### Custom Bot Identity

Custom Bot Identity is disabled by default. Set `CUSTOMBOTIDENTITY__ENABLED=true` and configure a separate, long random `CUSTOMBOTIDENTITY__FINGERPRINTKEY` to allow guild owners to connect one custom Discord bot application to one guild. `CUSTOMBOTIDENTITY__MAXACTIVEGUILDS` limits reserved identities; omit it for no limit. An empty `CUSTOMBOTIDENTITY__ALLOWEDGUILDIDS` allowlist permits every guild, while indexed values such as `CUSTOMBOTIDENTITY__ALLOWEDGUILDIDS__0=123456789012345678` restrict access.

Custom bot tokens are encrypted using ASP.NET Core Data Protection and never
returned by the API. Outside Development, `DATAPROTECTION__KEYRINGPATH` must name
a persistent, readable/writable directory outside the application image; startup
rejects an invalid ring without logging secrets. Keep
`CUSTOMBOTIDENTITY__FINGERPRINTKEY` stable and separate from JWT and Discord secrets.
`CUSTOMBOTIDENTITY__STARTUPPARALLELISM` controls staggered runtime restoration and
must be between `1` and `4`. See
[deployment key-ring operations](../operations/deployment.md#data-protection-key-ring)
for Docker volumes, backups, recovery, and multi-instance permissions.

### Seasons

Guilds can run manual or scheduled fixed-duration, monthly, quarterly,
semiannual, or annual seasons. Explicitly enabled automatic planning keeps a
bounded number of seasons prepared. Each prepared season snapshots its settings so later
configuration changes do not rewrite its rules.

Activation initializes a baseline from zero, lifetime, or lifetime-percentage XP.
Optional carry-over from previous frozen standings applies once. Closing writes
immutable final standings with rank, total XP, level, message count, voice time,
and public visibility. A per-guild MongoDB lease prevents concurrent automation.

Level rewards are cumulative: Rankoon adds every configured reward role at or
below a member's level and removes configured rewards above it. The Rankoon bot
role must be above every reward role it manages.

### Rankings

Rankoon offers three views of member progress:

- `/rank` shows the invoking member's level, total XP, and next level threshold.
- `/leaderboard` shows the server's top ten current members in Discord.
- The web leaderboard supports a guild-specific alias, signed cursor pagination,
  public or members-only access, per-member public visibility, and jump-to-user
  navigation for authenticated guild members.

Web leaderboard entries include level, total XP, message count, and connected
voice time. New guild leaderboards default to members-only visibility.

### Temporary voice channels

Administrators create voice hubs in the dashboard. A hub can use an existing
join channel or ask Rankoon to create one, and supports:

- a target category;
- a temporary channel name template using `{username}` or `{user}`;
- a user limit and bitrate;
- a maximum number of channels owned by one member;
- enabled or disabled state.

When a non-bot member joins an enabled hub, Rankoon creates a channel, records
the member as its owner, and moves them into it. Empty temporary channels are
removed automatically. Missing configured hub channels are reconciled while the
bot is running.

### Self-role panel health

Self-role panels show **Active** after Rankoon verifies their Discord message,
saved embeds, available role reactions, role hierarchy, and `ManageRoles` access.
Permissions needed only to edit or republish a message do not make an otherwise
working panel unhealthy. A confirmed missing message, reaction, or manageable role
shows **Needs attention**, with an explanation and a recovery step.

Discord outages, timeouts, rate limits, or missing access to inspect message
history show **Status unknown**. This does not imply that role assignment has
stopped. Checks use the authoritative bot identity's Discord REST client instead
of potentially incomplete gateway message caches.

Opening or reloading the panel list rechecks enabled panels at most once per
minute, including panels with earlier errors. Reconnecting the bot forces a new
check and restores missing reactions on existing messages. Health checks never
recreate deleted messages; use **Repair** to do that. A successful check clears
old errors without changing the panel settings or publication timestamp.

### Dashboard and reports

The Angular dashboard uses Discord OAuth and supports English and German. Guild
owners can grant dashboard modules to Discord roles for:

- XP configuration;
- leaderboard configuration;
- voice hubs;
- reporting.

The reporting pages expose recorded guild activity, command usage, and bot errors.
Retention is configured through `ReportingRetention`: the checked-in defaults
retain audit events for 180 days, error occurrences for 30 days, and incidents for
180 days. These reports describe application activity; separate
[performance instrumentation](../backend/performance.md) provides metrics and traces.

### Bot management

Global bot operations are restricted to authorized bot operators. They provide
guild health, usage, server installation history, and incident investigation and
status actions. Usage describes Rankoon report activity rather than CPU or memory
utilization. See the [server history checks](../testing/bot-guild-history.md) for
history semantics and access verification.

## Slash Commands

Rankoon synchronizes these commands globally when the Discord client becomes
ready. Discord may take time to propagate global command changes.

| Command | Description |
| --- | --- |
| `/help` | Show Rankoon commands and how to use them |
| `/rank` | Show your level, total XP, and next cumulative level threshold |
| `/leaderboard` | Show the top ten current members by XP |
| `/voice status` | Show the channel you own, its owner, and available places |
| `/voice name value:<name>` | Rename the temporary channel you own |
| `/voice limit value:<0-99>` | Change your temporary channel's user limit; `0` removes the limit |
| `/voice kick member:<member>` | Disconnect a selected member while managing your owned channel |
| `/voice transfer member:<member>` | Transfer the stored channel ownership to another member |

Command responses are ephemeral. `/help` also links to the dashboard and
self-hosting instructions. The `/voice` command works only while the invoking
member is connected to the temporary channel recorded as theirs.
Legacy `action` options remain accepted while global command changes propagate.

## Bot permission diagnostics

Guild administrators can open **Bot diagnostics** in the server navigation and run a read-only permission scan. The report evaluates only enabled Rankoon features and their configured channels or roles, including effective Discord.Net channel permissions and role hierarchy.

Rankoon does not require or recommend `Administrator`. Category and channel overrides can still restrict granular guild permissions. Role assignment additionally requires `ManageRoles` and a Rankoon role positioned strictly above every configured level or self-role. The scan explains missing permissions and configuration changes but never changes Discord permissions, overwrites, or role positions automatically.

The first version uses the Discord socket cache and a short in-memory cache. If Discord is reconnecting or a configured resource cannot be resolved, it reports `Unknown` instead of assuming access.

## Level-up announcements

New announcement profiles are disabled by default. Existing legacy announcement
settings, including a configured legacy level-up channel, are preserved by the
[XP settings migration](../backend/xp-settings-migrations.md). Administrators can
configure a text channel and safe templates in **Level-Up-Nachrichten**.

Rendering supports only the allowlisted tokens exposed by the API; template text
cannot execute code. Discord sends use restrictive allowed mentions, so manually
typed mentions, roles, `@everyone`, and `@here` never ping. A transition outbox
separates XP projection from Discord delivery and retries transient failures;
deduplication is best effort rather than an exactly-once Discord guarantee.

The bot needs access to the target text channel and permission to send messages;
role delivery is only claimed after a successful role assignment. XP imports
never create announcements. See [XP integrity and recovery](../backend/xp-integrity.md)
for reconciliation and delivery limits.
