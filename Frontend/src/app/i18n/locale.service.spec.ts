import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { testI18n } from '../testing/i18n-testing';
import { LOCALE_STORAGE_KEY, LocaleService, resolveLocale } from './locale.service';

describe('LocaleService', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem(LOCALE_STORAGE_KEY, 'en');
    TestBed.configureTestingModule({ imports: [testI18n] });
  });

  afterEach(() => localStorage.clear());

  it('prefers the persisted locale over browser languages', () => {
    expect(resolveLocale('en', ['de-DE'])).toBe('en');
  });

  it('keeps the persisted locale when Transloco starts with the English default', () => {
    localStorage.setItem(LOCALE_STORAGE_KEY, 'de');

    const locale = TestBed.inject(LocaleService);
    const transloco = TestBed.inject(TranslocoService);

    expect(locale.locale()).toBe('de');
    expect(transloco.getActiveLang()).toBe('de');
    expect(document.documentElement.lang).toBe('de');

    transloco.setActiveLang('en');
  });

  it('uses a supported browser language and falls back to English', () => {
    expect(resolveLocale(null, ['fr-FR', 'de-AT'])).toBe('de');
    expect(resolveLocale(null, ['fr-FR'])).toBe('en');
    expect(resolveLocale('fr-FR', ['de-DE'])).toBe('de');
  });

  it('persists changes and updates document metadata and formatting', async () => {
    const locale = TestBed.inject(LocaleService);
    const transloco = TestBed.inject(TranslocoService);
    locale.setLocale('de');
    await transloco.load('de').toPromise();

    expect(localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('de');
    expect(document.documentElement.lang).toBe('de');
    expect(document.title).toBe('Rankoon Kontrollzentrum');
    expect(locale.number(1234.5)).toContain(',');
  });
});
