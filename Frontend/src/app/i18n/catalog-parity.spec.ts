import { KNOWN_API_ERROR_KEYS } from '../models/api-error.model';

describe('translation catalogs', () => {
  const expectedKeyCount = 1393;
  const namespaces = [
    'activity',
    'analytics',
    'app',
    'apiErrors',
    'authCallback',
    'botManagement',
    'commands',
    'common',
    'customBotIdentity',
    'dashboard',
    'devTools',
    'diagnostics',
    'domain',
    'errorLogs',
    'errors',
    'header',
    'landing',
    'language',
    'leaderboard',
    'leaderboardSettings',
    'levelUpAnnouncements',
    'login',
    'modules',
    'nav',
    'reports',
    'rolePermissions',
    'seasons',
    'selfRoles',
    'serverSelection',
    'voiceHubs',
    'xp',
    'xpAudit',
  ];
  const flatten = (value: unknown, prefix = ''): string[] =>
    Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => {
      const path = prefix ? `${prefix}.${key}` : key;
      return child && typeof child === 'object' ? flatten(child, path) : [path];
    });

  const catalog = (lang: string) =>
    fetch(`/assets/i18n/${lang}.json`).then((response) => response.json());

  it('keeps the consolidated English and German catalogs in parity', async () => {
    const [en, de] = await Promise.all([catalog('en'), catalog('de')]);
    expect(flatten(de).sort()).toEqual(flatten(en).sort());
    expect(flatten(en)).toHaveSize(expectedKeyCount);
    expect(Object.keys(en).sort()).toEqual(namespaces.sort());
  });

  it('contains every frontend-known API error key', async () => {
    for (const lang of ['en', 'de']) {
      const keys = flatten(await catalog(lang));
      for (const errorKey of KNOWN_API_ERROR_KEYS)
        expect(keys).toContain(`apiErrors.${errorKey}`);
      expect(keys).not.toContain('auth.logoutSucceeded');
    }
  });

  it('contains English and German server booster settings copy', async () => {
    const requiredKeys = [
      'xp.boosterTitle', 'xp.boosterDescription', 'xp.boosterAddTier', 'xp.boosterRemove',
      'xp.boosterThresholdHint', 'xp.boosterLastTierHint', 'xp.boosterMonthsValidation',
      'xp.boosterDuplicateValidation', 'xp.boosterMultiplierValidation', 'xp.boosterOrderValidation'
    ];
    for (const lang of ['en', 'de']) {
      const keys = flatten(await catalog(lang));
      for (const key of requiredKeys) expect(keys).toContain(key);
    }
  });
});
