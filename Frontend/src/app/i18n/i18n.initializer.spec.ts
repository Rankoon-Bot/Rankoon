import { Subject } from 'rxjs';
import { Translation, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from './locale.service';
import { initializeTranslations } from './i18n.initializer';

describe('initializeTranslations', () => {
  it('waits for the resolved initial catalog before completing', async () => {
    const catalog = new Subject<Translation>();
    const transloco = jasmine.createSpyObj<TranslocoService>('TranslocoService', ['setActiveLang', 'load']);
    transloco.load.and.returnValue(catalog);
    const locale = { locale: () => 'de' } as LocaleService;
    let completed = false;

    const initialization = initializeTranslations(locale, transloco).then(() => completed = true);
    await Promise.resolve();
    expect(transloco.setActiveLang).toHaveBeenCalledOnceWith('de');
    expect(completed).toBeFalse();

    catalog.next({ app: { title: 'Rankoon Kontrollzentrum' } });
    catalog.complete();
    await initialization;
    expect(completed).toBeTrue();
  });
});
