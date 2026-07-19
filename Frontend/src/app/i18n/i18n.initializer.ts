import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { LocaleService } from './locale.service';

export async function initializeTranslations(locale: LocaleService, transloco: TranslocoService): Promise<void> {
  const initialLocale = locale.locale();
  transloco.setActiveLang(initialLocale);
  await firstValueFrom(transloco.load(initialLocale));
}
