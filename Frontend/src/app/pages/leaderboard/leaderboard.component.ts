import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { combineLatest, finalize } from 'rxjs';
import { LevelProgressComponent } from '../../components/level-progress/level-progress.component';
import { UserAvatarComponent } from '../../components/user-avatar/user-avatar.component';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService, LeaderboardEntry, LeaderboardPage, LeaderboardWindow, SeasonLeaderboardScope } from '../../services/guild.service';
import { LeaderboardChanged, RealtimeService } from '../../services/realtime.service';
import { AuthStore } from '../../store/auth.store';
import { ToastService } from '../../services/toast.service';

interface VirtualLeaderboardRow { index: number; entry?: LeaderboardEntry; }

@Component({
  selector: 'app-leaderboard',
  standalone: true,
  imports: [CommonModule, RouterLink, TranslocoPipe, LevelProgressComponent, UserAvatarComponent, ScrollingModule],
  templateUrl: './leaderboard.component.html',
  styleUrls: ['./leaderboard.component.scss'],
})
export class LeaderboardComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(GuildService);
  private readonly destroyRef = inject(DestroyRef);
  readonly auth = inject(AuthStore);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);
  @ViewChild(CdkVirtualScrollViewport) private viewport?: CdkVirtualScrollViewport;

  readonly page = signal<LeaderboardPage | null>(null);
  readonly virtualRows = signal<Array<VirtualLeaderboardRow | undefined>>([]);
  readonly totalCount = signal(0);
  readonly initialLoading = signal(true);
  readonly refreshing = signal(false);
  readonly jumping = signal(false);
  readonly privacyBusy = signal(false);
  readonly error = signal('');
  readonly scope = signal<SeasonLeaderboardScope>('Lifetime');
  readonly selectedSeasonId = signal<string | null>(null);
  readonly rowHeight = 88;

  private alias = '';
  private hasExplicitScope = false;
  private requestSequence = 0;
  private desiredOffset = 0;
  private visibleIndex = 0;
  private windowDirty = false;
  private windowRequestActive = false;
  private pendingAroundCurrentUser = false;
  private refreshTimer?: number;
  private readonly cachedUserIndexes = new Map<string, number>();
  private realtimeSubscription?: { alias: string; scope: SeasonLeaderboardScope; seasonId?: string };

  ngOnInit(): void {
    this.realtime.leaderboardEntryChanges$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(event => this.handleInvalidation(event));
    this.realtime.leaderboardInvalidations$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(event => this.handleInvalidation(event));
    this.realtime.leaderboardChanges$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(event => this.handleStructuralChange(event));
    this.realtime.leaderboardAccessRevoked$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(event => {
      if (this.matchesCurrentView(event)) {
        this.requestSequence++;
        this.windowDirty = false;
        this.page.set(null);
        this.clearVisibleData(this.i18n.translate('errors.notMember'));
      }
    });
    this.realtime.leaderboardConnections$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { if (this.page()) this.scheduleVisibleWindowRefresh(); });
    this.destroyRef.onDestroy(() => {
      if (this.realtimeSubscription) void this.realtime.unsubscribeLeaderboard(this.realtimeSubscription.alias, this.realtimeSubscription.scope, this.realtimeSubscription.seasonId);
      if (this.refreshTimer !== undefined) window.clearTimeout(this.refreshTimer);
    });
    combineLatest([this.route.paramMap, this.route.queryParamMap]).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(([params, query]) => {
      if (this.realtimeSubscription) void this.realtime.unsubscribeLeaderboard(this.realtimeSubscription.alias, this.realtimeSubscription.scope, this.realtimeSubscription.seasonId);
      this.alias = params.get('alias') ?? '';
      const requestedScope = query.get('scope');
      this.hasExplicitScope = requestedScope !== null;
      this.scope.set(requestedScope === 'CurrentSeason' || requestedScope === 'Season' ? requestedScope : 'Lifetime');
      this.selectedSeasonId.set(query.get('seasonId'));
      this.resetWindow();
      this.loadInitial();
    });
  }

  loadInitial(): void {
    if (!this.alias) return;
    this.initialLoading.set(true);
    this.error.set('');
    const requestedAlias = this.alias;
    const requestId = ++this.requestSequence;
    const requestedScope = this.hasExplicitScope ? this.scope() : undefined;
    this.api.publicLeaderboard(this.alias, undefined, false, requestedScope, requestedScope === 'Season' ? this.selectedSeasonId() ?? undefined : undefined)
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => { if (requestId === this.requestSequence && !this.page()) this.initialLoading.set(false); }))
      .subscribe({
        next: response => {
          if (requestedAlias !== this.alias || requestId !== this.requestSequence) return;
          if (!this.hasExplicitScope) {
            this.scope.set(response.scope ?? 'Lifetime');
            this.selectedSeasonId.set(response.seasonId ?? null);
          }
          this.page.set(response);
          this.applyHttpFallback(response);
          void this.initializeRealtime(response, requestedAlias, requestId);
        },
        error: response => {
          if (requestedAlias !== this.alias || requestId !== this.requestSequence) return;
          this.initialLoading.set(false);
          this.clearVisibleData();
          this.error.set(response.status === 401 ? this.i18n.translate('errors.leaderboardLogin') : response.status === 403 ? this.i18n.translate('errors.notMember') : this.apiErrors.resolve(response, 'errors.leaderboardLoad').message);
        },
      });
  }

  onViewportIndexChange(index: number): void {
    this.visibleIndex = index;
    const offset = this.offsetForIndex(index);
    if (!this.virtualRows()[index]?.entry || Math.abs(offset - this.desiredOffset) >= 25) this.scheduleWindowRefresh(offset);
  }

  jumpToMe(): void {
    if (!this.page()?.isMember || this.jumping()) return;
    this.jumping.set(true);
    this.pendingAroundCurrentUser = true;
    this.scheduleWindowRefresh(this.desiredOffset, true);
  }

  retryVisibleWindow(): void { this.scheduleVisibleWindowRefresh(); }

  setPrivacy(publicVisible: boolean): void {
    if (this.privacyBusy()) return;
    this.privacyBusy.set(true);
    const requestedAlias = this.alias;
    this.api.setLeaderboardPrivacy(this.alias, publicVisible).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => { if (requestedAlias === this.alias) this.privacyBusy.set(false); })).subscribe({
      next: () => { if (requestedAlias === this.alias) { this.page.update(page => page ? { ...page, publicVisible } : page); this.scheduleVisibleWindowRefresh(); } },
      error: error => { if (requestedAlias === this.alias) this.toast.error(this.apiErrors.resolve(error, 'errors.privacySave').message); },
    });
  }

  selectScope(scope: SeasonLeaderboardScope): void {
    if (scope === 'Season') {
      const seasonId = this.selectedSeasonId() ?? this.page()?.historicalSeasons?.[0]?.id;
      if (!seasonId) return;
      this.navigateScope(scope, seasonId);
      return;
    }
    this.navigateScope(scope, null);
  }
  selectHistoricalSeason(seasonId: string): void { this.navigateScope('Season', seasonId); }
  formatNumber(value: string | number): string { return this.locale.number(value); }
  messageCount(value: string | number): string { return this.locale.plural(value, 'leaderboard.messageOne', 'leaderboard.messageOther'); }
  trackRow(index: number, row: VirtualLeaderboardRow | undefined): number | string { return row?.entry?.userId ?? index; }

  private async initializeRealtime(response: LeaderboardPage, alias: string, requestSequence: number): Promise<void> {
    try {
      if (alias !== this.alias || requestSequence !== this.requestSequence) return;
      await this.updateRealtimeSubscription(response.scope ?? this.scope(), response.seasonId ?? undefined);
      if (alias !== this.alias || requestSequence !== this.requestSequence) return;
      this.scheduleWindowRefresh(0);
    } catch (error) {
      if (alias === this.alias && requestSequence === this.requestSequence) {
        this.initialLoading.set(false);
        this.error.set(this.apiErrors.resolve(error, 'errors.leaderboardLoad').message);
      }
    }
  }

  private handleInvalidation(event: Pick<LeaderboardChanged, 'alias' | 'scope' | 'seasonId'>): void {
    if (!this.matchesCurrentView(event) || this.refreshTimer !== undefined) return;
    this.refreshTimer = window.setTimeout(() => {
      this.refreshTimer = undefined;
      this.scheduleVisibleWindowRefresh();
    }, 200);
  }

  private handleStructuralChange(event: LeaderboardChanged): void {
    if (!this.matchesCurrentView(event)) return;
    this.loadInitial();
  }

  private matchesCurrentView(event: Pick<LeaderboardChanged, 'alias' | 'scope' | 'seasonId'>): boolean {
    return event.alias === this.alias && event.scope === this.scope() && event.seasonId === (this.scope() === 'Season' ? this.selectedSeasonId() : null);
  }

  private scheduleWindowRefresh(offset: number, aroundCurrentUser = false): void {
    this.desiredOffset = Math.max(0, offset);
    this.pendingAroundCurrentUser ||= aroundCurrentUser;
    this.windowDirty = true;
    void this.processWindowRequests();
  }

  private scheduleVisibleWindowRefresh(): void {
    this.scheduleWindowRefresh(this.offsetForIndex(this.visibleIndex));
  }

  private offsetForIndex(index: number): number {
    return Math.max(0, Math.floor(Math.max(0, index - 10) / 25) * 25);
  }

  private async processWindowRequests(): Promise<void> {
    if (this.windowRequestActive || !this.page()) return;
    this.windowRequestActive = true;
    this.refreshing.set(true);
    try {
      while (this.windowDirty) {
        this.windowDirty = false;
        const aroundCurrentUser = this.pendingAroundCurrentUser;
        this.pendingAroundCurrentUser = false;
        const alias = this.alias;
        const requestSequence = this.requestSequence;
        const requestedOffset = this.desiredOffset;
        const visibleIndexAtRequest = this.visibleIndex;
        try {
          const response = await this.realtime.getLeaderboardWindow({
            alias,
            scope: this.scope(),
            seasonId: this.scope() === 'Season' ? this.selectedSeasonId() ?? undefined : undefined,
            offset: requestedOffset,
            take: 50,
            aroundCurrentUser,
            cachedUserIds: [...this.cachedUserIndexes.keys()].slice(-100),
          });
          if (alias !== this.alias || requestSequence !== this.requestSequence) continue;
          this.applyWindow(response);
          if (aroundCurrentUser) {
            const current = response.items.find(row => row.entry.isCurrentUser) ?? response.cachedItems.find(row => row.entry.isCurrentUser);
            if (current) {
              setTimeout(() => {
                if (alias !== this.alias || requestSequence !== this.requestSequence || this.visibleIndex !== visibleIndexAtRequest) return;
                this.visibleIndex = current.index;
                this.desiredOffset = this.offsetForIndex(current.index);
                this.viewport?.scrollToIndex(current.index, 'auto');
              });
            }
            else this.toast.info(this.i18n.translate('errors.noXpEntry'));
          }
        } catch (error) {
          if (alias === this.alias && requestSequence === this.requestSequence) this.error.set(this.apiErrors.resolve(error, 'errors.leaderboardLoad').message);
        } finally {
          if (aroundCurrentUser) this.jumping.set(false);
        }
      }
    } finally {
      this.windowRequestActive = false;
      this.refreshing.set(false);
      this.initialLoading.set(false);
      if (this.windowDirty) void this.processWindowRequests();
    }
  }

  private applyWindow(response: LeaderboardWindow): void {
    const rows = new Array<VirtualLeaderboardRow | undefined>(response.totalCount);
    const nextIndexes = new Map<string, number>();
    for (const row of [...response.cachedItems, ...response.items]) {
      if (row.index < 0 || row.index >= response.totalCount) continue;
      const previousIndex = nextIndexes.get(row.entry.userId);
      if (previousIndex !== undefined) rows[previousIndex] = undefined;
      const displacedUserId = rows[row.index]?.entry?.userId;
      if (displacedUserId) nextIndexes.delete(displacedUserId);
      rows[row.index] = { index: row.index, entry: { ...row.entry, rank: Number(row.entry.rank), level: Number(row.entry.level) } };
      nextIndexes.set(row.entry.userId, row.index);
    }
    this.cachedUserIndexes.clear();
    for (const [userId, index] of nextIndexes) this.cachedUserIndexes.set(userId, index);
    this.virtualRows.set(rows);
    this.totalCount.set(response.totalCount);
    this.page.update(page => page ? {
      ...page,
      guildName: response.guildName, alias: response.alias, visibility: response.visibility,
      items: response.items.map(row => row.entry), isMember: response.isMember, publicVisible: response.publicVisible,
      scope: response.scope, seasonId: response.seasonId, seasonName: response.seasonName,
      historicalSeasons: response.historicalSeasons, currentSeason: response.currentSeason, seasonsEnabled: response.seasonsEnabled,
      hasMore: false, nextCursor: null,
    } : page);
    this.error.set('');
  }

  private applyHttpFallback(response: LeaderboardPage): void {
    if (this.totalCount() > 0) return;
    const rows = response.items.map((entry, index) => ({ index, entry: { ...entry, rank: Number(entry.rank), level: Number(entry.level) } }));
    this.virtualRows.set(rows);
    this.totalCount.set(rows.length);
    this.initialLoading.set(false);
  }

  private clearVisibleData(error = ''): void {
    this.virtualRows.set([]);
    this.totalCount.set(0);
    this.cachedUserIndexes.clear();
    if (error) this.error.set(error);
  }

  private resetWindow(): void {
    this.requestSequence++;
    this.virtualRows.set([]);
    this.totalCount.set(0);
    this.page.set(null);
    this.cachedUserIndexes.clear();
    this.desiredOffset = 0;
    this.visibleIndex = 0;
    this.windowDirty = false;
    this.pendingAroundCurrentUser = false;
    this.initialLoading.set(true);
    this.refreshing.set(false);
    this.jumping.set(false);
    this.privacyBusy.set(false);
    this.error.set('');
  }

  private navigateScope(scope: SeasonLeaderboardScope, seasonId: string | null): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { scope, seasonId }, queryParamsHandling: '' });
  }

  private async updateRealtimeSubscription(scope: SeasonLeaderboardScope, seasonId?: string): Promise<void> {
    const next = { alias: this.alias, scope, seasonId: scope === 'Season' ? seasonId : undefined };
    if (this.realtimeSubscription?.alias === next.alias && this.realtimeSubscription.scope === next.scope && this.realtimeSubscription.seasonId === next.seasonId) return;
    if (this.realtimeSubscription) await this.realtime.unsubscribeLeaderboard(this.realtimeSubscription.alias, this.realtimeSubscription.scope, this.realtimeSubscription.seasonId);
    this.realtimeSubscription = next;
    await this.realtime.subscribeLeaderboard(next.alias, next.scope, next.seasonId);
  }
}
