import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
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
});
