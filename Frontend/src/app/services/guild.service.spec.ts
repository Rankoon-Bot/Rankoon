import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { GuildService } from './guild.service';

describe('GuildService permissions API', () => {
  let service: GuildService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(GuildService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads guild capabilities', () => {
    service.capabilities('guild-1').subscribe(response => expect(response.moduleIds).toEqual(['xp']));
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`);
    expect(request.request.method).toBe('GET');
    request.flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });
  });

  it('loads and saves role permissions', () => {
    service.rolePermissions('guild-1').subscribe();
    const getRequest = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(getRequest.request.method).toBe('GET');
    getRequest.flush({ guildId: 'guild-1', isOwner: true, modules: [], roles: [], updatedAt: null });

    const body = { roles: [{ roleId: 'role-1', moduleIds: ['xp' as const] }] };
    service.saveRolePermissions('guild-1', body).subscribe();
    const putRequest = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(putRequest.request.method).toBe('PUT');
    expect(putRequest.request.body).toEqual(body);
    putRequest.flush({ guildId: 'guild-1', isOwner: true, modules: [], roles: [], updatedAt: null });
  });
});
