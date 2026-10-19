# Manual Test Plan

Use a disposable Discord guild, two non-bot test members, and an administrator.

## Authentication

1. Log in with `returnUrl=/dashboard`; verify the callback sets HttpOnly cookies
   and returns to `/dashboard` without credentials in the URL or session response.
2. Try `https://example.com`, `//example.com`, and `/\\example` return URLs;
   verify no external `return_url` is returned.
3. Submit expired, altered, and reused OAuth states; verify the OAuth-failure
   redirect.
4. Refresh once; verify the old token fails. Retry that old token and verify the
   replacement token also fails because its family was revoked.
5. Log out and verify the current token family cannot refresh and cookies are cleared.
6. With browser session cookies present, send a state-changing API request without
   a valid CSRF header; verify `auth.forbidden`. Bootstrap `/api/auth/csrf` and
   retry with its request token; verify the legitimate request can proceed.

## XP, Ledger, And Voice

1. Send a qualifying message and replay its event if possible; verify one ledger
   grant and one total increase.
2. Send qualifying messages, reactions, and thread messages inside cooldowns;
   verify no XP increase. Wait for each cooldown and verify the next event awards.
3. Enable reaction reversal, add then remove a reaction, and verify one reversal.
   Repeat with scheduled-event interest.
4. Join voice past the minimum duration; verify fractional XP and voice seconds.
   Verify configured AFK, deafened, excluded role/channel/category, and
   single-human cases do not qualify.
5. Keep an eligible voice session across a season boundary; verify distinct activity
   segments are attributed to the applicable seasons.
6. Restart after a pending ledger entry is created; verify repair completes it
   without duplicate XP.

## Seasons

1. Configure scheduled seasons and a prepared count. Verify saving settings alone
   does not create seasons. Explicitly create drafts or enable bounded automatic
   planning; verify non-overlapping seasons are created with settings snapshots.
2. Activate with zero, lifetime, and lifetime-percentage baselines; verify totals.
3. Close a season, alter lifetime XP, and verify final standings remain frozen.
4. Configure carry-over; verify the following season applies it once and respects
   the maximum.
5. Cancel and resume an eligible season; verify invalid transitions are rejected.

## Voice Hubs

1. Join an enabled hub and verify a recorded temporary channel is created and the
   member is moved.
2. Verify name tokens, category, bitrate, limit, and per-owner channel maximum.
3. Test `/voice` rename, limit, kick, and transfer as owner; verify non-owners fail.
4. Empty a temporary channel and verify deletion. Delete a hub channel and verify a
   managed replacement is reconciled.

## Self-Role Panels

1. Create a panel with a manageable role and unicode or available custom emoji;
   verify publication and seeded reactions.
2. Add then remove a mapped reaction; verify role assignment and removal.
3. Give the role outside Rankoon, remove the reaction, and verify Rankoon preserves
   the external role.
4. Map one role twice; remove one reaction and verify it stays until the final
   Rankoon assignment is removed.
5. Test an unmanageable role and unavailable custom emoji; verify validation or
   repair fails. Reconnect the bot and verify panel reconciliation.
