# Import existing XP

The dashboard imports leaderboard JSON from MEE6 and legacy Rankoon exports.
The backend detects the format from the JSON structure; no format selection is
required. A MEE6 export has this shape:

```json
{
  "guild": {
    "id": "123456789012345678"
  },
  "players": [
    {
      "id": "234567890123456789",
      "xp": 1234,
      "message_count": 56,
      "username": "Example member"
    }
  ]
}
```

Legacy Rankoon data is accepted as an array of exported user documents. Discord
snowflakes should be strings or MongoDB Extended JSON `$numberLong` values:

```json
[
  {
    "GuildId": { "$numberLong": "123456789012345678" },
    "DiscordUserId": { "$numberLong": "234567890123456789" },
    "UserName": "Example member",
    "TotalWrittenMessages": 321,
    "TotalVoiceChatSeconds": { "$numberDouble": "48.4343059" },
    "ExtraPoints": 13001,
    "VcPoints": 111,
    "VcPointCent": 25,
    "MessagePoints": 1078,
    "ReactionPoints": 12,
    "EventInterestPoints": 0,
    "StagePoints": 0,
    "Mee6Points": 23020,
    "NegativePoints": 26000
  }
]
```

Custom XP is calculated exactly as `ExtraPoints + VcPoints +
(VcPointCent / 100) + MessagePoints + ReactionPoints + EventInterestPoints +
StagePoints + Mee6Points - NegativePoints`. Missing point fields are zero.
Files may contain several guilds; Rankoon imports only valid records matching
the selected Discord server and reports skipped foreign, invalid, and duplicate
records. The last valid duplicate for a user wins.

Each re-import replaces only the external imported XP basis. XP earned in
Rankoon, manual adjustments, leaderboard privacy, membership state, seasons,
and ledger history remain intact. MEE6 sets message counts but leaves existing
voice time unchanged. Legacy Rankoon imports both `TotalWrittenMessages` and
`TotalVoiceChatSeconds`. Imported data never generates XP ledger entries,
level-transition events, or level-up announcements.

The dashboard uses `POST /api/guilds/{guildId}/xp/import`. The previous
`POST /api/guilds/{guildId}/xp/import/mee6` route remains a compatible alias and
also accepts both formats. Rankoon does not fetch data directly from MEE6.
