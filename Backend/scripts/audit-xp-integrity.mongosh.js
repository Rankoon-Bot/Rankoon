// Read-only complete XP source audit. Run against the intended DB with mongosh --file.
// Optional RANKOON_XP_OUTPUT writes the report locally. No database mutations or Discord calls.
const xpAuditFs = require('fs');
const xpAuditGuild = process.env.RANKOON_AUDIT_GUILD_ID;
if (xpAuditGuild && !/^[1-9][0-9]{0,19}$/.test(xpAuditGuild)) throw new Error('RANKOON_AUDIT_GUILD_ID must be an exact Discord ID.');
const xpAuditGuildFilter = xpAuditGuild ? { guild_id: Decimal128.fromString(xpAuditGuild) } : {};
const xpAuditScale = 34;
const xpAuditUnit = 10n ** BigInt(xpAuditScale);
const xpAuditScalar = (value) => {
  if (value == null) return '0';
  const bson = EJSON.serialize(value, { relaxed: false });
  return String(bson.$numberDecimal ?? bson.$numberLong ?? bson.$numberInt ?? bson.$numberDouble ?? value);
};
const xpAuditDecimal = (value) => {
  const text = xpAuditScalar(value);
  const match = /^([+-]?)(\d+)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/.exec(text);
  if (!match) throw new Error('Invalid XP amount; refuse an incomplete audit.');
  const fraction = match[3] || '';
  const shift = xpAuditScale + Number(match[4] || 0) - fraction.length;
  if (shift < 0) throw new Error('XP precision exceeds audit limits; inspect this amount separately.');
  return (match[1] === '-' ? -1n : 1n) * BigInt(match[2] + fraction) * (10n ** BigInt(shift));
};
const xpAuditFormat = (value) => {
  const negative = value < 0n; const absolute = negative ? -value : value;
  const fraction = String(absolute % xpAuditUnit).padStart(xpAuditScale, '0').replace(/0+$/, '');
  return (negative ? '-' : '') + String(absolute / xpAuditUnit) + (fraction ? '.' + fraction : '');
};
const xpAuditId = (value) => value == null ? '' : typeof value === 'object' && value._bsontype === 'ObjectId' ? value.toHexString() : xpAuditFormat(xpAuditDecimal(value));
const xpAuditKey = (guild, user, season = '') => xpAuditId(guild) + ':' + xpAuditId(user) + ':' + season;
const xpAuditMembers = db.member_xp.find({ ...xpAuditGuildFilter, is_development_mock: { $ne: true } }, {
  guild_id: 1, user_id: 1, display_name: 1, imported_mee6_xp: 1, earned_xp: 1, manual_adjustment: 1, voice_xp: 1, total_xp: 1, is_current_member: 1
}).limit(100001).maxTimeMS(30000).toArray();
if (xpAuditMembers.length > 100000) throw new Error('Member limit exceeded; audit one guild at a time.');
const xpAuditLedger = db.xp_ledger.find(xpAuditGuildFilter, { guild_id: 1, user_id: 1, season_id: 1, source: 1, grant_key: 1, amount: 1, kind: 1, scope: 1, projection_status: 1, cooldown_denied: 1, is_projection_control: 1 }).limit(500001).maxTimeMS(30000).toArray();
if (xpAuditLedger.length > 500000) throw new Error('Ledger limit exceeded; audit one guild at a time.');
const xpAuditDays = db.voice_activity_days.find(xpAuditGuildFilter, { guild_id: 1, user_id: 1, projection_status: 1, projection_revision: 1, projected_revision: 1,
  awarded_xp: 1, projected_awarded_xp: 1, eligible_seconds: 1, projected_eligible_seconds: 1, projected_season_totals: 1,
  'segments.session_id': 1, 'segments.settings_revision': 1, 'segments.awarded_xp': 1, 'segments.season_id': 1
}).limit(100001).maxTimeMS(30000).toArray();
if (xpAuditDays.length > 100000) throw new Error('Voice day limit exceeded; audit one guild at a time.');
const xpAuditMigration = db.voice_ledger_migration_states.findOne({ _id: 'voice-ledger-v1' });
const xpAuditCompressed = xpAuditMigration?.parity?.matches === true && xpAuditMigration?.parity?.grouped_parity_verified === true &&
  (xpAuditMigration.phase === 'AwaitingDeletionApproval' || ['Deleting', 'Completed'].includes(xpAuditMigration.phase) && xpAuditMigration.deletion_approval_fingerprint === xpAuditMigration.parity.fingerprint);
