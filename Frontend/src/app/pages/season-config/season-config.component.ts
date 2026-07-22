import { CommonModule } from '@angular/common';
import { Component, ElementRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, finalize, forkJoin, Observable, of } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService, Season, SeasonPreview, SeasonSettings } from '../../services/guild.service';
import { LocaleService } from '../../i18n/locale.service';
import { AppStore } from '../../store/app.store';
import { ToastService } from '../../services/toast.service';

type SeasonAction = 'start' | 'close' | 'cancel' | 'resume' | 'delete';
type PendingAction = { action: SeasonAction; season: Season } | { action: 'disable'; season: null };

export function defaultSeasonSettings(): SeasonSettings {
  return {
    enabled: false, defaultLeaderboardScope: 'Lifetime', timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', scheduleKind: 'Manual',
    scheduleAnchorUtc: null, fixedDurationDays: 7, gapDays: 0, preparedSeasonCount: 3, pauseBehavior: 'NoSeasonXp', publicHistoryCount: 3,
    initialXpMode: 'Zero', initialXpPercentage: 0, carryOverMode: 'None', carryOverPercentage: 0, carryOverMaximumXp: null,
    announcementChannelId: null, announcements: { startEnabled: false, endEnabled: false, winnerEnabled: false, warningOffsetsMinutes: [] },
    winnerCount: 3, nameTemplate: 'Season {number}', rotation: [], rotationOffset: 0, seasonLevelRoles: [],
  };
}

