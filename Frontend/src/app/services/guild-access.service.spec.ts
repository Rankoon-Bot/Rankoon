import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { environment } from '../../environments/environment';
import { AppStore, Guild } from '../store/app.store';
import { GuildAccessService } from './guild-access.service';

describe('GuildAccessService', () => {
  const guild: Guild = {
    id: 'guild-1', name: 'Guild One', icon: null, owner: false, permissions: '0',
    features: [], botInstalled: true, inviteUrl: ''
  };

  let service: GuildAccessService;
  let store: AppStore;
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    });
    service = TestBed.inject(GuildAccessService);
    store = TestBed.inject(AppStore);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  it('refreshes capabilities on selection and routes module users to the dashboard', () => {
    const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);

    service.selectAndNavigate(guild).subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`);
    request.flush({
      guildId: 'guild-1', isOwner: false, canAccessSettings: true,
      moduleIds: ['xp'], leaderboardAlias: 'guild-one'
    });

    expect(store.guildCapabilities()?.moduleIds).toEqual(['xp']);
    expect(navigate).toHaveBeenCalledWith(jasmine.objectContaining({}));
    const target = navigate.calls.mostRecent().args[0];
    expect(typeof target === 'string' ? target : router.serializeUrl(target)).toBe('/dashboard');
  });

  it('routes users without settings access to the public ranking', () => {
    const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);

    service.selectAndNavigate(guild).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`).flush({
      guildId: 'guild-1', isOwner: false, canAccessSettings: false,
      moduleIds: [], leaderboardAlias: 'guild-one'
    });

    const target = navigate.calls.mostRecent().args[0];
    expect(typeof target === 'string' ? target : router.serializeUrl(target)).toBe('/rankings/guild-one');
  });

  it('coalesces capability requests, caches successful responses, and expires them', fakeAsync(() => {
    const first = jasmine.createSpy('first');
    const second = jasmine.createSpy('second');

    service.loadCapabilities('guild-1').subscribe(first);
    service.loadCapabilities('guild-1').subscribe(second);
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`);
    request.flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });

    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1);
    service.loadCapabilities('guild-1').subscribe();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`);

    tick(60_001);
    service.loadCapabilities('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`).flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });
  }));

  it('invalidates cached capabilities when selecting another guild', () => {
    service.loadCapabilities('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`).flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });

    service.selectAndNavigate({ ...guild, id: 'guild-2' }).subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-2/capabilities`).flush({ guildId: 'guild-2', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-two' });

    service.loadCapabilities('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`).flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });
    expect(store.guildCapabilities()?.guildId).toBe('guild-2');
  });
});
