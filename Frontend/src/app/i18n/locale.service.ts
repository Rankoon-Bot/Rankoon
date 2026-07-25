import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

export const SUPPORTED_LOCALES = ['en', 'de', 'es', 'pt', 'fr', 'it'] as const;
export type AppLocale = (typeof SUPPORTED_LOCALES)[number];
export const LOCALE_STORAGE_KEY = 'rankoon_locale';

export function resolveLocale(persisted: string | null, browserLocales: readonly string[]): AppLocale {
  const normalize = (value: string): AppLocale | null => {
    const language = value.toLowerCase().split(/[-_]/)[0];
    return SUPPORTED_LOCALES.includes(language as AppLocale)
      ? (language as AppLocale)
      : null;
  };
  const persistedLocale = persisted ? normalize(persisted) : null;
  if (persistedLocale) return persistedLocale;
  return browserLocales.map(normalize).find((locale): locale is AppLocale => locale !== null) ?? 'en';
}

@Injectable({ providedIn: 'root' })
export class LocaleService {
  private readonly transloco = inject(TranslocoService);
  private readonly document = inject(DOCUMENT);
  private readonly activeLocale = signal<AppLocale>(this.initialLocale());
  readonly locale = this.activeLocale.asReadonly();

  constructor() {
    // Set the persisted/browser locale before subscribing. langChanges$ emits the
    // current Transloco default immediately and would otherwise reset it to English.
    this.transloco.setActiveLang(this.activeLocale());
    this.transloco.langChanges$.subscribe(lang => {
      const locale = this.normalize(lang) ?? 'en';
      this.activeLocale.set(locale);
      this.document.documentElement.lang = locale;
    });
    this.transloco.selectTranslate('app.title').subscribe(title => this.document.title = title);
  }

  setLocale(locale: AppLocale): void {
    if (locale === this.activeLocale()) return;
    localStorage.setItem(LOCALE_STORAGE_KEY, locale);
    this.transloco.setActiveLang(locale);
  }

  number(value: string | number, options?: Intl.NumberFormatOptions): string {
    return new Intl.NumberFormat(this.intlLocale(), options).format(Number(value));
  }

  date(value: string | Date, options: Intl.DateTimeFormatOptions): string {
    return new Intl.DateTimeFormat(this.intlLocale(), options).format(new Date(value));
  }

  plural(value: string | number, oneKey: string, otherKey: string, params: Record<string, string | number> = {}): string {
    const count = Number(value);
    const key = new Intl.PluralRules(this.intlLocale()).select(count) === 'one' ? oneKey : otherKey;
    return this.transloco.translate(key, { ...params, count: this.number(count) });
  }

  private initialLocale(): AppLocale {
    const persisted = localStorage.getItem(LOCALE_STORAGE_KEY);
    return resolveLocale(persisted, [...(navigator.languages ?? []), navigator.language]);
  }

  private normalize(value: string, fallback: AppLocale | null = 'en'): AppLocale | null {
    const language = value.toLowerCase().split(/[-_]/)[0];
    return SUPPORTED_LOCALES.includes(language as AppLocale)
      ? (language as AppLocale)
      : fallback;
  }

  private intlLocale(): string {
    return this.activeLocale() === 'pt' ? 'pt-PT' : this.activeLocale();
  }
}
