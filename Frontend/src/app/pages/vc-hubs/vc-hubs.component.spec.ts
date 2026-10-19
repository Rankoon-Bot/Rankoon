import { apiErrorInterceptor } from '../../interceptors/api-error.interceptor';
import { withInterceptors, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { VcHubsComponent } from './vc-hubs.component';
import { ToastService } from '../../services/toast.service';

describe('VcHubsComponent', () => {
  const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [VcHubsComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    TestBed.inject(AppStore).setSelectedGuild(guild);
  });

  it('preserves the literal username token in localized templates and the saved payload', () => {
    const component = TestBed.createComponent(VcHubsComponent).componentInstance;
    component.newHub();
    expect(component.hubs()[0].nameTemplate).toBe("{username}'s channel");

    component.save(component.hubs()[0]);
    const request = TestBed.inject(HttpTestingController).expectOne(`${environment.apiBaseUrl}/guilds/guild-1/vc-hubs`);
    expect(request.request.body.nameTemplate).toBe("{username}'s channel");
    request.flush({ ...component.hubs()[0], id: 'hub-1' });

    const transloco = TestBed.inject(TranslocoService);
    transloco.setActiveLang('de');
    component.newHub();
    expect(component.hubs()[1].nameTemplate).toBe('{username}s Kanal');
    transloco.setActiveLang('en');
  });

  it('exposes backend load and delete failures', () => {
    const component = TestBed.createComponent(VcHubsComponent).componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.load();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`).flush({ roles: [], channels: [] });
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/vc-hubs`).flush({ message: 'Hub data unavailable' }, { status: 500, statusText: 'Server Error' });
    expect(component.error()).toBe('Hub data unavailable');

    const hub = { id: 'hub-1', joinChannelId: 1, hubChannelName: 'Hub', categoryId: null, nameTemplate: '{username}', userLimit: 0, bitrate: 64000, maxChannelsPerOwner: 1, enabled: true };
    component.hubs.set([hub]);
    component.remove(hub);
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/vc-hubs/hub-1`).flush({ message: 'Delete denied' }, { status: 403, statusText: 'Forbidden' });
    expect(TestBed.inject(ToastService).toasts()).toEqual([jasmine.objectContaining({ message: 'Delete denied', type: 'error' })]);
    expect(component.hubs()).toEqual([hub]);
  });

  it('removes a new unsaved hub when changes are reset', () => {
    const component = TestBed.createComponent(VcHubsComponent).componentInstance;
    component.newHub();

    expect(component.dirty()).toBeTrue();
    expect(component.hubs()).toHaveSize(1);

    component.reset();

    expect(component.hubs()).toEqual([]);
    expect(component.editor()).toBeNull();
    expect(component.dirty()).toBeFalse();
  });

  it('restores an existing hub to its loaded baseline', () => {
    const component = TestBed.createComponent(VcHubsComponent).componentInstance;
    const hub = { id: 'hub-1', joinChannelId: 1, hubChannelName: 'Hub', categoryId: null, nameTemplate: '{username}', userLimit: 0, bitrate: 64000, maxChannelsPerOwner: 1, enabled: true };
    component.hubs.set([hub]);
    component.edit(hub);
    component.editor()!.nameTemplate = 'Changed';

    expect(component.dirty()).toBeTrue();
    component.reset();

    expect(component.editor()?.nameTemplate).toBe('{username}');
    expect(component.hubs()[0].nameTemplate).toBe('{username}');
    expect(component.dirty()).toBeFalse();
  });
});

describe('Voice hub delete error notification integration', () => {
  it('shows one actionable localized toast and retains the hub and unsaved editor', () => {
    TestBed.configureTestingModule({
      imports: [VcHubsComponent, testI18n],
      providers: [provideHttpClient(withInterceptors([apiErrorInterceptor])), provideHttpClientTesting()]
    });
    TestBed.inject(AppStore).setSelectedGuild({ id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' });
    const i18n = TestBed.inject(TranslocoService);
    i18n.setTranslation({ apiErrors: { discord: { missingAccess: 'Der Bot hat keinen Zugriff. Prüfe die Kanalberechtigungen.' } } }, 'de', { merge: true });
    i18n.setActiveLang('de');
    const component = TestBed.createComponent(VcHubsComponent).componentInstance;
    const hub = { id: 'hub-1', joinChannelId: 1, hubChannelName: 'Hub', categoryId: null, nameTemplate: '{username}', userLimit: 0, bitrate: 64000, maxChannelsPerOwner: 1, enabled: true };
    component.hubs.set([hub]);
    component.edit(hub);
    component.editor()!.nameTemplate = 'Unsaved draft';
    component.remove(hub);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(environment.apiBaseUrl + '/guilds/guild-1/vc-hubs/hub-1').flush({ errorKey: 'discord.missingAccess', message: 'English fallback', traceId: 'delete-hub-trace' }, { status: 422, statusText: 'Unprocessable Entity' });
    expect(TestBed.inject(ToastService).toasts()).toEqual([jasmine.objectContaining({ message: 'Der Bot hat keinen Zugriff. Prüfe die Kanalberechtigungen.', type: 'error' })]);
    expect(component.hubs()).toEqual([hub]);
    expect(component.editor()!.nameTemplate).toBe('Unsaved draft');
    expect(component.dirty()).toBeTrue();
    http.verify();
    i18n.setActiveLang('en');
  });
});
