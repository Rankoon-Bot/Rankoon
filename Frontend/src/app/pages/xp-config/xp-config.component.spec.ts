import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import {
  ComponentFixture,
  TestBed,
  fakeAsync,
  flushMicrotasks,
} from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { testI18n } from '../../testing/i18n-testing';
import { AppStore, Guild } from '../../store/app.store';
import { XpConfig } from '../../services/guild.service';
import { ToastService } from '../../services/toast.service';
import { LocaleService } from '../../i18n/locale.service';
import { XpConfigComponent } from './xp-config.component';

describe('XpConfigComponent server booster settings', () => {
  const guild: Guild = {
    id: 'guild-1',
    name: 'Guild',
    icon: null,
    owner: true,
    permissions: '8',
    features: [],
    botInstalled: true,
    inviteUrl: '',
  };
  const createConfig = (): XpConfig => ({
    enabled: true,
    message: {
      enabled: true,
      minimumPoints: 5,
      maximumPoints: 10,
      minimumCharacters: 1,
      maximumCharacters: 500,
      cooldownSeconds: 60,
    },
    voice: {
      enabled: true,
      pointsPerMinute: 10,
      minimumSessionSeconds: 60,
      settingsVersion: 2,
      eligibility: {
        awardWhileSelfMuted: true,
        awardWhileSelfDeafened: true,
        awardWhileGuildMuted: true,
        awardWhileGuildDeafened: false,
        awardWhileSuppressed: true,
        awardInAfkChannel: false,
        minimumHumanParticipants: 2,
        participantCountingMode: 'EligibleHumansOnly',
        resetMinimumSessionWhenIneligible: true,
      },
    },
    reaction: {
      enabled: true,
      points: 2,
      cooldownSeconds: 30,
      reverseOnRemove: true,
    },
    eventInterest: { enabled: true, points: 10 },
    thread: {
      enabled: true,
      createPoints: 15,
      messagePoints: 5,
      cooldownSeconds: 60,
    },
    excludedChannelIds: [],
    excludedCategoryIds: [],
    excludedRoleIds: [],
    channelMultipliers: [],
    serverBooster: {
      enabled: false,
      tiers: [
        { minimumBoostMonths: 4, multiplier: 1.75 },
        { minimumBoostMonths: 0, multiplier: 1.25 },
      ],
    },
    levelRoles: [],
    levelUpChannelId: null,
  });

  let fixture: ComponentFixture<XpConfigComponent>;
  let component: XpConfigComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [XpConfigComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(TranslocoService).setActiveLang('en');
    TestBed.inject(AppStore).setSelectedGuild(guild);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(XpConfigComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/config`)
      .flush(createConfig());
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`)
      .flush({ roles: [], channels: [] });
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`)
      .flush({
        guildId: 'guild-1',
        state: 'Stopped',
        lastRunAt: null,
        lastPersistenceAt: null,
        connectedUsers: 0,
        eligibleUsers: 0,
        excludedUsers: 0,
        lastError: null,
        intervalSeconds: 30,
      });
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('applies visible eligibility presets as normal dirty changes and resets them', () => {
    component.applyVoicePreset('strict');
    const eligibility = component.config()!.voice.eligibility;
    expect(eligibility.minimumHumanParticipants).toBe(2);
    expect(eligibility.participantCountingMode).toBe('EligibleHumansOnly');
    expect(eligibility.awardWhileSelfMuted).toBeFalse();
    expect(eligibility.awardWhileSuppressed).toBeFalse();
    expect(component.dirty()).toBeTrue();

    component.reset();
    expect(component.config()!.voice.eligibility.awardWhileSelfMuted).toBeTrue();
    expect(component.dirty()).toBeFalse();
  });

  it('builds the live summary from the current form values', () => {
    component.applyVoicePreset('relaxed');
    const summary = component.voiceSummary(component.config()!);
    expect(summary).toContain('1 human');
    expect(summary).toContain('self-mute');
    expect(summary).toContain('accumulated');
  });

  it('validates participant limits and keeps eligibility visible while voice XP is disabled', () => {
    const config = component.config()!;
    config.voice.eligibility.minimumHumanParticipants = 100;
    config.voice.enabled = false;
    fixture.detectChanges();
    expect(component.isValid(config)).toBeFalse();
    expect(fixture.nativeElement.textContent).toContain('These rules are kept');
  });

  it('starts disabled and hides tiers while retaining and sorting them', () => {
    expect(component.config()!.serverBooster.enabled).toBeFalse();
    expect(
      component
        .config()!
        .serverBooster.tiers.map((tier) => tier.minimumBoostMonths),
    ).toEqual([0, 4]);
    expect(fixture.nativeElement.querySelector('.booster-row')).toBeNull();
    expect(component.dirty()).toBeFalse();
  });

  it('shows and hides tier management without deleting tiers', () => {
    component.config()!.serverBooster.enabled = true;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.booster-row').length).toBe(
      2,
    );
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
    expect(
      component
        .config()!
        .serverBooster.tiers.map((tier) => tier.minimumBoostMonths),
    ).toEqual([4, 8]);
  });

  it('detects duplicate month thresholds', () => {
    const config = component.config()!;
    config.serverBooster.tiers[1].minimumBoostMonths = 0;
    expect(
      component.boosterTierErrors(config, config.serverBooster.tiers[0]),
    ).toContain('xp.boosterDuplicateValidation');
    expect(component.isValid(config)).toBeFalse();
  });

  it('rejects multipliers below one and accepts exactly one', () => {
    const config = component.config()!;
    config.serverBooster.tiers[0].multiplier = 0.99;
    expect(component.isValid(config)).toBeFalse();
    config.serverBooster.tiers[0].multiplier = 1;
    expect(
      component.boosterTierErrors(config, config.serverBooster.tiers[0]),
    ).not.toContain('xp.boosterMultiplierValidation');
    expect(component.isValid(config)).toBeTrue();
  });

  it('detects decreasing multipliers', () => {
    const config = component.config()!;
    config.serverBooster.tiers[0].multiplier = 1.75;
    config.serverBooster.tiers[1].multiplier = 1.25;
    expect(
      component.boosterTierErrors(config, config.serverBooster.tiers[1]),
    ).toContain('xp.boosterOrderValidation');
  });

  it('saves the complete configuration, then reloads the voice tracking status', () => {
    const config = component.config()!;
    config.serverBooster.enabled = true;
    config.serverBooster.tiers.reverse();
    component.save();
    const request = http.expectOne(
      `${environment.apiBaseUrl}/guilds/guild-1/xp/config`,
    );
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.serverBooster).toEqual({
      enabled: true,
      tiers: [
        { minimumBoostMonths: 0, multiplier: 1.25 },
        { minimumBoostMonths: 4, multiplier: 1.75 },
      ],
    });
    request.flush(config);
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`)
      .flush({});
  });

  it('does not provide watchdog start or stop controls and changing Voice XP makes no request', () => {
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).not.toContain('VCWatchdog');
    expect(element.querySelector('.watchdog-card')).toBeNull();

    component.config()!.voice.enabled = false;
    fixture.detectChanges();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`);
  });

  it('keeps XP settings available when loading the voice tracking status fails', () => {
    component.load();
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/config`)
      .flush(createConfig());
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/resources`)
      .flush({ roles: [], channels: [] });
    http
      .expectOne(`${environment.apiBaseUrl}/guilds/guild-1/xp/watchdog`)
      .flush('unavailable', { status: 503, statusText: 'Unavailable' });

    fixture.detectChanges();
    expect(component.config()).not.toBeNull();
    expect(component.loadError()).toBe('');
    expect(component.watchdog()).toBeNull();
  });

  it('renders a positive voice tracking status for active XP and Voice XP', () => {
    expect(component.voiceStatusTone(component.watchdog())).toBe('neutral');
    component.watchdog.set({
      guildId: 'guild-1',
      state: 'Healthy',
      lastRunAt: null,
      lastPersistenceAt: null,
      connectedUsers: 0,
      eligibleUsers: 0,
      excludedUsers: 0,
      lastError: null,
      intervalSeconds: 30,
    });
    fixture.detectChanges();
    expect(component.voiceStatusTone(component.watchdog())).toBe('success');
    expect(
      fixture.nativeElement.querySelector('.voice-service-status').textContent,
    ).toContain('xp.voiceTrackingHealthy');
  });

  it('explains that configured Voice XP is paused while the XP system is disabled', () => {
    component.config()!.enabled = false;
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('.voice-service-status').textContent,
    ).toContain('xp.voiceXpPaused');
  });

  it('maps faulty watchdog states to friendly status copy instead of enum names', () => {
    component.watchdog.set({
      guildId: 'guild-1',
      state: 'Faulted',
      lastRunAt: null,
      lastPersistenceAt: null,
      connectedUsers: 0,
      eligibleUsers: 0,
      excludedUsers: 0,
      lastError: 'TimeoutException',
      intervalSeconds: 30,
    });
    fixture.detectChanges();
    const text = fixture.nativeElement.querySelector('.voice-service-status')
      .textContent as string;
    expect(text).toContain('xp.voiceTrackingImpaired');
    expect(text).not.toContain('Faulted');
  });

  it('retains validation for message ranges, channel multipliers and level roles', () => {
    const config = component.config()!;
    config.message.maximumPoints = config.message.minimumPoints - 1;
    expect(component.isValid(config)).toBeFalse();
    config.message.maximumPoints = 10;
    config.channelMultipliers.push({ channelId: '', multiplier: 1 });
    expect(component.isValid(config)).toBeFalse();
    config.channelMultipliers = [];
    config.levelRoles.push({ level: 0, roleId: '' });
    expect(component.isValid(config)).toBeFalse();
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

  it('imports JSON, reports the detected format and resets the input', fakeAsync(() => {
    const toast = TestBed.inject(ToastService);
    const locale = TestBed.inject(LocaleService);
    spyOn(toast, 'success');
    spyOn(toast, 'warning');
    const plural = spyOn(locale, 'plural').and.callFake(
      (value, one, other, params) =>
        `${other}:${value}:${params?.['format'] ?? ''}`,
    );
    const payload = [
      {
        GuildId: { $numberLong: '1' },
        DiscordUserId: { $numberLong: '2' },
        MessagePoints: 3,
      },
    ];
    const input = {
      files: [{ text: () => Promise.resolve(JSON.stringify(payload)) }],
      value: 'ranking.json',
    } as unknown as HTMLInputElement;

    component.importXpJson({ target: input } as unknown as Event);
    flushMicrotasks();
    const request = http.expectOne(
      `${environment.apiBaseUrl}/guilds/guild-1/xp/import`,
    );
    expect(request.request.body).toEqual(payload);
    request.flush({
      format: 'CustomRankoon',
      imported: 96,
      skippedInvalid: 1,
      skippedForeignGuild: 2,
      duplicateUsers: 1,
    });

    expect(toast.success).toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
    expect(plural).toHaveBeenCalledWith(
      96,
      'xp.importedOne',
      'xp.importedOther',
      { format: 'xp.importFormats.CustomRankoon' },
    );
    expect(input.value).toBe('');
  }));

  it('rejects syntactically invalid JSON without an HTTP import request', fakeAsync(() => {
    const toast = TestBed.inject(ToastService);
    spyOn(toast, 'error');
    const input = {
      files: [{ text: () => Promise.resolve('{invalid') }],
      value: 'invalid.json',
    } as unknown as HTMLInputElement;

    component.importXpJson({ target: input } as unknown as Event);
    flushMicrotasks();

    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/xp/import`);
    expect(toast.error).toHaveBeenCalled();
    expect(input.value).toBe('');
  }));

  it('keeps the import in a secondary data migration section', () => {
    const text = fixture.nativeElement.querySelector('.migration')
      .textContent as string;
    expect(text).not.toContain('MEE6 import');
    expect(
      fixture.nativeElement.querySelector('.migration h2').textContent,
    ).toContain('xp.jsonImport');
  });
});