const xpAuditSources = new Map(); const xpAuditPending = new Set(); const xpAuditInconsistent = new Set(); const xpAuditReplaced = new Set();
const xpAuditTotals = (key) => {
  if (!xpAuditSources.has(key)) xpAuditSources.set(key, { earned: 0n, manual: 0n, voice: 0n, baseline: false });
  return xpAuditSources.get(key);
};
for (const day of xpAuditDays) {
  const key = xpAuditKey(day.guild_id, day.user_id);
  if (day.projection_status === 'Projecting') xpAuditPending.add(key);
  if (day.projection_status === 'Applied' && (xpAuditDecimal(day.awarded_xp) !== xpAuditDecimal(day.projected_awarded_xp) ||
      xpAuditScalar(day.projection_revision) !== xpAuditScalar(day.projected_revision) || xpAuditScalar(day.eligible_seconds) !== xpAuditScalar(day.projected_eligible_seconds))) xpAuditInconsistent.add(key);
  const legacy = (day.segments || []).filter(s => xpAuditScalar(s.settings_revision) === '-9223372036854775808');
  if (xpAuditCompressed) for (const segment of legacy) {
    if (/^lv_[A-Za-z0-9_-]{16}$/.test(segment.session_id)) xpAuditReplaced.add(Buffer.from(segment.session_id.slice(3).replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('hex'));
  }
  let voice = xpAuditDecimal(day.projected_awarded_xp);
  if (!xpAuditCompressed) voice -= legacy.reduce((sum, x) => sum + xpAuditDecimal(x.awarded_xp), 0n);
  if (voice < 0n) voice = 0n;
  const totals = xpAuditTotals(key); totals.earned += voice; totals.voice += voice;
  for (const season of day.projected_season_totals || []) {
    if (!season.season_id) continue;
    const seasonId = xpAuditId(season.season_id);
    let amount = xpAuditDecimal(season.awarded_xp);
    if (!xpAuditCompressed) amount -= legacy.filter(x => xpAuditId(x.season_id) === seasonId).reduce((sum, x) => sum + xpAuditDecimal(x.awarded_xp), 0n);
    const seasonTotals = xpAuditTotals(xpAuditKey(day.guild_id, day.user_id, seasonId)); seasonTotals.earned += amount; seasonTotals.voice += amount;
  }
}
let xpAuditLegacyEntries = 0;
for (const entry of xpAuditLedger) {
  const key = xpAuditKey(entry.guild_id, entry.user_id);
  if (entry.projection_status === 'Pending') { xpAuditPending.add(key); continue; }
  if (entry.projection_status !== undefined && entry.projection_status !== 'Applied' || entry.cooldown_denied || entry.is_projection_control) continue;
  if (entry.projection_status === undefined) xpAuditLegacyEntries++;
  if (xpAuditCompressed && entry.source === 'voice' && xpAuditReplaced.has(xpAuditId(entry._id))) continue;
  const automatic = entry.kind == null || ['AutomaticGrant', 'AutomaticReversal'].includes(entry.kind);
  const amount = xpAuditDecimal(entry.amount);
  const seasonId = xpAuditId(entry.season_id);
  const scope = entry.scope || (seasonId ? 'LifetimeAndSeason' : 'LifetimeOnly');
  const scopes = [];
  if (scope !== 'SeasonOnly') scopes.push({ key, baseline: 'migration:manual-adjustment:v1:' + xpAuditId(entry.guild_id) + ':' + xpAuditId(entry.user_id), source: 'legacy_manual_adjustment' });
  if (seasonId && scope !== 'LifetimeOnly') scopes.push({ key: xpAuditKey(entry.guild_id, entry.user_id, seasonId), baseline: 'migration:season-manual-adjustment:v1:' + seasonId + ':' + xpAuditId(entry.user_id), source: 'legacy_season_manual_adjustment' });
  for (const scopeInfo of scopes) {
    const totals = xpAuditTotals(scopeInfo.key);
    if (automatic) totals.earned += amount; else totals.manual += amount;
    if (automatic && ['voice', 'voice_reversal'].includes(entry.source)) totals.voice += amount;
    if (entry.kind === 'SystemMigration' && entry.source === scopeInfo.source && entry.grant_key === scopeInfo.baseline) totals.baseline = true;
  }
}
const xpAuditRows = [];
const xpAuditCompare = (member, seasonId = '') => {
  const key = xpAuditKey(member.guild_id, member.user_id, seasonId);
  const totals = xpAuditTotals(key);
  const earnedDelta = totals.earned - xpAuditDecimal(member.earned_xp); const manualDelta = totals.manual - xpAuditDecimal(member.manual_adjustment);
  const voiceDelta = totals.voice - xpAuditDecimal(member.voice_xp);
  const expected = xpAuditDecimal(seasonId ? member.starting_xp : member.imported_mee6_xp) + totals.earned + totals.manual;
  const totalDelta = expected - xpAuditDecimal(member.total_xp);
  const parent = xpAuditKey(member.guild_id, member.user_id);
  const changed = earnedDelta !== 0n || manualDelta !== 0n || voiceDelta !== 0n || totalDelta !== 0n;
  const action = xpAuditPending.has(parent) || xpAuditInconsistent.has(parent) ? 'defer' : earnedDelta < 0n || manualDelta < 0n && !totals.baseline ? 'review' : changed ? 'restore' : 'ok';
  xpAuditRows.push({ guildId: xpAuditId(member.guild_id), userId: xpAuditId(member.user_id), name: member.display_name || '', scope: seasonId || 'lifetime',
    currentMember: member.is_current_member !== false, action, storedTotal: xpAuditFormat(xpAuditDecimal(member.total_xp)), expectedTotal: xpAuditFormat(expected),
    totalDifference: xpAuditFormat(totalDelta), earnedDifference: xpAuditFormat(earnedDelta), manualDifference: xpAuditFormat(manualDelta), voiceDifference: xpAuditFormat(voiceDelta),
    hasManualBaseline: totals.baseline });
};
for (const member of xpAuditMembers) xpAuditCompare(member);
const xpAuditActive = db.guild_seasons.find({ ...xpAuditGuildFilter, status: 'Active' }, { _id: 1 }).maxTimeMS(15000).toArray().map(x => x._id);
const xpAuditSeasonMembers = xpAuditActive.length ? db.season_member_xp.find({ season_id: { $in: xpAuditActive } }).maxTimeMS(15000).toArray() : [];
for (const member of xpAuditSeasonMembers) xpAuditCompare(member, xpAuditId(member.season_id));
const xpAuditSummary = xpAuditRows.reduce((counts, row) => { counts[row.action] = (counts[row.action] || 0) + 1; return counts; }, {});
const xpAuditOutput = { readOnly: true, observedAtUtc: new Date().toISOString(), note: 'Observational dry run; concurrent activity can require another pass. Closed seasons remain frozen.',
  counts: { members: xpAuditMembers.length, ledger: xpAuditLedger.length, voiceDays: xpAuditDays.length, legacyEntries: xpAuditLegacyEntries, activeSeasonMembers: xpAuditSeasonMembers.length },
  compressedVoiceAuthoritative: xpAuditCompressed, summary: xpAuditSummary, differences: xpAuditRows.filter(x => x.action !== 'ok'), rows: xpAuditRows };
if (process.env.RANKOON_XP_OUTPUT) xpAuditFs.writeFileSync(process.env.RANKOON_XP_OUTPUT, JSON.stringify(xpAuditOutput, null, 2));
print(JSON.stringify({ counts: xpAuditOutput.counts, summary: xpAuditSummary, differences: xpAuditOutput.differences }, null, 2));
