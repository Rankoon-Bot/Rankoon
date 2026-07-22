import { provideHttpClient } from '@angular/common/http';
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
});
