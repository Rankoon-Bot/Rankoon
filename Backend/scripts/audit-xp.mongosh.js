// Read-only, guild/member-scoped production diagnosis. No writes or repairs.
// Set RANKOON_AUDIT_GUILD_ID and RANKOON_AUDIT_USER_ID as exact decimal strings.
const auditGuildId = process.env.RANKOON_AUDIT_GUILD_ID;
const auditUserId = process.env.RANKOON_AUDIT_USER_ID;
if (!/^[1-9][0-9]{0,19}$/.test(auditGuildId || "") || !/^[1-9][0-9]{0,19}$/.test(auditUserId || "")) {
  throw new Error("Set RANKOON_AUDIT_GUILD_ID and RANKOON_AUDIT_USER_ID to Discord IDs before running this read-only audit.");
}
const auditGuild = Decimal128.fromString(auditGuildId);
const auditUser = Decimal128.fromString(auditUserId);
const auditMember = { guild_id: auditGuild, user_id: auditUser };
function section(name, value) {
  print("\n" + name);
  print(EJSON.stringify(value, null, 2, { relaxed: false }));
}
section("Guild XP settings", db.guild_xp_settings.findOne({ guild_id: auditGuild }, {
  enabled: 1, message: 1, voice: 1, reaction: 1, thread: 1, event_interest: 1,
  excluded_channel_ids: 1, excluded_category_ids: 1, excluded_role_ids: 1,
  channel_multipliers: 1, revision: 1, updated_at: 1
}));
section("Member XP projection", db.member_xp.findOne(auditMember));
section("Persisted voice session (runtime observation epoch)", db.voice_sessions.findOne(auditMember));
section("Ledger totals by source, projection state and cooldown decision", db.xp_ledger.aggregate([
  { $match: auditMember },
  { $group: {
    _id: { source: "$source", status: "$projection_status", denied: "$cooldown_denied", scope: "$scope" },
    count: { $sum: 1 }, recordedAmount: { $sum: "$amount" },
    firstAt: { $min: "$occurred_at_utc" }, lastAt: { $max: "$occurred_at_utc" }
  } },
  { $sort: { "_id.source": 1, "_id.status": 1 } }
]).toArray());
section("Pending ledger entries", db.xp_ledger.find({ ...auditMember, projection_status: "Pending" })
  .sort({ created_at: 1 }).limit(30).toArray());
section("Recent voice day projections (no segment payloads)", db.voice_activity_days.find(auditMember, {
  segments: 0, session_cursors: 0
}).sort({ day_start_utc: -1, part: -1 }).limit(10).toArray());
section("Recent level transitions: XP/roles/announcement are separate states", db.level_transition_events.find(auditMember)
  .sort({ created_at_utc: -1 }).limit(20).toArray());
section("Guild season switch", db.guild_season_settings.findOne({ guild_id: auditGuild }, { enabled: 1, updated_at_utc: 1 }));
section("Guild season attribution windows", db.guild_seasons.find({ guild_id: auditGuild }, {
  name: 1, status: 1, starts_at_utc: 1, ends_at_utc: 1, activated_at_utc: 1, closed_at_utc: 1
}).sort({ starts_at_utc: -1 }).limit(20).toArray());
section("Voice storage migration authority", db.voice_ledger_migration_states.findOne({ _id: "voice-ledger-v1" }, {
  phase: 1, parity: 1, deletion_approval_fingerprint: 1
}));
section("Ledger indexes", db.xp_ledger.getIndexes());
print("\nInterpretation: denied=true entries award zero XP. Pending entries are not final balances. Do not add legacy voice ledger amounts to migrated voice days without checking migration authority.");
