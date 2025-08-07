export const environment = {
  production: false,
  discordClientId: '1402257269088850112',
  // NIEMALS das Client Secret hier einfügen - das gehört nur ins Backend!
  discordRedirectUri: 'http://localhost:5020/api/auth/callback', // Backend callback URL
  frontendCallbackUri: 'http://localhost:4200/auth/callback', // Frontend callback URL
  apiBaseUrl: 'http://localhost:5020/api'
};
