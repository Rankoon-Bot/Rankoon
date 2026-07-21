import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Translation, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';

export type TranslationScope =
  | 'auth'
  | 'landing'
  | 'navigation'
  | 'server-selection'
  | 'dashboard'
  | 'voice-hubs'
  | 'leaderboard'
  | 'leaderboard-settings'
  | 'role-permissions'
  | 'reporting'
  | 'self-roles'
  | 'xp'
  | 'xp-audit';

@Injectable({ providedIn: 'root' })
export class ModuleTranslationService {
  private readonly http = inject(HttpClient);
  private readonly transloco = inject(TranslocoService);
  private readonly requestedScopes = new Set<TranslationScope>();
  private readonly loads = new Map<string, Promise<void>>();
  private readonly loadedScopes = signal(new Set<string>());

  isLoaded(scope: TranslationScope): boolean {
    return this.loadedScopes().has(
      `${scope}/${this.transloco.getActiveLang()}`,
    );
  }

  constructor() {
    this.transloco.langChanges$.subscribe((lang) => {
      for (const scope of this.requestedScopes) void this.load(scope, lang);
    });
  }

  load(
    scope: TranslationScope,
    lang = this.transloco.getActiveLang(),
  ): Promise<void> {
    this.requestedScopes.add(scope);
    const key = `${scope}/${lang}`;
    const pending = this.loads.get(key);
    if (pending) return pending;

    const request = firstValueFrom(
      this.http.get<Translation>(`/assets/i18n/${scope}/${lang}.json`),
    )
      .then((translation) => {
        this.transloco.setTranslation(translation, lang, { merge: true });
        this.loadedScopes.update((scopes) => new Set(scopes).add(key));
      })
      .catch((error) => {
        this.loads.delete(key);
        throw error;
      });
    this.loads.set(key, request);
    return request;
  }
}
