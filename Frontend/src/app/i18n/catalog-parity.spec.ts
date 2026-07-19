import { KNOWN_API_ERROR_KEYS } from '../models/api-error.model';

describe('translation catalogs', () => {
  const scopes = [
    'core',
    'auth',
    'navigation',
    'server-selection',
    'dashboard',
    'voice-hubs',
    'leaderboard',
    'leaderboard-settings',
    'role-permissions',
    'reporting',
    'xp',
  ];
  const flatten = (value: unknown, prefix = ''): string[] =>
    Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => {
      const path = prefix ? `${prefix}.${key}` : key;
      return child && typeof child === 'object' ? flatten(child, path) : [path];
    });

  const catalog = (scope: string, lang: string) =>
    fetch(
      scope === 'core'
        ? `/assets/i18n/${lang}.json`
        : `/assets/i18n/${scope}/${lang}.json`,
    ).then((response) => response.json());

  it('keeps English and German keys in parity for every catalog', async () => {
    for (const scope of scopes) {
      const [en, de] = await Promise.all([
        catalog(scope, 'en'),
        catalog(scope, 'de'),
      ]);
      expect(flatten(de).sort()).toEqual(flatten(en).sort());
    }
  });

  it('contains every frontend-known API error key across the scoped catalogs', async () => {
    for (const lang of ['en', 'de']) {
      const catalogs = await Promise.all(
        scopes.map((scope) => catalog(scope, lang)),
      );
      const keys = catalogs.flatMap((translation) => flatten(translation));
      for (const errorKey of KNOWN_API_ERROR_KEYS)
        expect(keys).toContain(`apiErrors.${errorKey}`);
      expect(keys).not.toContain('auth.logoutSucceeded');
    }
  });
});
