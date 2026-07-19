import { ApplicationConfig, inject, isDevMode, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideTransloco, TranslocoService } from '@jsverse/transloco';

import { routes } from './app.routes';
import { authInterceptor } from './interceptors/auth.interceptor';
import { TranslocoHttpLoader } from './i18n/transloco-http.loader';
import { initializeTranslations } from './i18n/i18n.initializer';
import { LocaleService } from './i18n/locale.service';
import { AuthService } from './services/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideTransloco({
      config: {
        availableLangs: ['en', 'de'],
        defaultLang: 'en',
        fallbackLang: 'en',
        reRenderOnLangChange: true,
        prodMode: !isDevMode()
      },
      loader: TranslocoHttpLoader
    }),
    provideAppInitializer(() => initializeTranslations(inject(LocaleService), inject(TranslocoService))),
    provideAppInitializer(() => inject(AuthService).initializeSession())
  ]
};
