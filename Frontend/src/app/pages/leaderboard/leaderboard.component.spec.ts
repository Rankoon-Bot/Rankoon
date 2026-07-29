import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { EMPTY, of } from 'rxjs';
import { UserXpModalComponent } from '../../components/user-xp-modal/user-xp-modal.component';
import { GuildService, LeaderboardEntry, LeaderboardPage, LeaderboardWindow } from '../../services/guild.service';
import { RealtimeService } from '../../services/realtime.service';
import { testI18n } from '../../testing/i18n-testing';
import { LeaderboardComponent } from './leaderboard.component';

describe('LeaderboardComponent', () => {
  const entry = (userId: string, displayName: string): LeaderboardEntry => ({
    userId,
    displayName,
    iconUrl: null,
    totalXp: 100,
    level: 2,
    messageCount: 5,
    voiceSeconds: 0,
    rank: 1,
    isCurrentUser: false,
  });
  const page = (canAuditXp: boolean, canAdjustXp = false): LeaderboardPage => ({
    guildName: 'Guild',
    alias: 'guild',
    visibility: 'MembersOnly',
    items: [entry('1', 'Ada')],
    nextCursor: null,
    hasMore: false,
    isMember: true,
    publicVisible: null,
    scope: 'Lifetime',
    viewerCapabilities: { guildId: 'guild-id', canAuditXp, canAdjustXp },
  });
  const realtime = {
    leaderboardEntryChanges$: EMPTY,
    leaderboardChanges$: EMPTY,
    leaderboardInvalidations$: EMPTY,
    leaderboardAccessRevoked$: EMPTY,
    leaderboardConnections$: EMPTY,
    subscribeLeaderboard: jasmine.createSpy('subscribeLeaderboard').and.resolveTo(),
    unsubscribeLeaderboard: jasmine.createSpy('unsubscribeLeaderboard').and.resolveTo(),
    getLeaderboardWindow: jasmine.createSpy('getLeaderboardWindow'),
  };

  beforeEach(() => {
    realtime.subscribeLeaderboard.calls.reset();
    realtime.unsubscribeLeaderboard.calls.reset();
    realtime.getLeaderboardWindow.calls.reset();
    TestBed.configureTestingModule({
      imports: [LeaderboardComponent, testI18n],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({})), queryParamMap: of(convertToParamMap({})) } },
        { provide: Router, useValue: { navigate: jasmine.createSpy('navigate') } },
        { provide: GuildService, useValue: { publicLeaderboard: jasmine.createSpy('publicLeaderboard') } },
        { provide: RealtimeService, useValue: realtime },
      ],
    });
  });

  it('shows row menus only with audit capability and uses the adjustment label capability', fakeAsync(() => {
    const fixture = TestBed.createComponent(LeaderboardComponent);
    fixture.detectChanges();
    fixture.componentInstance.page.set(page(false));
    fixture.componentInstance.virtualRows.set([{ index: 0, entry: entry('1', 'Ada') }]);
    fixture.componentInstance.totalCount.set(1);
    fixture.detectChanges();
    (fixture.componentInstance as any).viewport?.checkViewportSize();
    tick();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.row-actions')).toBeNull();
    expect(fixture.nativeElement.querySelector('.actions-heading')).toBeNull();
    expect(fixture.nativeElement.querySelector('.ranking').classList).not.toContain('has-actions');

    fixture.componentInstance.page.set(page(true, true));
    fixture.detectChanges();
    (fixture.componentInstance as any).viewport?.checkViewportSize();
    tick();
    fixture.detectChanges();
    const action = fixture.nativeElement.querySelector('.row-actions') as HTMLButtonElement;
    expect(action).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.actions-heading')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.ranking').classList).toContain('has-actions');
    action.click();
    fixture.detectChanges();
    expect(document.body.textContent).toContain('leaderboard.openXpAdjustments');
  }));

  it('closes row menus and opens history with the viewer capability guild id', fakeAsync(() => {
    const fixture = TestBed.createComponent(LeaderboardComponent);
    const component = fixture.componentInstance;
    component.page.set(page(true));
    const modalOpen = jasmine.createSpy('open');
    const menuClose = jasmine.createSpy('menuClose');
    const contextClose = jasmine.createSpy('contextClose');
    const trigger = document.createElement('button');
    (component as any).userXpModal = { open: modalOpen } as Partial<UserXpModalComponent>;
    (component as any).menuTriggers = { forEach: (callback: (value: { close(): void }) => void) => callback({ close: menuClose }) };
    (component as any).contextMenuTriggers = { forEach: (callback: (value: { close(): void }) => void) => callback({ close: contextClose }) };
    component.rememberMenuTrigger({ currentTarget: trigger } as unknown as Event);

    component.openXpHistory(entry('1', 'Ada'));
    expect(menuClose).toHaveBeenCalled();
    expect(contextClose).toHaveBeenCalled();
    tick();

    expect(modalOpen).toHaveBeenCalledWith({
      guildId: 'guild-id',
      userId: '1',
      displayName: 'Ada',
      iconUrl: null,
      isCurrentMember: true,
    }, { initialTab: 'history', trigger });

    modalOpen.calls.reset();
    component.page.set(page(false));
    component.openXpHistory(entry('1', 'Ada'));
    tick();
    expect(modalOpen).not.toHaveBeenCalled();
  }));

  it('applies window rows and replaces viewer capabilities', () => {
    const fixture = TestBed.createComponent(LeaderboardComponent);
    const component = fixture.componentInstance;
    component.page.set({ ...page(true, true), levelRewards: [{ level: 10, roleName: 'Veteran' }] });
    const response: LeaderboardWindow = {
      guildName: 'Guild',
      alias: 'guild',
      visibility: 'MembersOnly',
      items: [{ index: 1, entry: { ...entry('2', 'Bea'), rank: 2, level: 3 } }],
      cachedItems: [{ index: 0, entry: entry('1', 'Ada') }],
      removedCachedUserIds: [],
      offset: 0,
      totalCount: 3,
      isMember: true,
      publicVisible: null,
      scope: 'Lifetime',
      seasonId: null,
      seasonName: null,
      historicalSeasons: [],
      currentSeason: null,
      seasonsEnabled: false,
      viewerCapabilities: { guildId: 'guild-id', canAuditXp: false, canAdjustXp: false },
    };

    (component as any).applyWindow(response);

    expect(component.virtualRows().length).toBe(3);
    expect(component.virtualRows()[0]?.entry?.userId).toBe('1');
    expect(component.virtualRows()[1]?.entry?.userId).toBe('2');
    expect(component.virtualRows()[2]).toBeUndefined();
    expect(component.totalCount()).toBe(3);
    expect(component.page()?.viewerCapabilities).toEqual(response.viewerCapabilities);
    expect(component.page()?.levelRewards).toEqual([{ level: 10, roleName: 'Veteran' }]);
  });

  it('renders compact voice time with its full tooltip and accessible label', fakeAsync(() => {
    const fixture = TestBed.createComponent(LeaderboardComponent);
    const voiceEntry = { ...entry('1', 'Ada'), voiceSeconds: 3661 };
    fixture.detectChanges();
    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.componentInstance.page.set(page(false));
    fixture.componentInstance.virtualRows.set([{ index: 0, entry: voiceEntry }]);
    fixture.componentInstance.totalCount.set(1);
    fixture.detectChanges();
    (fixture.componentInstance as any).viewport?.checkViewportSize();
    tick();
    fixture.detectChanges();

    const voice = fixture.nativeElement.querySelector('.voice-time') as HTMLElement;
    expect(voice.textContent?.trim()).toBe('1 hr 1 min');
    expect(voice.title).toBe('1 hour, 1 minute, and 1 second');
    expect(voice.getAttribute('aria-label')).toContain('1 hour, 1 minute, and 1 second');
    expect(fixture.nativeElement.querySelector('.voice-mobile').textContent).toContain('1 hr 1 min');
  }));

  it('shows resolved rewards, omits empty descriptions, and renders HTML-like text safely', () => {
    const fixture = TestBed.createComponent(LeaderboardComponent);
    fixture.detectChanges();
    fixture.componentInstance.page.set({
      ...page(false),
      levelRewards: [
        { level: 10, roleName: 'Veteran', description: '<b>Trusted member</b>' },
        { level: 20, roleName: 'Champion', description: null },
      ],
    });
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('.rewards-panel') as HTMLElement;
    expect(panel).not.toBeNull();
    expect(panel.textContent).toContain('<b>Trusted member</b>');
    expect(panel.querySelector('b')).toBeNull();
    expect(panel.querySelectorAll('.reward-list li p').length).toBe(1);

    fixture.componentInstance.page.update(current => current ? { ...current, levelRewards: [] } : current);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rewards-panel')).toBeNull();
    expect(fixture.nativeElement.querySelector('.leaderboard-layout').classList).not.toContain('has-rewards');
  });
});
