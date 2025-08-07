export const environment = {
  production: true,
  discordClientId: 'YOUR_DISCORD_CLIENT_ID',
  // NIEMALS das Client Secret hier einfügen - das gehört nur ins Backend!
  discordRedirectUri: 'https://your-api-domain.com/auth/discord/callback', // Backend callback URL
  frontendCallbackUri: 'https://your-domain.com/auth/callback', // Frontend callback URL
  apiBaseUrl: '/api'
};
