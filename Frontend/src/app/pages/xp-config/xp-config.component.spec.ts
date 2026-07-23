import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { testI18n } from '../../testing/i18n-testing';
import { AppStore, Guild } from '../../store/app.store';
import { XpConfig } from '../../services/guild.service';
import { XpConfigComponent } from './xp-config.component';

describe('XpConfigComponent server booster settings', () => {
  const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };
  const createConfig = (): XpConfig => ({
    enabled: true,
    message: { enabled: true, minimumPoints: 5, maximumPoints: 10, minimumCharacters: 1, maximumCharacters: 500, cooldownSeconds: 60 },
    voice: { enabled: true, pointsPerMinute: 10, minimumSessionSeconds: 60, requireMultipleHumans: true, excludeAfkChannel: true },
    reaction: { enabled: true, points: 2, cooldownSeconds: 30, reverseOnRemove: true },
    eventInterest: { enabled: true, points: 10 },
    thread: { enabled: true, createPoints: 15, messagePoints: 5, cooldownSeconds: 60 },
    excludedChannelIds: [], excludedCategoryIds: [], excludedRoleIds: [], channelMultipliers: [],
    serverBooster: { enabled: false, tiers: [{ minimumBoostMonths: 4, multiplier: 1.75 }, { minimumBoostMonths: 0, multiplier: 1.25 }] },
    levelRoles: [], levelUpChannelId: null
  });

  let fixture: ComponentFixture<XpConfigComponent>;
  let component: XpConfigComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [XpConfigComponent, testI18n], providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(TranslocoService).setActiveLang('en');
    TestBed.inject(AppStore).setSelectedGuild(guild);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(XpConfigComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/config`).flush(createConfig());
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`).flush({ roles: [], channels: [] });
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`).flush({ guildId: 'guild-1', state: 'Stopped', lastRunAt: null, lastPersistenceAt: null, connectedUsers: 0, eligibleUsers: 0, excludedUsers: 0, lastError: null, intervalSeconds: 30 });
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/leaderboard`).flush([]);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('starts disabled and hides tiers while retaining and sorting them', () => {
    expect(component.config()!.serverBooster.enabled).toBeFalse();
    expect(component.config()!.serverBooster.tiers.map(tier => tier.minimumBoostMonths)).toEqual([0, 4]);
    expect(fixture.nativeElement.querySelector('.booster-row')).toBeNull();
    expect(component.dirty()).toBeFalse();
  });

  it('shows and hides tier management without deleting tiers', () => {
    component.config()!.serverBooster.enabled = true;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.booster-row').length).toBe(2);
    component.config()!.serverBooster.enabled = false;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.booster-row')).toBeNull();
    expect(component.config()!.serverBooster.tiers.length).toBe(2);
  });

  it('adds a sensible unique tier and removes it again', () => {
    component.addBoosterTier();
    const added = component.config()!.serverBooster.tiers.at(-1)!;
    expect(added).toEqual({ minimumBoostMonths: 6, multiplier: 1.75 });
    component.removeBoosterTier(added);
    expect(component.config()!.serverBooster.tiers.length).toBe(2);
  });

  it('sorts edited tiers automatically', () => {
    component.config()!.serverBooster.tiers[0].minimumBoostMonths = 8;
    component.sortBoosterTiers();
    expect(component.config()!.serverBooster.tiers.map(tier => tier.minimumBoostMonths)).toEqual([4, 8]);
  });

  it('detects duplicate month thresholds', () => {
    const config = component.config()!;
    config.serverBooster.tiers[1].minimumBoostMonths = 0;
    expect(component.boosterTierErrors(config, config.serverBooster.tiers[0])).toContain('xp.boosterDuplicateValidation');
    expect(component.isValid(config)).toBeFalse();
  });

  it('rejects multipliers below one and accepts exactly one', () => {
    const config = component.config()!;
    config.serverBooster.tiers[0].multiplier = 0.99;
    expect(component.isValid(config)).toBeFalse();
    config.serverBooster.tiers[0].multiplier = 1;
    expect(component.boosterTierErrors(config, config.serverBooster.tiers[0])).not.toContain('xp.boosterMultiplierValidation');
    expect(component.isValid(config)).toBeTrue();
  });

  it('detects decreasing multipliers', () => {
    const config = component.config()!;
    config.serverBooster.tiers[0].multiplier = 1.75;
    config.serverBooster.tiers[1].multiplier = 1.25;
    expect(component.boosterTierErrors(config, config.serverBooster.tiers[1])).toContain('xp.boosterOrderValidation');
  });

  it('sends sorted booster tiers in the XP settings payload', () => {
    const config = component.config()!;
    config.serverBooster.enabled = true;
    config.serverBooster.tiers.reverse();
    component.save();
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/config`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.serverBooster).toEqual({ enabled: true, tiers: [{ minimumBoostMonths: 0, multiplier: 1.25 }, { minimumBoostMonths: 4, multiplier: 1.75 }] });
    request.flush(config);
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`).flush({});
  });

  it('integrates booster changes with dirty state and reset', () => {
    component.config()!.serverBooster.enabled = true;
    component.config()!.serverBooster.tiers[0].multiplier = 1.5;
    expect(component.dirty()).toBeTrue();
    component.reset();
    expect(component.dirty()).toBeFalse();
    expect(component.config()!.serverBooster.enabled).toBeFalse();
    expect(component.config()!.serverBooster.tiers[0].multiplier).toBe(1.25);
  });
});
