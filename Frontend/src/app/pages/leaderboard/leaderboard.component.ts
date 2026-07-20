import { CommonModule } from '@angular/common';
import { AfterViewInit, Component, DestroyRef, ElementRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, finalize } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { GuildService, LeaderboardEntry, LeaderboardPage, SeasonLeaderboardScope } from '../../services/guild.service';
import { AuthStore } from '../../store/auth.store';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';

@Component({
  selector: 'app-leaderboard',
  standalone: true,
  imports: [CommonModule, RouterLink, TranslocoPipe],
  templateUrl: './leaderboard.component.html',
  styleUrls: ['./leaderboard.component.scss'],
})
export class LeaderboardComponent implements OnInit, AfterViewInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(GuildService);
  private readonly destroyRef = inject(DestroyRef);
  readonly auth = inject(AuthStore);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);

  private observedSentinel?: HTMLElement;
  @ViewChild('sentinel') set sentinel(value: ElementRef<HTMLElement> | undefined) {
    if (this.observedSentinel) this.observer?.unobserve(this.observedSentinel);
    this.observedSentinel = value?.nativeElement;
    if (this.observedSentinel) this.observer?.observe(this.observedSentinel);
  }
  readonly page = signal<LeaderboardPage | null>(null);
  readonly entries = signal<LeaderboardEntry[]>([]);
  readonly initialLoading = signal(true);
  readonly loadingMore = signal(false);
  readonly jumping = signal(false);
  readonly privacyBusy = signal(false);
  readonly error = signal('');
  readonly scope = signal<SeasonLeaderboardScope>('Lifetime');
  readonly selectedSeasonId = signal<string | null>(null);
  private alias = '';
  private hasExplicitScope = false;
  private observer?: IntersectionObserver;
  private requestSequence = 0;

  ngOnInit(): void {
    combineLatest([this.route.paramMap, this.route.queryParamMap]).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(([params, query]) => {
      this.alias = params.get('alias') ?? '';
      const requestedScope = query.get('scope');
      this.hasExplicitScope = requestedScope !== null;
      this.scope.set(requestedScope === 'CurrentSeason' || requestedScope === 'Season' ? requestedScope : 'Lifetime');
      this.selectedSeasonId.set(query.get('seasonId'));
      this.requestSequence++;
      this.entries.set([]);
      this.page.set(null);
      this.loadingMore.set(false);
      this.jumping.set(false);
      this.privacyBusy.set(false);
      this.load(true);
    });
  }

  ngAfterViewInit(): void {
    this.observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) this.loadMore();
    }, { rootMargin: '240px' });
    if (this.observedSentinel) this.observer.observe(this.observedSentinel);
    this.destroyRef.onDestroy(() => this.observer?.disconnect());
  }

  load(initial = false): void {
    if (!this.alias || this.loadingMore()) return;
    if (initial) this.initialLoading.set(true); else this.loadingMore.set(true);
    this.error.set('');
    const cursor = initial ? undefined : this.page()?.nextCursor ?? undefined;
    const requestedAlias = this.alias;
    const requestId = ++this.requestSequence;
    this.api.publicLeaderboard(this.alias, cursor, false, this.scope(), this.scope() === 'Season' ? this.selectedSeasonId() ?? undefined : undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .pipe(finalize(() => { if (requestId === this.requestSequence) { this.initialLoading.set(false); this.loadingMore.set(false); } }))
      .subscribe({
        next: response => {
          if (requestedAlias !== this.alias || requestId !== this.requestSequence) return;
          if (!this.hasExplicitScope && (response.seasonsEnabled || response.currentSeason))
          {
            this.navigateScope('CurrentSeason', null);
            return;
          }
          this.page.set(response);
          const known = new Set(this.entries().map(entry => entry.userId));
          this.entries.update(entries => [...entries, ...response.items.filter(entry => !known.has(entry.userId))]);
        },
        error: response => {
          if (requestedAlias !== this.alias || requestId !== this.requestSequence) return;
           this.error.set(response.status === 401 ? this.i18n.translate('errors.leaderboardLogin') : response.status === 403 ? this.i18n.translate('errors.notMember') : this.apiErrors.resolve(response, 'errors.leaderboardLoad').message);
        },
      });
  }

  loadMore(): void {
    if (this.page()?.hasMore && !this.initialLoading() && !this.loadingMore() && !this.jumping()) this.load();
  }

  jumpToMe(): void {
    if (!this.page()?.isMember || this.jumping() || this.loadingMore()) return;
    this.jumping.set(true);
    this.error.set('');
    const requestedAlias = this.alias;
    const requestId = ++this.requestSequence;
    this.api.publicLeaderboard(this.alias, undefined, true, this.scope(), this.scope() === 'Season' ? this.selectedSeasonId() ?? undefined : undefined).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => { if (requestId === this.requestSequence) this.jumping.set(false); })).subscribe({
      next: response => {
        if (requestedAlias !== this.alias || requestId !== this.requestSequence) return;
        this.page.set(response);
        this.entries.set(response.items);
        if (!response.items.some(entry => entry.isCurrentUser)) {
          this.error.set(this.i18n.translate('errors.noXpEntry'));
          return;
        }
        setTimeout(() => document.querySelector<HTMLElement>('[data-current-user="true"]')?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
      },
       error: error => { if (requestedAlias === this.alias && requestId === this.requestSequence) this.error.set(this.apiErrors.resolve(error, 'errors.rankLoad').message); },
    });
  }

  setPrivacy(publicVisible: boolean): void {
    if (this.privacyBusy()) return;
    this.privacyBusy.set(true);
    const requestedAlias = this.alias;
    this.api.setLeaderboardPrivacy(this.alias, publicVisible).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => { if (requestedAlias === this.alias) this.privacyBusy.set(false); })).subscribe({
      next: () => { if (requestedAlias === this.alias) this.page.update(page => page ? { ...page, publicVisible } : page); },
       error: error => { if (requestedAlias === this.alias) this.error.set(this.apiErrors.resolve(error, 'errors.privacySave').message); },
    });
  }

  selectScope(scope: SeasonLeaderboardScope): void {
    if (scope === 'Season')
    {
      const seasonId = this.selectedSeasonId() ?? this.page()?.historicalSeasons?.[0]?.id;
      if (!seasonId) return;
      this.navigateScope(scope, seasonId);
      return;
    }
    this.navigateScope(scope, null);
  }

  selectHistoricalSeason(seasonId: string): void { this.navigateScope('Season', seasonId); }

  private navigateScope(scope: SeasonLeaderboardScope, seasonId: string | null): void {
    // Keep an explicit Lifetime choice distinct from a fresh navbar navigation without scope.
    void this.router.navigate([], { relativeTo: this.route, queryParams: { scope, seasonId }, queryParamsHandling: '' });
  }

  formatNumber(value: string | number): string { return this.locale.number(value); }
  messageCount(value: string | number): string { return this.locale.plural(value, 'leaderboard.messageOne', 'leaderboard.messageOther'); }
  trackEntry(_: number, entry: LeaderboardEntry): string { return entry.userId; }
}
