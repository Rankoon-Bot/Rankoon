# Discord command review

Run against a test Discord server after starting the updated bot. Schema version 3
registers `/voice status|name|limit|kick|transfer` for platform and custom bots.
Old `action` options are still handled while global commands propagate.

- Check `/help`, `/rank` and `/leaderboard` in Discord desktop and mobile, including
  a new member, an empty leaderboard and ten long display names. Cards should fit
  without clipped fields. Rank must include a textual percentage as well as its bar.
- Navigate the `/help` dashboard link with the keyboard. Confirm only the invoking
  member sees the response and labels cannot trigger mentions.
- Join a configured voice hub, run `/voice status`, rename the channel and set its
  limit to 0 and 99. Check that confirmations describe the resulting state.
- Try a blank/overlong name, a negative/100 limit, a missing target, yourself and a
  member in another channel. None should modify a channel or disconnect anyone.
- Issue two voice mutations concurrently. One should finish while the other gets
  a clear running-action response. Repeat after completion to check lock release.
- Transfer to another connected member. Confirm the old owner is rejected on the
  next mutation and the new owner can use `/voice status` and rename the channel.
- Remove the bot's channel-edit or move-member permission and try the corresponding
  action. Expect a permission recovery card, no false success and a failed report.
- Delay or interrupt database/Discord calls. Commands should acknowledge immediately,
  show a recovery message on failure and never repeat a mutation automatically.

These checks require a connected bot; unit tests do not replace Discord visual QA.
