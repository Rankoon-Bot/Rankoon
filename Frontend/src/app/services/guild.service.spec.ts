import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { GuildService, SeasonSettings, SelfRolePanel } from './guild.service';

describe('GuildService permissions API', () => {
  let service: GuildService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(GuildService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    service.invalidateResourceCache();
    http.verify();
  });

  it('loads guild capabilities', () => {
    service.capabilities('guild-1').subscribe(response => expect(response.moduleIds).toEqual(['xp']));
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/capabilities`);
    expect(request.request.method).toBe('GET');
    request.flush({ guildId: 'guild-1', isOwner: false, canAccessSettings: true, moduleIds: ['xp'], leaderboardAlias: 'guild-one' });
  });

  it('coalesces and expires guild resource requests', fakeAsync(() => {
    const first = jasmine.createSpy('first');
    const second = jasmine.createSpy('second');

    service.resources('guild-1').subscribe(first);
    service.resources('guild-1').subscribe(second);
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`);
    request.flush({ roles: [], channels: [] });

    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1);
    service.resources('guild-1').subscribe();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/resources`);

    tick(60_001);
    service.resources('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`).flush({ roles: [], channels: [] });
  }));

  it('invalidates both guild resource caches on demand', () => {
    service.resources('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`).flush({ roles: [], channels: [] });
    service.selfRoleResources('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-resources`).flush({ roles: [], channels: [], emojis: [] });

    service.invalidateResourceCache('guild-1');
    service.resources('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`).flush({ roles: [], channels: [] });
    service.selfRoleResources('guild-1').subscribe();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-resources`).flush({ roles: [], channels: [], emojis: [] });
    expect(service).toBeTruthy();
  });

  it('posts MEE6 and Custom payloads unchanged to the generic XP import endpoint', () => {
    const mee6 = { guild: { id: '1' }, players: [{ id: '2', xp: 3 }] };
    const custom = [{ GuildId: { $numberLong: '1' }, DiscordUserId: { $numberLong: '2' }, MessagePoints: 3 }];
    for (const payload of [mee6, custom]) {
      service.importXpJson('guild-1', payload).subscribe();
      const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/import`);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toBe(payload);
      request.flush({ format: payload === mee6 ? 'Mee6' : 'CustomRankoon', imported: 1, skippedInvalid: 0, skippedForeignGuild: 0, duplicateUsers: 0 });
    }
  });

  it('loads and saves role permissions', () => {
    service.rolePermissions('guild-1').subscribe();
    const getRequest = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(getRequest.request.method).toBe('GET');
    getRequest.flush({ guildId: 'guild-1', isOwner: true, revision: 1, modules: [], roles: [], updatedAt: null });

    const body = { revision: 1, roles: [{ roleId: 'role-1', moduleIds: ['xp' as const] }] };
    service.saveRolePermissions('guild-1', body).subscribe();
    const putRequest = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(putRequest.request.method).toBe('PUT');
    expect(putRequest.request.body).toEqual(body);
    putRequest.flush({ guildId: 'guild-1', isOwner: true, revision: 2, modules: [], roles: [], updatedAt: null });
  });

  it('uses string IDs for season configuration and lifecycle endpoints', () => {
    const settings: SeasonSettings = {
      enabled: true, defaultLeaderboardScope: 'CurrentSeason', timeZoneId: 'Europe/Berlin', scheduleKind: 'Monthly', scheduleAnchorUtc: '2027-01-01T00:00:00.000Z', fixedDurationDays: null,
      gapDays: 0, preparedSeasonCount: 3, pauseBehavior: 'NoSeasonXp', publicHistoryCount: 3, initialXpMode: 'Zero', initialXpPercentage: 0, carryOverMode: 'None', carryOverPercentage: 0,
      carryOverMaximumXp: null, announcementChannelId: 'channel-1', announcements: { startEnabled: false, endEnabled: false, winnerEnabled: false, warningOffsetsMinutes: [] }, winnerCount: 3,
      nameTemplate: 'Season {number}', rotation: [], rotationOffset: 0, seasonLevelRoles: []
    };
    service.saveSeasonConfig('guild-1', settings).subscribe();
    const save = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/config`);
    expect(save.request.method).toBe('PUT');
    expect(save.request.body.announcementChannelId).toBe('channel-1');
    save.flush(settings);

    service.previewSeasons('guild-1', settings, 6).subscribe();
    const preview = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/preview?count=6`);
    expect(preview.request.method).toBe('POST');
    preview.flush([]);

    service.startSeason('guild-1', 'season-1').subscribe();
    const start = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/season-1/start`);
    expect(start.request.method).toBe('POST');
    start.flush({ id: 'season-1' });

    service.planSeasons('guild-1', 4).subscribe();
    const plan = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/plan`);
    expect(plan.request.method).toBe('POST');
    expect(plan.request.body).toEqual({ count: 4 });
    plan.flush([]);
  });

  it('sends scope and season ID with public leaderboard pagination', () => {
    service.publicLeaderboard('guild-one', 'cursor-1', true, 'Season', 'season-1').subscribe();
    const request = http.expectOne(`${environment.apiBaseUrl}/rankings/guild-one?take=25&cursor=cursor-1&aroundMe=true&scope=Season&seasonId=season-1`);
    expect(request.request.method).toBe('GET');
    request.flush({ guildName: 'Guild', alias: 'guild-one', visibility: 'Public', items: [], nextCursor: null, hasMore: false, isMember: true, publicVisible: true, scope: 'Season', seasonId: 'season-1', historicalSeasons: [] });
  });

  it('uses the self-role panel and resource endpoints', () => {
    const panel: SelfRolePanel = { channelId: 'channel-1', embeds: [{ kind: 'RoleMappings', title: 'Pick roles', description: '', color: '#5865F2', fields: [] }], enabled: true, mappings: [], revision: 0 };
    service.selfRolePanels('guild-1').subscribe();
    const list = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-panels`);
    expect(list.request.method).toBe('GET');
    list.flush([]);

    service.selfRoleResources('guild-1').subscribe();
    const resources = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-resources`);
    expect(resources.request.method).toBe('GET');
    resources.flush({ roles: [], channels: [], emojis: [] });

    service.createSelfRolePanel('guild-1', panel).subscribe();
    const create = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-panels`);
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual(panel);
    create.flush({ ...panel, id: 'panel-1' });

    service.updateSelfRolePanel('guild-1', { ...panel, id: 'panel-1' }).subscribe();
    const update = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/self-role-panels/panel-1`);
    expect(update.request.method).toBe('PUT');
    expect(update.request.body.embeds).toEqual(panel.embeds);
    update.flush({ ...panel, id: 'panel-1' });
  });
});
