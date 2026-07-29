import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ToastService } from '../../services/toast.service';
import { testI18n } from '../../testing/i18n-testing';
import { UserXpModalComponent } from './user-xp-modal.component';

describe('UserXpModalComponent', () => {
  const ada = { guildId: 'guild-1', userId: '1', displayName: 'Ada', isCurrentMember: true };
  const details = (userId = '1', canAdjust = false) => ({
    userId,
    displayName: userId === '1' ? 'Ada' : 'Bea',
    isCurrentMember: true,
    lastXpActivityAtUtc: null,
    lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 },
    activeSeason: null,
    permissions: { canAdjust, isSelf: false, isOwner: canAdjust }
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [UserXpModalComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('opens on the requested tab, focuses it, and restores trigger focus when closed', fakeAsync(() => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const trigger = document.createElement('button');
    document.body.appendChild(trigger);
    spyOn(trigger, 'focus');

    fixture.componentInstance.open(ada, { initialTab: 'overview', trigger });
    http.expectOne(request => request.url.includes('/guilds/guild-1/') && request.url.endsWith('/members/1')).flush(details());
    http.expectOne(request => request.url.includes('/guilds/guild-1/') && request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    tick();
    fixture.detectChanges();

    const selectedTab = fixture.nativeElement.querySelector('[role="tab"][aria-selected="true"]') as HTMLElement;
    expect(selectedTab.textContent).toContain('xpAudit.overview');
    fixture.componentInstance.close();
    fixture.componentInstance.onDialogClosed();
    tick();
    expect(trigger.focus).toHaveBeenCalled();
    trigger.remove();
  }));

  it('ignores stale detail and timeline responses after opening another subject', () => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    fixture.componentInstance.open(ada);
    const staleDetails = http.expectOne(request => request.url.endsWith('/members/1'));
    const staleTimeline = http.expectOne(request => request.url.endsWith('/members/1/timeline'));

    fixture.componentInstance.open({ guildId: 'guild-2', userId: '2', displayName: 'Bea' });
    http.expectOne(request => request.url.includes('/guilds/guild-2/') && request.url.endsWith('/members/2')).flush(details('2'));
    http.expectOne(request => request.url.includes('/guilds/guild-2/') && request.url.endsWith('/members/2/timeline')).flush({ items: [], nextCursor: null });
    staleDetails.flush(details('1'));
    staleTimeline.flush({ items: [{ id: 'stale' }], nextCursor: null });

    expect(fixture.componentInstance.details()?.userId).toBe('2');
    expect(fixture.componentInstance.timelineItems()).toEqual([]);
  });

  it('closes, reports a 403 details response, and restores focus', fakeAsync(() => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const toast = spyOn(TestBed.inject(ToastService), 'error');
    const trigger = document.createElement('button');
    document.body.appendChild(trigger);
    spyOn(trigger, 'focus');
    fixture.componentInstance.open(ada, { trigger });
    http.expectOne(request => request.url.endsWith('/members/1')).flush(null, { status: 403, statusText: 'Forbidden' });
    http.expectOne(request => request.url.endsWith('/timeline')).flush(null, { status: 403, statusText: 'Forbidden' });
    tick();

    expect(fixture.componentInstance.subject()).toBeNull();
    expect(fixture.componentInstance.memberDialog?.nativeElement.open).toBeFalse();
    expect(toast).toHaveBeenCalledOnceWith('xpAudit.noAuditPermission');
    expect(trigger.focus).toHaveBeenCalled();
    trigger.remove();
  }));

  it('binds the adjustment direction radios to the signed amount', fakeAsync(() => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    fixture.componentInstance.open(ada, { initialTab: 'adjust' });
    http.expectOne(request => request.url.endsWith('/members/1')).flush(details('1', true));
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.amount = 25;
    fixture.detectChanges();
    tick();

    const subtract = fixture.debugElement.query(By.css('input[name="direction"][value="subtract"]')).nativeElement as HTMLInputElement;
    subtract.click();
    tick();
    fixture.detectChanges();

    expect(subtract.checked).toBeTrue();
    expect(fixture.componentInstance.direction).toBe('subtract');
    expect(fixture.componentInstance.signedAmount()).toBe(-25);
  }));

  it('applies UTC filters and forces voice history to compact mode', () => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    fixture.componentInstance.open(ada);
    http.expectOne(request => request.url.endsWith('/members/1')).flush(details());
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });

    fixture.componentInstance.setTimelineMode('entries');
    http.expectOne(request => request.url.endsWith('/entries')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.updateDateFilter('from', '2026-07-24');
    http.expectOne(request => request.url.endsWith('/entries') && request.params.get('from') === '2026-07-24T00:00:00.000Z').flush({ items: [], nextCursor: null });
    fixture.componentInstance.updateFilter('source', 'voice');
    const voice = http.expectOne(request => request.url.endsWith('/timeline') && request.params.get('source') === 'voice');
    expect(voice.request.params.get('from')).toBe('2026-07-24T00:00:00.000Z');
    voice.flush({ items: [], nextCursor: null });

    expect(fixture.componentInstance.timelineMode()).toBe('compact');
    fixture.componentInstance.setTimelineMode('entries');
    http.expectNone(request => request.url.endsWith('/entries') && request.params.get('source') === 'voice');
  });

  it('caches voice segments and invalidates stale lazy responses on filter changes', () => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    fixture.componentInstance.open(ada);
    http.expectOne(request => request.url.endsWith('/members/1')).flush(details());
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    const day = { id: 'voice-day:2026-07-24', itemType: 'VoiceDay', dayKey: '2026-07-24' } as any;

    fixture.componentInstance.toggleVoiceDay(day);
    const stale = http.expectOne(request => request.url.endsWith('/segments'));
    fixture.componentInstance.updateFilter('channelId', '42');
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [day], nextCursor: null });
    stale.flush({ items: [{ id: 'stale' }] });
    expect(fixture.componentInstance.voiceDayState(day)).toBeNull();

    fixture.componentInstance.toggleVoiceDay(day);
    const current = http.expectOne(request => request.url.endsWith('/segments'));
    expect(current.request.params.get('channelId')).toBe('42');
    current.flush({ items: [] });
    fixture.componentInstance.toggleVoiceDay(day);
    fixture.componentInstance.toggleVoiceDay(day);
    http.expectNone(request => request.url.endsWith('/segments'));
  });

  it('emits xpChanged and refreshes after an adjustment', () => {
    const fixture = TestBed.createComponent(UserXpModalComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    fixture.componentInstance.open(ada, { initialTab: 'adjust' });
    http.expectOne(request => request.url.endsWith('/members/1')).flush(details('1', true));
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    const changed = jasmine.createSpy('changed');
    fixture.componentInstance.xpChanged.subscribe(changed);
    fixture.componentInstance.amount = 25;
    fixture.componentInstance.reason = 'Manual adjustment';

    fixture.componentInstance.adjust();
    const request = http.expectOne(request => request.url.includes('/guilds/guild-1/') && request.url.endsWith('/adjustments'));
    expect(request.request.body.amount).toBe(25);
    request.flush({});
    expect(changed).toHaveBeenCalledWith({ guildId: 'guild-1', userId: '1' });
    http.expectOne(request => request.url.endsWith('/members/1')).flush(details('1', true));
    http.expectOne(request => request.url.endsWith('/timeline')).flush({ items: [], nextCursor: null });
    expect(fixture.componentInstance.activeModalTab()).toBe('history');
  });
});
