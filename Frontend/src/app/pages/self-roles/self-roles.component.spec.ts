import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import en from '../../../assets/i18n/en.json';
import de from '../../../assets/i18n/de.json';
import { SelfRolePanel } from '../../services/guild.service';
import { SelfRolesComponent } from './self-roles.component';

describe('SelfRolesComponent health display', () => {
  const panel: SelfRolePanel = {
    id: 'panel-1', channelId: '1', enabled: true, revision: 1,
    embeds: [{ kind: 'RoleMappings', title: 'Geschlecht', description: '', color: '#5865F2', fields: [] }], mappings: [],
    state: 'Unknown', lastError: 'selfRoles.healthDetails.checkPermissions'
  };

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [SelfRolesComponent, TranslocoTestingModule.forRoot({
        langs: { en, de }, translocoConfig: { availableLangs: ['en', 'de'], defaultLang: 'de' }, preloadLangs: true
      })],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
  });

  it('shows an inconclusive status and a translated recovery step without raw error keys', () => {
    const fixture = TestBed.createComponent(SelfRolesComponent);
    fixture.componentInstance.panels.set([panel]);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.state')?.textContent).toContain('Status unklar');
    expect(element.querySelector('.health-detail')?.textContent).toContain('Nachrichtenverlauf anzeigen');
    expect(element.textContent).not.toContain('selfRoles.healthDetails');
  });

  for (const width of [360, 1280]) {
    it(`fits the health message at ${width}px and keeps actions focusable`, () => {
      const fixture = TestBed.createComponent(SelfRolesComponent);
      fixture.nativeElement.style.display = 'block';
      fixture.nativeElement.style.width = `${width}px`;
      fixture.componentInstance.panels.set([panel]);
      fixture.detectChanges();
      const article = fixture.nativeElement.querySelector('.panel-summary') as HTMLElement;
      expect(article.scrollWidth).toBeLessThanOrEqual(article.clientWidth + 1);
      const repair = article.querySelector('.repair') as HTMLButtonElement;
      repair.focus();
      expect(document.activeElement).toBe(repair);
      expect(repair.getAttribute('aria-label')).toContain('Geschlecht');
    });
  }
});
