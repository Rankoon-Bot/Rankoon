import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog/confirmation-dialog.component';
import {
  Season,
  SeasonPreview,
  SeasonSettings,
} from '../../services/guild.service';
import { ToastService } from '../../services/toast.service';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import {
  defaultSeasonSettings,
  SeasonConfigComponent,
} from './season-config.component';

describe('SeasonConfigComponent', () => {
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
  const configUrl = `${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/config`;
  const seasonsUrl = `${environment.apiBaseUrl}/guilds/guild-1/xp/seasons`;
  const previewUrl = `${environment.apiBaseUrl}/guilds/guild-1/xp/seasons/preview?count=3`;
  const resourcesUrl = `${environment.apiBaseUrl}/guilds/guild-1/resources`;

  const createSettings = (
    overrides: Partial<SeasonSettings> = {},
  ): SeasonSettings => ({
    ...defaultSeasonSettings(),
    enabled: true,
    timeZoneId: 'UTC',
    scheduleAnchorUtc: '2030-01-01T00:00:00.000Z',
    fixedDurationDays: 30,
    ...overrides,
  });

  const createSeason = (
    sequence: number,
    status: Season['status'],
    overrides: Partial<Season> = {},
  ): Season => ({
    id: `season-${sequence}`,
    sequence,
    name: `Season ${sequence}`,
    description: null,
    status,
    startsAtUtc: '2030-01-01T00:00:00.000Z',
    endsAtUtc: '2030-01-31T00:00:00.000Z',
    carryOverApplied: false,
    finalized: false,
    ...overrides,
  });

  const previews: SeasonPreview[] = [
    {
      sequence: 1,
      name: 'Season 1',
      startsAtUtc: '2030-01-01T00:00:00.000Z',
      endsAtUtc: '2030-01-31T00:00:00.000Z',
    },
    {
      sequence: 2,
      name: 'Season 2',
      startsAtUtc: '2030-01-31T00:00:00.000Z',
      endsAtUtc: '2030-03-02T00:00:00.000Z',
    },
  ];

  let fixture: ComponentFixture<SeasonConfigComponent>;
  let component: SeasonConfigComponent;
  let http: HttpTestingController;

  function flushInitialLoad(
    settings = createSettings(),
    seasons: Season[] = [
      createSeason(1, 'Active'),
      createSeason(2, 'Scheduled'),
    ],
  ): void {
    http.expectOne(configUrl).flush(settings);
    http.expectOne(seasonsUrl).flush(seasons);
    http.expectOne(resourcesUrl).flush({ roles: [], channels: [] });
    http.expectOne(previewUrl).flush(previews);
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SeasonConfigComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(TranslocoService).setActiveLang('en');
    TestBed.inject(AppStore).setSelectedGuild(guild);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SeasonConfigComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('loads existing settings and renders active and scheduled season state', () => {
    flushInitialLoad();

    const element = fixture.nativeElement as HTMLElement;
    expect(component.loading()).toBeFalse();
    expect(component.dirty()).toBeFalse();
    expect(
      element.querySelector('.status-card')?.getAttribute('data-status'),
    ).toBe('active');
    expect(element.querySelector('.status-card h2')?.textContent).toContain(
      'Season 1',
    );
    expect(element.querySelector('.next')?.textContent).toContain('Season 2');
    expect(element.querySelectorAll('.season-segment').length).toBe(2);
    expect(element.querySelectorAll('rk-season-instance-list').length).toBe(1);
    expect(element.querySelector('.operations-section')).toBeNull();
  });

  it('renders disabled configuration and disables its configuration fieldset', () => {
    flushInitialLoad(createSettings({ enabled: false }), []);

    const element = fixture.nativeElement as HTMLElement;
    expect(
      element.querySelector('.status-card')?.getAttribute('data-status'),
    ).toBe('disabled');
    expect(
      (element.querySelector('.steps-fieldset') as HTMLFieldSetElement)
        .disabled,
    ).toBeTrue();
    expect(
      (element.querySelector('#season-enabled') as HTMLInputElement).checked,
    ).toBeFalse();
  });

  it('renders ready state when only a future season is scheduled', () => {
    flushInitialLoad(createSettings(), [createSeason(2, 'Scheduled')]);

    const status = (fixture.nativeElement as HTMLElement).querySelector(
      '.status-card',
    );
    expect(status?.getAttribute('data-status')).toBe('ready');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('.instance-row')
        ?.textContent,
    ).toContain('Season 2');
    expect(component.nextSeason()?.name).toBe('Season 2');
  });

  it('becomes dirty only after a real settings change and reset restores the baseline', () => {
    flushInitialLoad();
    const settings = component.settings()!;

    settings.enabled = true;
    expect(component.dirty()).toBeFalse();

    settings.winnerCount = 5;
    expect(component.dirty()).toBeTrue();
    component.serverErrors.set({ winnerCount: 'Invalid' });
    component.pageError.set('Failed');

    component.reset();
    http.expectOne(previewUrl).flush(previews);
    expect(component.settings()!.winnerCount).toBe(3);
    expect(component.dirty()).toBeFalse();
    expect(component.serverErrors()).toEqual({});
    expect(component.pageError()).toBe('');
  });

  it('saves a valid change, clears dirty state and reloads seasons', () => {
    flushInitialLoad();
    const toast = TestBed.inject(ToastService);
    spyOn(toast, 'success');
    component.settings()!.winnerCount = 5;

    component.save();
    const save = http.expectOne(configUrl);
    expect(save.request.method).toBe('PUT');
    expect(save.request.body.winnerCount).toBe(5);
    save.flush(createSettings({ winnerCount: 5 }));
    http.expectOne(previewUrl).flush(previews);
    http.expectOne(seasonsUrl).flush([createSeason(3, 'Scheduled')]);

    expect(component.dirty()).toBeFalse();
    expect(component.seasons()[0].sequence).toBe(3);
    expect(toast.success).toHaveBeenCalled();
  });

  it('retains dirty state and exposes the API error when save fails', () => {
    flushInitialLoad();
    component.settings()!.winnerCount = 5;

    component.save();
    http
      .expectOne(configUrl)
      .flush(
        { message: 'Season save failed' },
        { status: 500, statusText: 'Server Error' },
      );

    expect(component.dirty()).toBeTrue();
    expect(component.pageError()).toContain('Season save failed');
    expect(component.saving()).toBeFalse();
  });

  it('applies schedule presets and treats manual scheduling as custom', () => {
    flushInitialLoad();

    component.setPreset('monthly');
    const monthly = http.expectOne(previewUrl);
    expect(monthly.request.body.scheduleKind).toBe('Monthly');
    monthly.flush(previews);
    expect(component.preset(component.settings()!)).toBe('monthly');
    expect(component.dirty()).toBeTrue();

    component.settings()!.scheduleKind = 'Manual';
    component.refreshPreview();
    http.expectNone(previewUrl);
    expect(component.preview()).toEqual([]);

    component.setPreset('custom');
    http.expectNone(previewUrl);
    expect(component.settings()!.scheduleKind).toBe('Manual');
    expect(component.preset(component.settings()!)).toBe('custom');
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('#schedule-anchor')).toBeNull();
    expect(element.querySelector('.plan-strip')).toBeNull();
  });

  it('keeps the newest preview when an older request finishes last', () => {
    flushInitialLoad();

    component.refreshPreview();
    const stale = http.expectOne(previewUrl);
    component.settings()!.nameTemplate = 'Cycle {number}';
    component.refreshPreview();
    const latest = http.expectOne(previewUrl);
    const latestPreview = [{ ...previews[0], name: 'Cycle 1' }];

    latest.flush(latestPreview);
    expect(component.preview()).toEqual(latestPreview);
    expect(component.previewing()).toBeFalse();

    stale.flush([{ ...previews[0], name: 'Stale season' }]);
    expect(component.preview()).toEqual(latestPreview);
  });

  it('validates rotation duplicates and unknown tokens and previews patterns', () => {
    flushInitialLoad();
    const settings = component.settings()!;
    component.preview.set(previews);

    settings.nameTemplate = '{number:000} {rotation} {year}';
    settings.rotation = ['Alpha', 'Beta'];
    settings.rotationOffset = 1;
    expect(component.namePreview(settings, 3)).toEqual([
      '001 Beta 2030',
      '002 Alpha 2030',
    ]);

    settings.rotation = ['Alpha', ' alpha '];
    expect(component.validationKeys(settings)).toContain(
      'seasons.validation.rotationDuplicate',
    );
    settings.rotation = ['Alpha'];
    settings.nameTemplate = 'Season {mystery}';
    expect(component.validationKeys(settings)).toContain(
      'seasons.validation.tokens',
    );
    expect(component.namePreview(settings)).toEqual([]);
  });

  it('uses button quicklinks for in-page navigation without changing the route', () => {
    flushInitialLoad();
    fixture.detectChanges();
    const navigation = fixture.nativeElement.querySelector('.step-nav') as HTMLElement;
    const buttons = navigation.querySelectorAll('button');

    expect(navigation.querySelector('a')).toBeNull();
    expect(buttons.length).toBe(5);
    spyOn(component, 'scrollToStep');
    (buttons[2] as HTMLButtonElement).click();
    expect(component.scrollToStep).toHaveBeenCalledWith(3);
  });

  it('keeps the draft when a guild switch is cancelled', () => {
    flushInitialLoad();
    component.settings()!.gapDays = 2;
    const confirm = spyOn(window, 'confirm').and.returnValue(false);
    const otherGuild: Guild = { ...guild, id: 'guild-2', name: 'Other guild' };

    TestBed.inject(AppStore).setSelectedGuild(otherGuild);
    fixture.detectChanges();

    expect(confirm).toHaveBeenCalled();
    expect(TestBed.inject(AppStore).selectedGuild()?.id).toBe('guild-1');
    expect(component.settings()!.gapDays).toBe(2);
    expect(component.dirty()).toBeTrue();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-2/xp/seasons/config`);
  });

  it('preserves edits made while a lifecycle action is running', () => {
    flushInitialLoad();
    const season = createSeason(2, 'Scheduled');
    component.requestAction('start', season);
    component.confirmAction();
    const action = http.expectOne(`${seasonsUrl}/season-2/start`);
    component.settings()!.gapDays = 3;

    action.flush({ ...season, status: 'Active' });

    expect(component.settings()!.gapDays).toBe(3);
    expect(component.dirty()).toBeTrue();
    http.expectOne(seasonsUrl).flush([{ ...season, status: 'Active' }]);
    http.expectNone(configUrl);
  });

  it('calculates initial and carry-over XP examples', () => {
    flushInitialLoad();
    const settings = component.settings()!;

    settings.initialXpMode = 'Zero';
    expect(component.initialExample(settings)).toBe(0);
    settings.initialXpMode = 'Lifetime';
    expect(component.initialExample(settings)).toBe(10000);
    settings.initialXpMode = 'LifetimePercentage';
    settings.initialXpPercentage = 12.5;
    expect(component.initialExample(settings)).toBe(1250);

    settings.carryOverMode = 'Percentage';
    settings.carryOverPercentage = 27.5;
    expect(component.carryExample(settings)).toBe(2750);
    settings.carryOverMode = 'None';
    expect(component.carryExample(settings)).toBe(0);
  });

  it('does not save invalid enabled settings', () => {
    flushInitialLoad();
    const settings = component.settings()!;
    settings.scheduleAnchorUtc = null;
    settings.winnerCount = 0;

    expect(component.valid(settings)).toBeFalse();
    expect(component.validationKeys(settings)).toContain(
      'seasons.validation.anchor',
    );
    component.save();
    http.expectNone(configUrl);
  });

  it('offers resume only while an unfinalized cancelled season is in its window', () => {
    flushInitialLoad();
    const now = Date.now();
    const resumable = createSeason(4, 'Cancelled', {
      startsAtUtc: new Date(now - 60_000).toISOString(),
      endsAtUtc: new Date(now + 60_000).toISOString(),
    });

    expect(component.isResumable(resumable)).toBeTrue();
    expect(component.availableActions(resumable)).toEqual(['resume', 'delete']);
    expect(
      component.isResumable({ ...resumable, carryOverApplied: true }),
    ).toBeFalse();
    expect(
      component.isResumable({ ...resumable, finalized: true }),
    ).toBeFalse();
    expect(
      component.isResumable({
        ...resumable,
        endsAtUtc: new Date(now - 1).toISOString(),
      }),
    ).toBeFalse();
  });

  it('opens the shared dialog for a lifecycle action and reloads after confirmation', () => {
    flushInitialLoad();
    const season = createSeason(2, 'Scheduled');
    const dialog = fixture.debugElement.query(
      By.directive(ConfirmationDialogComponent),
    ).componentInstance as ConfirmationDialogComponent;
    spyOn(dialog, 'open');
    spyOn(dialog, 'close');

    component.requestAction('start', season);
    expect(dialog.open).toHaveBeenCalled();
    expect(component.pending()).toEqual({ action: 'start', season, guildId: 'guild-1' });

    component.confirmAction();
    const action = http.expectOne(`${seasonsUrl}/season-2/start`);
    expect(action.request.method).toBe('POST');
    action.flush({ ...season, status: 'Active' });
    expect(dialog.close).toHaveBeenCalled();
    expect(component.pending()).toBeNull();

    http.expectOne(configUrl).flush(createSettings());
    http.expectOne(seasonsUrl).flush([{ ...season, status: 'Active' }]);
    http.expectOne(previewUrl).flush(previews);
    expect(component.currentSeason()?.id).toBe('season-2');
  });

  it('cancels all scheduled seasons through the confirmed bulk endpoint', () => {
    flushInitialLoad();
    const dialog = fixture.debugElement.query(
      By.directive(ConfirmationDialogComponent),
    ).componentInstance as ConfirmationDialogComponent;
    spyOn(dialog, 'open');
    spyOn(dialog, 'close');

    component.requestBulkAction('cancelScheduled');
    expect(dialog.open).toHaveBeenCalled();
    expect(component.pending()).toEqual({ action: 'cancelScheduled', season: null, guildId: 'guild-1' });
    component.confirmAction();

    const request = http.expectOne(`${seasonsUrl}/cancel-scheduled`);
    expect(request.request.method).toBe('POST');
    request.flush({ affectedCount: 1 });
    http.expectOne(configUrl).flush(createSettings());
    http.expectOne(seasonsUrl).flush([createSeason(1, 'Active'), createSeason(2, 'Cancelled')]);
    http.expectOne(previewUrl).flush(previews);
    expect(dialog.close).toHaveBeenCalled();
  });

  it('deletes all cancelled seasons through the confirmed bulk endpoint', () => {
    flushInitialLoad(createSettings(), [createSeason(3, 'Cancelled')]);
    component.requestBulkAction('deleteCancelled');
    component.confirmAction();

    const request = http.expectOne(`${seasonsUrl}/cancelled`);
    expect(request.request.method).toBe('DELETE');
    request.flush({ affectedCount: 1 });
    http.expectOne(configUrl).flush(createSettings());
    http.expectOne(seasonsUrl).flush([]);
    http.expectOne(previewUrl).flush(previews);
  });
});
