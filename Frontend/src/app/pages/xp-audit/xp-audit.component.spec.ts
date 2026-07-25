import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { XpAuditComponent } from './xp-audit.component';

describe('XpAuditComponent', () => {
  const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [XpAuditComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    TestBed.inject(AppStore).setSelectedGuild(guild);
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('loads members for the selected guild and appends unique subsequent pages', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const first = http.expectOne(request => request.url.endsWith('/guilds/guild-1/xp-audit/members'));
    expect(first.request.params.get('includeFormerMembers')).toBe('false');
    first.flush({ items: [{ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 }], nextCursor: 'next' });

    fixture.componentInstance.loadMembers(true);
    http.expectOne(request => request.params.get('cursor') === 'next').flush({ items: [
      { userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 },
      { userId: '2', displayName: 'Bea', isCurrentMember: false, totalXp: 20, level: 2 }
    ], nextCursor: null });

    expect(fixture.componentInstance.members().map(member => member.userId)).toEqual(['1', '2']);
  });

  it('sends only populated history filters and resets entries on a filter change', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/timeline') && request.params.keys().length === 0).flush({ items: [], nextCursor: 'old' });

    fixture.componentInstance.updateFilter('direction', 'Positive');
    const filtered = http.expectOne(request => request.url.endsWith('/timeline') && request.params.get('direction') === 'Positive');
    expect(filtered.request.params.has('cursor')).toBeFalse();
    filtered.flush({ items: [], nextCursor: null });
  });

  it('converts date-only filters at UTC boundaries', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.updateDateFilter('from', '2026-07-24');
    fixture.componentInstance.updateDateFilter('to', '2026-07-24');

    expect(fixture.componentInstance.filters().from).toBe('2026-07-24T00:00:00.000Z');
    expect(fixture.componentInstance.filters().to).toBe('2026-07-24T23:59:59.999Z');
  });

  it('forces the compact daily timeline when voice is selected', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.setTimelineMode('entries');
    http.expectOne(request => request.url.endsWith('/entries')).flush({ items: [], nextCursor: null });

    fixture.componentInstance.updateFilter('source', 'voice');

    expect(fixture.componentInstance.timelineMode()).toBe('compact');
    expect(fixture.componentInstance.voiceTimelineSelected()).toBeTrue();
    http.expectOne(request => request.url.endsWith('/timeline') && request.params.get('source') === 'voice').flush({ items: [], nextCursor: null });
    fixture.componentInstance.setTimelineMode('entries');
    http.expectNone(request => request.url.endsWith('/entries') && request.params.get('source') === 'voice');
  });

  it('validates the adjustment amount and calculates a negative API amount for deductions', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    const component = fixture.componentInstance;
    component.amount = '0';
    expect(component.amountError).toBeTruthy();
    component.amount = '1.12345';
    expect(component.amountError).toBeTruthy();
    component.amount = '1000000.0001';
    expect(component.amountError).toBeTruthy();
    component.amount = 25.25;
    component.reason = 'Manual adjustment';
    expect(component.amountError).toBe('');
    expect(component.canReviewAdjustment).toBeTrue();
    component.direction = 'subtract';
    expect(component.signedAmount()).toBe(-25.25);
  });

  it('lazy-loads a voice day once and reuses the guild/user/filter cache', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    const day = { id: 'voice-day:2026-07-24', itemType: 'VoiceDay', dayKey: '2026-07-24' } as any;

    fixture.componentInstance.toggleVoiceDay(day);
    expect(fixture.componentInstance.voiceDayState(day)?.loading).toBeTrue();
    http.expectOne(request => request.url.endsWith('/voice-days/2026-07-24/segments')).flush({ items: [{ id: 'segment-1', amount: 5 }] });
    expect(fixture.componentInstance.voiceDayState(day)?.items?.length).toBe(1);

    fixture.componentInstance.toggleVoiceDay(day);
    fixture.componentInstance.toggleVoiceDay(day);
    http.expectNone(request => request.url.endsWith('/voice-days/2026-07-24/segments'));
    expect(fixture.componentInstance.voiceDayState(day)?.expanded).toBeTrue();

    fixture.componentInstance.updateFilter('channelId', '42');
    expect(fixture.componentInstance.voiceDayState(day)).toBeNull();
    http.expectOne(request => request.url.endsWith('/timeline') && request.params.get('channelId') === '42').flush({ items: [day], nextCursor: null });
    fixture.componentInstance.toggleVoiceDay(day);
    const filteredSegments = http.expectOne(request => request.url.endsWith('/voice-days/2026-07-24/segments'));
    expect(filteredSegments.request.params.get('channelId')).toBe('42');
    expect(filteredSegments.request.params.has('cursor')).toBeFalse();
    filteredSegments.flush({ items: [] });
  });

  it('shows a voice-day error and retries to a loaded empty state', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    const day = { id: 'voice-day:2026-07-24', itemType: 'VoiceDay', dayKey: '2026-07-24' } as any;

    fixture.componentInstance.toggleVoiceDay(day);
    http.expectOne(request => request.url.endsWith('/segments')).flush({}, { status: 500, statusText: 'Error' });
    expect(fixture.componentInstance.voiceDayState(day)?.error).toBeTruthy();
    fixture.componentInstance.retryVoiceDay(day);
    http.expectOne(request => request.url.endsWith('/segments')).flush({ items: [] });
    expect(fixture.componentInstance.voiceDayState(day)?.items).toEqual([]);
    expect(fixture.componentInstance.voiceDayState(day)?.error).toBe('');
  });

  it('ignores stale lazy responses after a filter change and bounds the voice cache', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    const day = { id: 'voice-day:2026-07-24', itemType: 'VoiceDay', dayKey: '2026-07-24' } as any;

    fixture.componentInstance.toggleVoiceDay(day);
    const stale = http.expectOne(request => request.url.endsWith('/segments'));
    fixture.componentInstance.updateFilter('channelId', '42');
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [day], nextCursor: null });
    stale.flush({ items: [{ id: 'stale' }] });
    expect(fixture.componentInstance.voiceDayState(day)).toBeNull();

    for (let index = 0; index < 33; index++) (fixture.componentInstance as any).setVoiceDayState(`key-${index}`, { expanded: false, loading: false, error: '', items: [] });
    expect(Object.keys(fixture.componentInstance.voiceDayStates()).length).toBe(32);
  });

  it('does not offer correction or reversal actions on a voice day', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: true, isSelf: false, isOwner: true } });
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [{
      id: 'voice-day:2026-07-24', itemType: 'VoiceDay', entry: null, source: 'voice', kind: 'AutomaticGrant', scope: 'LifetimeOnly', totalAmount: 5,
      entryCount: 1, occurredFromUtc: '2026-07-24T10:00:00Z', occurredToUtc: '2026-07-24T10:01:00Z', periodStartsAtUtc: '2026-07-24T10:00:00Z',
      periodEndsAtUtc: '2026-07-24T10:01:00Z', durationSeconds: 60, channelId: '42', seasonId: null, seasonName: null, projectionStatus: 'Applied',
      appliedServerBoosterMultiplier: null, isPartial: false, dayKey: '2026-07-24', voiceDay: { day: '2026-07-24', totalXp: 5, eligibleSeconds: 60, segmentCount: 1, sessionCount: 1, sessionIds: ['opaque'], channelCount: 1, channelIds: ['42'], seasonIds: [], activityStartsAtUtc: '2026-07-24T10:00:00Z', activityEndsAtUtc: '2026-07-24T10:01:00Z' }
    }], nextCursor: null });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.voice-day')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.voice-day .rk-button--danger')).toBeNull();
  });
});
