import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, finalize, of } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { LocaleService } from '../../i18n/locale.service';
import { BotManagementGuild, BotManagementOverview, BotManagementRange, BotManagementStatus } from './bot-management.models';
import { BotManagementService } from './bot-management.service';

@Component({ selector: 'app-bot-management', standalone: true, imports: [CommonModule, TranslocoPipe], templateUrl: './bot-management.component.html', styleUrl: './bot-management.component.scss' })
export class BotManagementComponent {
  private readonly api = inject(BotManagementService); private readonly route = inject(ActivatedRoute); private readonly router = inject(Router); private readonly destroyRef = inject(DestroyRef);
  private readonly errors = inject(ApiErrorService); readonly locale = inject(LocaleService); readonly i18n = inject(TranslocoService);
  readonly overview = signal<BotManagementOverview | null>(null); readonly loading = signal(true); readonly refreshing = signal(false); readonly error = signal<string | null>(null);
  readonly range = signal<BotManagementRange>('7d'); readonly search = signal(''); readonly status = signal<BotManagementStatus | 'all'>('all'); readonly sort = signal<'name' | 'members' | 'joined' | 'last' | 'activity' | 'intensity' | 'errors' | 'status'>('activity'); readonly descending = signal(true);
  readonly guilds = computed(() => { const search = this.search().trim().toLowerCase(); const status = this.status(); const factor = this.descending() ? -1 : 1; const key = this.sort(); return (this.overview()?.guilds ?? []).filter(g => (!search || g.name.toLowerCase().includes(search) || g.guildId.includes(search)) && (status === 'all' || g.status === status)).sort((a, b) => this.value(a, key).localeCompare(this.value(b, key), undefined, { numeric: true }) * factor); });
  constructor() { this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => { const value = params.get('range'); if (value === '24h' || value === '7d' || value === '30d' || value === '90d') this.range.set(value); this.load(); }); }
  load(): void { if (this.refreshing()) return; this.error.set(null); this.refreshing.set(true); if (!this.overview()) this.loading.set(true); this.api.getOverview(this.range()).pipe(catchError(error => { this.error.set(this.errors.resolve(error, 'errors.botManagementLoad').message); return of(null); }), finalize(() => { this.loading.set(false); this.refreshing.set(false); }), takeUntilDestroyed(this.destroyRef)).subscribe(value => { if (value) this.overview.set(value); }); }
  changeRange(range: BotManagementRange): void { if (range !== this.range()) void this.router.navigate([], { relativeTo: this.route, queryParams: { range } }); }
  setSort(key: typeof this.sort extends () => infer T ? T : never): void { if (this.sort() === key) this.descending.update(value => !value); else { this.sort.set(key); this.descending.set(key !== 'name'); } }
  initials(guild: BotManagementGuild): string { return guild.name.trim().split(/\s+/).slice(0, 2).map(value => value[0]).join('').toUpperCase() || '?'; }
  usage(guild: BotManagementGuild): number { return guild.activityEventCount + guild.commandEventCount; }
  date(value: string | null, empty: string): string { return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : this.i18n.translate(empty); }
  statusLabel(status: BotManagementStatus): string { return this.i18n.translate(`botManagement.status.${status}`); }
  private value(guild: BotManagementGuild, key: string): string { return String(key === 'name' ? guild.name : key === 'members' ? guild.memberCount : key === 'joined' ? guild.botJoinedAt ?? '' : key === 'last' ? guild.lastActivityAt ?? '' : key === 'activity' ? this.usage(guild) : key === 'intensity' ? guild.activityPerHundredMembers : key === 'errors' ? guild.errorEventCount : guild.status); }
}
