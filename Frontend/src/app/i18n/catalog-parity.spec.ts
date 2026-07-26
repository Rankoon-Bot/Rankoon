import { KNOWN_API_ERROR_KEYS } from '../models/api-error.model';
import { SUPPORTED_LOCALES } from './locale.service';

describe('translation catalogs', () => {
  const expectedKeyCount = 1412;
  const namespaces = [
    'activity',
    'analytics',
    'app',
    'apiErrors',
    'authCallback',
    'botManagement',
    'channelPicker',
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
    'saveBar',
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

  it('keeps every consolidated language catalog in parity', async () => {
    const catalogs = await Promise.all(
      SUPPORTED_LOCALES.map((language) => catalog(language)),
    );
    const englishKeys = flatten(catalogs[0]).sort();
    for (const translation of catalogs) {
      expect(flatten(translation).sort()).toEqual(englishKeys);
      expect(flatten(translation)).toHaveSize(expectedKeyCount);
      expect(Object.keys(translation).sort()).toEqual([...namespaces].sort());
    }
  });

  it('contains every frontend-known API error key', async () => {
    for (const lang of SUPPORTED_LOCALES) {
      const keys = flatten(await catalog(lang));
      for (const errorKey of KNOWN_API_ERROR_KEYS)
        expect(keys).toContain(`apiErrors.${errorKey}`);
      expect(keys).not.toContain('auth.logoutSucceeded');
    }
  });

  it('contains server booster settings copy in every language', async () => {
    const requiredKeys = [
      'xp.boosterTitle', 'xp.boosterDescription', 'xp.boosterAddTier', 'xp.boosterRemove',
      'xp.boosterThresholdHint', 'xp.boosterLastTierHint', 'xp.boosterMonthsValidation',
      'xp.boosterDuplicateValidation', 'xp.boosterMultiplierValidation', 'xp.boosterOrderValidation'
    ];
    for (const lang of SUPPORTED_LOCALES) {
      const keys = flatten(await catalog(lang));
      for (const key of requiredKeys) expect(keys).toContain(key);
    }
  });
});
