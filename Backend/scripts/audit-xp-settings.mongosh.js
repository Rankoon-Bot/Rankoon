// Read-only production diagnosis. Run with a read-only MongoDB account.
const settingsAuditId = process.env.RANKOON_AUDIT_GUILD_ID;
if (!/^[1-9][0-9]{0,19}$/.test(settingsAuditId || "")) {
  throw new Error("Set RANKOON_AUDIT_GUILD_ID to the exact Discord server ID.");
}
const settingsAuditGuild = Decimal128.fromString(settingsAuditId);
function settingsSection(name, value) {
  print("\n" + name);
  print(EJSON.stringify(value, null, 2, { relaxed: false }));
}
const xpSettings = db.guild_xp_settings.findOne({ guild_id: settingsAuditGuild });
const announcements = db.guild_level_up_announcement_settings.findOne({ guild_id: settingsAuditGuild });
settingsSection("Stored XP settings including rank roles and legacy channel", xpSettings);
settingsSection("Stored announcements including current and legacy profiles", announcements);
settingsSection("Schema diagnosis", {
  xpFound: !!xpSettings,
  xpSchema: xpSettings?.schema_version ?? "unversioned",
  rankRoles: xpSettings?.level_roles?.length ?? 0,
  announcementsFound: !!announcements,
  announcementSchema: announcements?.schema_version ?? "unversioned",
  legacyTemplates: announcements?.templates?.length ?? 0,
  lifetimeEnabled: announcements?.lifetime?.enabled ?? null,
  lifetimeChannel: announcements?.lifetime?.channel_id ?? null,
  legacyEnabled: announcements?.enabled ?? null,
  legacyChannel: announcements?.channel_id ?? xpSettings?.level_up_channel_id ?? null,
  legacyNeedsMigration: !!announcements && !announcements.lifetime &&
    ("templates" in announcements || "channel_id" in announcements || "enabled" in announcements)
});
settingsSection("Available original settings snapshots (latest 20; not automatically restored)",
  db.xp_settings_backups.find({ "original.guild_id": settingsAuditGuild })
    .sort({ created_at_utc: -1 }).limit(20).toArray());