@Component({
  selector: 'app-season-config',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  templateUrl: './season-config.component.html',
  styleUrls: ['./season-config.component.scss'],
})
export class SeasonConfigComponent implements OnInit {
  private readonly store = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);
  @ViewChild('confirmDialog') private readonly confirmDialog?: ElementRef<HTMLDialogElement>;

  readonly settings = signal<SeasonSettings | null>(null);
  readonly seasons = signal<Season[]>([]);
  readonly preview = signal<SeasonPreview[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly previewing = signal(false);
  readonly planning = signal(false);
  readonly actionBusy = signal(false);
  readonly error = signal('');
  readonly advanced = signal(false);
  readonly pending = signal<PendingAction | null>(null);
  rotationInput = '';

  ngOnInit(): void { this.load(); }

  load(): void {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId) { this.loading.set(false); this.error.set(this.i18n.translate('errors.noServer')); return; }
    this.loading.set(true); this.error.set('');
    forkJoin({ settings: this.api.seasonConfig(guildId), seasons: this.api.seasons(guildId) })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({ next: value => { this.settings.set(this.fromApi(value.settings)); this.seasons.set(value.seasons); this.refreshPreview(); }, error: error => this.error.set(this.apiErrors.resolve(error, 'errors.seasonsLoad').message) });
  }

  save(): void {
    const settings = this.settings();
    if (!settings || !this.valid(settings)) return;
    if (!settings.enabled && this.seasons().some(season => this.isActive(season))) {
      this.pending.set({ action: 'disable', season: null });
      this.confirmDialog?.nativeElement.showModal();
      return;
    }
    this.persistSettings();
  }

  private persistSettings(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings || !this.valid(settings)) return;
    this.saving.set(true); this.error.set('');
    this.api.saveSeasonConfig(guildId, this.toRequest(settings)).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: saved => {
        this.settings.set(this.fromApi(saved)); this.toast.success(this.i18n.translate('seasons.saved')); this.refreshPreview();
        this.api.seasons(guildId).subscribe(items => this.seasons.set(items));
      },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.seasonSave').message),
    });
  }

  refreshPreview(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings || settings.scheduleKind === 'Manual' || !this.valid(settings)) { this.preview.set([]); return; }
    this.previewing.set(true);
    this.api.previewSeasons(guildId, this.toRequest(settings)).pipe(finalize(() => this.previewing.set(false))).subscribe({
      next: preview => this.preview.set(preview), error: error => { this.preview.set([]); this.error.set(this.apiErrors.resolve(error, 'errors.seasonPreview').message); },
    });
  }

  addRotation(): void {
    const value = this.rotationInput.trim();
    const settings = this.settings();
    if (!settings || !value) return;
    settings.rotation.push(value); this.rotationInput = ''; this.refreshPreview();
  }

  removeRotation(index: number): void { this.settings()?.rotation.splice(index, 1); this.refreshPreview(); }

  plan(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings || settings.scheduleKind === 'Manual' || !this.valid(settings)) return;
    this.planning.set(true); this.error.set('');
    this.api.planSeasons(guildId, settings.preparedSeasonCount).pipe(finalize(() => this.planning.set(false))).subscribe({
      next: planned => { this.seasons.update(items => [...planned, ...items]); this.toast.success(this.i18n.translate('seasons.planSucceeded', { count: planned.length })); },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.seasonPlan').message),
    });
  }

  requestAction(action: SeasonAction, season: Season): void {
    this.pending.set({ action, season });
    this.confirmDialog?.nativeElement.showModal();
  }

  confirmAction(): void {
    const guildId = this.store.selectedGuild()?.id;
    const pending = this.pending();
    if (!guildId || !pending) return;
    if (pending.action === 'disable') {
      this.confirmDialog?.nativeElement.close(); this.pending.set(null); this.persistSettings();
      return;
    }
    if (!pending.season.id) return;
    const request: Observable<unknown> = pending.action === 'start' ? this.api.startSeason(guildId, pending.season.id)
      : pending.action === 'close' ? this.api.closeSeason(guildId, pending.season.id)
      : pending.action === 'resume' ? this.api.resumeSeason(guildId, pending.season.id)
      : pending.action === 'delete' ? this.api.deleteSeason(guildId, pending.season.id)
      : this.api.cancelSeason(guildId, pending.season.id);
    this.actionBusy.set(true); this.error.set('');
    request.pipe(finalize(() => this.actionBusy.set(false))).subscribe({
      next: () => { this.confirmDialog?.nativeElement.close(); this.pending.set(null); this.toast.success(this.i18n.translate(`seasons.${pending.action}Succeeded`)); this.load(); },
      error: error => { this.confirmDialog?.nativeElement.close(); this.pending.set(null); this.toast.error(this.apiErrors.resolve(error, 'errors.seasonAction').message); },
    });
  }

  closeDialog(): void { this.confirmDialog?.nativeElement.close(); this.pending.set(null); }
  isActive(season: Season): boolean { return season.status === 'Active' || season.status === 'Closing'; }
  isCancelled(season: Season): boolean { return season.status.toLowerCase() === 'cancelled'; }
  isReopenable(season: Season): boolean { const status = season.status.toLowerCase(); return status === 'cancelled' || status === 'closed'; }
  isResumable(season: Season): boolean { const now = Date.now(); return this.isReopenable(season) && new Date(season.startsAtUtc).getTime() <= now && now < new Date(season.endsAtUtc).getTime(); }
  formatDate(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }); }
  statusKey(status: string): string { return `seasons.status.${status.toLowerCase()}`; }
  valid(settings: SeasonSettings): boolean {
    return !!settings.timeZoneId.trim() && !!settings.nameTemplate.trim() && settings.gapDays >= 0 && settings.preparedSeasonCount >= 0
      && settings.publicHistoryCount >= 0 && settings.winnerCount >= 1 && settings.initialXpPercentage >= 0 && settings.carryOverPercentage >= 0
      && (settings.scheduleKind !== 'FixedDuration' || (settings.fixedDurationDays ?? 0) > 0)
      && (settings.scheduleKind === 'Manual' || !!settings.scheduleAnchorUtc)
      && (!settings.nameTemplate.includes('{rotation}') || settings.rotation.every(value => !!value.trim()) && settings.rotation.length > 0);
  }

  private toRequest(settings: SeasonSettings): SeasonSettings {
    return { ...settings, scheduleAnchorUtc: settings.scheduleAnchorUtc ? new Date(settings.scheduleAnchorUtc).toISOString() : null };
  }

  private fromApi(settings: SeasonSettings): SeasonSettings {
    if (!settings.scheduleAnchorUtc) return settings;
    const local = new Date(settings.scheduleAnchorUtc);
    const pad = (value: number) => value.toString().padStart(2, '0');
    return { ...settings, scheduleAnchorUtc: `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}T${pad(local.getHours())}:${pad(local.getMinutes())}` };
  }
}
