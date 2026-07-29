import { CommonModule } from '@angular/common';
import { Component, effect, HostListener, inject, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, finalize, forkJoin, Observable, of } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { ApiValidationItem } from '../../models/api-error.model';
import { ApiErrorService } from '../../services/api-error.service';
import {
  GuildResources,
  GuildService,
  Season,
  SeasonInitialXpMode,
  SeasonPreview,
  SeasonScheduleKind,
  SeasonSettings,
} from '../../services/guild.service';
import { ToastService } from '../../services/toast.service';
import { AppStore, Guild } from '../../store/app.store';
import { DiscordChannelPickerComponent } from '../../shared/ui/discord-channel-picker/discord-channel-picker.component';
import { DiscordChannelOption, normalizeDiscordChannels } from '../../shared/ui/discord-channel-picker/discord-channel.models';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog/confirmation-dialog.component';
import { StickySaveBarComponent } from '../../shared/ui/sticky-save-bar/sticky-save-bar.component';
import { SeasonInstanceListComponent } from './components/season-instance-list.component';
import { SeasonStatusSummaryComponent } from './components/season-status-summary.component';
import { SeasonTimelinePreviewComponent } from './components/season-timeline-preview.component';

export type SeasonAction = 'start' | 'close' | 'cancel' | 'resume' | 'delete';
type PendingAction = { action: SeasonAction; season: Season };
export type SchedulePreset = 'monthly' | 'quarterly' | 'custom';

const SUPPORTED_TOKENS = ['{number}', '{year}', '{endYear}', '{month}', '{monthName}', '{quarter}', '{rotation}', '{start:yyyy-MM-dd}', '{end:yyyy-MM-dd}'] as const;
const TOKEN_PATTERN = /\{([^{}]+)\}/g;

export function defaultSeasonSettings(): SeasonSettings {
  return {
    enabled: false,
    defaultLeaderboardScope: 'Lifetime',
    timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
    scheduleKind: 'FixedDuration',
    scheduleAnchorUtc: null,
    fixedDurationDays: 30,
    gapDays: 0,
    preparedSeasonCount: 3,
    pauseBehavior: 'NoSeasonXp',
    publicHistoryCount: 3,
    initialXpMode: 'Zero',
    initialXpPercentage: 0,
    carryOverMode: 'None',
    carryOverPercentage: 0,
    carryOverMaximumXp: null,
    announcementChannelId: null,
    announcements: { startEnabled: false, endEnabled: false, winnerEnabled: false, warningOffsetsMinutes: [] },
    winnerCount: 3,
    nameTemplate: 'Season {number}',
    rotation: [],
    rotationOffset: 0,
    seasonLevelRoles: [],
  };
}

@Component({
  selector: 'app-season-config',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TranslocoPipe,
    DiscordChannelPickerComponent,
    ConfirmationDialogComponent,
    StickySaveBarComponent,
    SeasonInstanceListComponent,
    SeasonStatusSummaryComponent,
    SeasonTimelinePreviewComponent,
  ],
  templateUrl: './season-config.component.html',
  styleUrls: ['./season-config.component.scss'],
})
export class SeasonConfigComponent {
  private readonly store = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);

  @ViewChild(ConfirmationDialogComponent) private readonly confirmDialog?: ConfirmationDialogComponent;

  readonly settings = signal<SeasonSettings | null>(null);
  readonly resources = signal<GuildResources>({ roles: [], channels: [] });
  readonly seasons = signal<Season[]>([]);
  readonly preview = signal<SeasonPreview[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly previewing = signal(false);
  readonly planning = signal(false);
  readonly actionBusy = signal(false);
  readonly loadError = signal('');
  readonly pageError = signal('');
  readonly advanced = signal(false);
  readonly pending = signal<PendingAction | null>(null);
  readonly serverErrors = signal<Record<string, string>>({});
  readonly activeTab = signal<'planned' | 'past'>('planned');
  readonly supportedTokens = SUPPORTED_TOKENS;

  rotationInput = '';
  private baseline = '';
  private loadRequest = 0;
  private previewRequest = 0;
  private loadedGuild: Guild | null = null;

  constructor() {
    effect(() => {
      const selected = this.store.selectedGuild();
      if (this.loadedGuild && selected?.id !== this.loadedGuild.id && this.dirty() && !window.confirm(this.i18n.translate('seasons.unsavedLeave'))) {
        this.store.setSelectedGuild(this.loadedGuild);
        return;
      }
      this.load();
    });
  }

  @HostListener('window:beforeunload', ['$event'])
  protectUnsavedChanges(event: BeforeUnloadEvent): void {
    if (!this.dirty()) return;
    event.preventDefault();
    event.returnValue = '';
  }

  canDeactivate(): boolean {
    return !this.dirty() || window.confirm(this.i18n.translate('seasons.unsavedLeave'));
  }

  load(): void {
    const guildId = this.store.selectedGuild()?.id;
    const request = ++this.loadRequest;
    if (!guildId) {
      this.loading.set(false);
      this.loadError.set(this.i18n.translate('errors.noServer'));
      return;
    }

    this.loading.set(true);
    this.loadError.set('');
    this.pageError.set('');
    forkJoin({
      settings: this.api.seasonConfig(guildId),
      seasons: this.api.seasons(guildId),
      resources: this.api.resources(guildId).pipe(catchError(() => of({ roles: [], channels: [] }))),
    }).pipe(finalize(() => {
      if (request === this.loadRequest) this.loading.set(false);
    })).subscribe({
      next: value => {
        if (request !== this.loadRequest || this.store.selectedGuild()?.id !== guildId) return;
        const settings = this.fromApi(value.settings);
        this.settings.set(settings);
        this.baseline = this.serialize(settings);
        this.seasons.set(this.sortSeasons(value.seasons));
        this.resources.set(value.resources);
        this.loadedGuild = this.store.selectedGuild();
        this.serverErrors.set({});
        this.refreshPreview();
      },
      error: error => {
        if (request === this.loadRequest) this.loadError.set(this.apiErrors.resolve(error, 'errors.seasonsLoad').message);
      },
    });
  }

  save(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings || !this.dirty() || !this.valid(settings) || this.saving()) return;
    if (!settings.enabled && this.seasons().some(season => this.isActive(season))) {
      this.pageError.set(this.i18n.translate('seasons.validation.closeBeforeDisable'));
      return;
    }

    this.saving.set(true);
    this.pageError.set('');
    this.serverErrors.set({});
    const requestSnapshot = this.serialize(settings);
    this.api.saveSeasonConfig(guildId, this.toRequest(settings)).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: saved => {
        if (this.store.selectedGuild()?.id !== guildId) return;
        const normalized = this.fromApi(saved);
        this.baseline = this.serialize(normalized);
        if (this.settings() && this.serialize(this.settings()!) === requestSnapshot) this.settings.set(normalized);
        this.toast.success(this.i18n.translate('seasons.saved'));
        this.refreshPreview();
        this.reloadSeasons(guildId);
      },
      error: error => {
        const resolved = this.apiErrors.resolve(error, 'errors.seasonSave');
        this.serverErrors.set(this.mapServerErrors(resolved.validation));
        this.pageError.set(resolved.message);
        this.toast.error(resolved.message);
      },
    });
  }

  reset(): void {
    if (!this.baseline) return;
    this.settings.set(JSON.parse(this.baseline) as SeasonSettings);
    this.rotationInput = '';
    this.serverErrors.set({});
    this.pageError.set('');
    this.refreshPreview();
  }

  refreshPreview(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    const request = ++this.previewRequest;
    if (!guildId || !settings || settings.scheduleKind === 'Manual' || !this.scheduleValid(settings) || !this.namingValid(settings)) {
      this.preview.set([]);
      this.previewing.set(false);
      return;
    }

    this.previewing.set(true);
    this.api.previewSeasons(guildId, this.toRequest(settings), 3).pipe(finalize(() => {
      if (request === this.previewRequest) this.previewing.set(false);
    })).subscribe({
      next: preview => {
        if (request === this.previewRequest && this.store.selectedGuild()?.id === guildId) this.preview.set(preview);
      },
      error: error => {
        if (request !== this.previewRequest) return;
        this.preview.set([]);
        this.pageError.set(this.apiErrors.resolve(error, 'errors.seasonPreview').message);
      },
    });
  }

  setPreset(preset: SchedulePreset): void {
    const settings = this.settings();
    if (!settings) return;
    if (preset === 'monthly') settings.scheduleKind = 'Monthly';
    if (preset === 'quarterly') settings.scheduleKind = 'Quarterly';
    if (preset === 'custom' && settings.scheduleKind !== 'FixedDuration' && settings.scheduleKind !== 'Manual') settings.scheduleKind = 'FixedDuration';
    if (settings.scheduleKind === 'FixedDuration' && !settings.fixedDurationDays) settings.fixedDurationDays = 30;
    this.clearServerError('scheduleKind');
    this.refreshPreview();
  }

  preset(settings: SeasonSettings): SchedulePreset {
    if (settings.scheduleKind === 'Monthly' && settings.gapDays === 0) return 'monthly';
    if (settings.scheduleKind === 'Quarterly' && settings.gapDays === 0) return 'quarterly';
    return 'custom';
  }

  setGapEnabled(enabled: boolean): void {
    const settings = this.settings();
    if (!settings) return;
    settings.gapDays = enabled ? Math.max(1, Number(settings.gapDays) || 1) : 0;
    this.clearServerError('gapDays');
    this.refreshPreview();
  }

  addRotation(): void {
    const settings = this.settings();
    const value = this.rotationInput.trim();
    if (!settings || !value) return;
    settings.rotation.push(value);
    this.rotationInput = '';
    this.clearServerError('rotation');
    this.refreshPreview();
  }

  removeRotation(index: number): void {
    this.settings()?.rotation.splice(index, 1);
    this.refreshPreview();
  }

  moveRotation(index: number, direction: -1 | 1): void {
    const rotation = this.settings()?.rotation;
    const target = index + direction;
    if (!rotation || target < 0 || target >= rotation.length) return;
    [rotation[index], rotation[target]] = [rotation[target], rotation[index]];
    this.refreshPreview();
  }

  insertToken(input: HTMLInputElement, token: string): void {
    const settings = this.settings();
    if (!settings) return;
    const start = input.selectionStart ?? settings.nameTemplate.length;
    const end = input.selectionEnd ?? start;
    settings.nameTemplate = `${settings.nameTemplate.slice(0, start)}${token}${settings.nameTemplate.slice(end)}`;
    this.clearServerError('nameTemplate');
    queueMicrotask(() => {
      input.focus();
      input.setSelectionRange(start + token.length, start + token.length);
    });
    this.refreshPreview();
  }

  namePreview(settings: SeasonSettings, count = 5): string[] {
    if (!this.namingValid(settings)) return [];
    const first = this.preview()[0];
    return Array.from({ length: count }, (_, index) => {
      const item = this.preview()[index] ?? first;
      const sequence = Number(item?.sequence ?? index + 1);
      const rotationIndex = settings.rotation.length ? ((sequence - 1 + settings.rotationOffset) % settings.rotation.length + settings.rotation.length) % settings.rotation.length : 0;
      const rotation = settings.rotation.length ? settings.rotation[rotationIndex] : '';
      const start = item ? new Date(item.startsAtUtc) : new Date();
      const end = item ? new Date(item.endsAtUtc) : start;
      return settings.nameTemplate.replace(TOKEN_PATTERN, (_, token: string) => {
        if (token === 'number') return String(sequence);
        if (/^number:0+$/.test(token)) return String(sequence).padStart(token.length - 7, '0');
        if (token === 'rotation') return rotation;
        if (token === 'year' || token === 'endYear') return String(start.getFullYear());
        if (token === 'month') return String(start.getMonth() + 1);
        if (token === 'monthName') return this.locale.date(start, { month: 'long' });
        if (token === 'quarter') return String(Math.floor(start.getMonth() / 3) + 1);
        if (/^start:[yMd-]+$/.test(token)) return this.locale.date(start, { year: 'numeric', month: '2-digit', day: '2-digit' });
        if (/^end:[yMd-]+$/.test(token)) return this.locale.date(end, { year: 'numeric', month: '2-digit', day: '2-digit' });
        return `{${token}}`;
      });
    });
  }

  namingMode(settings: SeasonSettings): 'numbered' | 'pattern' | 'rotation' {
    if (settings.nameTemplate.includes('{rotation}')) return 'rotation';
    return settings.nameTemplate === 'Season {number}' ? 'numbered' : 'pattern';
  }

  setNamingMode(mode: 'numbered' | 'pattern' | 'rotation'): void {
    const settings = this.settings();
    if (!settings) return;
    if (mode === 'numbered') settings.nameTemplate = 'Season {number}';
    if (mode === 'pattern' && (settings.nameTemplate === 'Season {number}' || settings.nameTemplate.includes('{rotation}'))) settings.nameTemplate = 'Season {number} · {year}';
    if (mode === 'rotation' && !settings.nameTemplate.includes('{rotation}')) settings.nameTemplate = 'Season {number} · {rotation}';
    this.refreshPreview();
  }

  plan(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings || this.dirty() || settings.scheduleKind === 'Manual' || !this.valid(settings) || this.planning()) return;
    this.planning.set(true);
    this.api.planSeasons(guildId, settings.preparedSeasonCount).pipe(finalize(() => this.planning.set(false))).subscribe({
      next: planned => {
        this.toast.success(this.i18n.translate('seasons.planSucceeded', { count: planned.length }));
        this.reloadSeasons(guildId);
      },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.seasonPlan').message),
    });
  }

  requestAction(action: SeasonAction, season: Season): void {
    if (this.dirty()) {
      this.pageError.set(this.i18n.translate('seasons.unsaved'));
      return;
    }
    this.pending.set({ action, season });
    this.confirmDialog?.open();
  }

  confirmAction(): void {
    const guildId = this.store.selectedGuild()?.id;
    const pending = this.pending();
    if (!guildId || !pending?.season.id || this.actionBusy()) return;
    const request: Observable<unknown> = pending.action === 'start' ? this.api.startSeason(guildId, pending.season.id)
      : pending.action === 'close' ? this.api.closeSeason(guildId, pending.season.id)
      : pending.action === 'resume' ? this.api.resumeSeason(guildId, pending.season.id)
      : pending.action === 'delete' ? this.api.deleteSeason(guildId, pending.season.id)
      : this.api.cancelSeason(guildId, pending.season.id);
    this.actionBusy.set(true);
    request.pipe(finalize(() => this.actionBusy.set(false))).subscribe({
      next: () => {
        this.confirmDialog?.close();
        this.pending.set(null);
        this.toast.success(this.i18n.translate(`seasons.${pending.action}Succeeded`));
        this.reloadAfterAction(guildId);
      },
      error: error => {
        this.confirmDialog?.close();
        this.pending.set(null);
        this.toast.error(this.apiErrors.resolve(error, 'errors.seasonAction').message);
      },
    });
  }

  closeDialog(): void {
    this.confirmDialog?.close();
    this.pending.set(null);
  }

  actionTitle(): string {
    const action = this.pending()?.action;
    return action ? this.i18n.translate(`seasons.confirmTitle.${action}`) : '';
  }

  actionDescription(): string {
    const pending = this.pending();
    return pending ? this.i18n.translate(`seasons.confirm.${pending.action}`, { name: pending.season.name }) : '';
  }

  actionImpact(): string {
    const action = this.pending()?.action;
    return action ? this.i18n.translate(`seasons.confirmImpact.${action}`) : '';
  }

  dirty(): boolean {
    const settings = this.settings();
    return !!settings && this.serialize(settings) !== this.baseline;
  }

  valid(settings: SeasonSettings): boolean {
    return this.validationKeys(settings).length === 0;
  }

  validationKeys(settings: SeasonSettings): string[] {
    const errors = new Set<string>();
    if (!settings.enabled) return [];
    if (!settings.timeZoneId?.trim()) errors.add('seasons.validation.timeZone');
    if (settings.scheduleKind !== 'Manual' && !settings.scheduleAnchorUtc) errors.add('seasons.validation.anchor');
    if (settings.scheduleKind === 'FixedDuration' && (!Number.isInteger(Number(settings.fixedDurationDays)) || Number(settings.fixedDurationDays) < 1 || Number(settings.fixedDurationDays) > 3660)) errors.add('seasons.validation.duration');
    if (!Number.isInteger(Number(settings.gapDays)) || Number(settings.gapDays) < 0 || this.gapMaximum(settings) < Number(settings.gapDays)) errors.add('seasons.validation.gap');
    if (!Number.isInteger(Number(settings.preparedSeasonCount)) || Number(settings.preparedSeasonCount) < 0 || Number(settings.preparedSeasonCount) > 24) errors.add('seasons.validation.prepared');
    if (!Number.isInteger(Number(settings.publicHistoryCount)) || Number(settings.publicHistoryCount) < 0 || Number(settings.publicHistoryCount) > 24) errors.add('seasons.validation.history');
    if (!Number.isInteger(Number(settings.winnerCount)) || Number(settings.winnerCount) < 1 || Number(settings.winnerCount) > 100) errors.add('seasons.validation.winners');
    if (!this.percentageValid(settings.initialXpPercentage)) errors.add('seasons.validation.percentage');
    if (!this.percentageValid(settings.carryOverPercentage)) errors.add('seasons.validation.percentage');
    if (settings.carryOverMaximumXp != null && Number(settings.carryOverMaximumXp) < 0) errors.add('seasons.validation.maximumXp');
    if (!this.namingValid(settings)) {
      if (this.unknownTokens(settings).length) errors.add('seasons.validation.tokens');
      else if (settings.nameTemplate.includes('{rotation}') && !settings.rotation.length) errors.add('seasons.validation.rotationRequired');
      else if (this.hasDuplicateRotation(settings)) errors.add('seasons.validation.rotationDuplicate');
      else errors.add('seasons.validation.nameTemplate');
    }
    return [...errors];
  }

  fieldError(settings: SeasonSettings, field: string): string {
    if (this.serverErrors()[field]) return this.serverErrors()[field];
    if (field === 'scheduleAnchorUtc' && settings.scheduleKind !== 'Manual' && !settings.scheduleAnchorUtc) return this.i18n.translate('seasons.validation.anchor');
    if (field === 'fixedDurationDays' && settings.scheduleKind === 'FixedDuration' && (!Number.isInteger(Number(settings.fixedDurationDays)) || Number(settings.fixedDurationDays) < 1 || Number(settings.fixedDurationDays) > 3660)) return this.i18n.translate('seasons.validation.duration');
    if (field === 'gapDays' && (!Number.isInteger(Number(settings.gapDays)) || Number(settings.gapDays) < 0 || this.gapMaximum(settings) < Number(settings.gapDays))) return this.i18n.translate('seasons.validation.gap');
    if (field === 'nameTemplate' && this.unknownTokens(settings).length) return this.i18n.translate('seasons.validation.tokens', { tokens: this.unknownTokens(settings).join(', ') });
    if (field === 'rotation' && settings.nameTemplate.includes('{rotation}') && !settings.rotation.length) return this.i18n.translate('seasons.validation.rotationRequired');
    if (field === 'rotation' && this.hasDuplicateRotation(settings)) return this.i18n.translate('seasons.validation.rotationDuplicate');
    return '';
  }

  stepComplete(step: number, settings: SeasonSettings): boolean {
    if (step === 1) return settings.enabled;
    if (!settings.enabled) return false;
    if (step === 2) return this.scheduleValid(settings);
    if (step === 3) return this.namingValid(settings);
    if (step === 4) return this.percentageValid(settings.initialXpPercentage) && this.percentageValid(settings.carryOverPercentage);
    return this.valid(settings);
  }

  scheduleValid(settings: SeasonSettings): boolean {
    return !!settings.timeZoneId?.trim()
      && (settings.scheduleKind === 'Manual' || !!settings.scheduleAnchorUtc)
      && (settings.scheduleKind !== 'FixedDuration' || Number(settings.fixedDurationDays) >= 1 && Number(settings.fixedDurationDays) <= 3660)
      && Number(settings.gapDays) >= 0 && Number(settings.gapDays) <= this.gapMaximum(settings);
  }

  namingValid(settings: SeasonSettings): boolean {
    return !!settings.nameTemplate?.trim()
      && settings.nameTemplate.length <= 120
      && this.unknownTokens(settings).length === 0
      && settings.rotation.every(name => !!name.trim() && name === name.trim())
      && !this.hasDuplicateRotation(settings)
      && (!settings.nameTemplate.includes('{rotation}') || settings.rotation.length > 0);
  }

  gapMaximum(settings: SeasonSettings): number {
    if (settings.scheduleKind === 'Monthly') return 27;
    if (settings.scheduleKind === 'Quarterly') return 83;
    if (settings.scheduleKind === 'SemiAnnual') return 167;
    if (settings.scheduleKind === 'Annual') return 335;
    return 3660;
  }

  initialExample(settings: SeasonSettings): number {
    if (settings.initialXpMode === 'Zero') return 0;
    if (settings.initialXpMode === 'Lifetime') return 10000;
    return Math.round(10000 * Number(settings.initialXpPercentage) / 100);
  }

  carryExample(settings: SeasonSettings): number {
    return settings.carryOverMode === 'Percentage' ? Math.round(10000 * Number(settings.carryOverPercentage) / 100) : 0;
  }

  setInitialMode(mode: SeasonInitialXpMode): void {
    const settings = this.settings();
    if (settings) settings.initialXpMode = mode;
  }

  currentSeason(): Season | null {
    return this.seasons().find(season => this.isActive(season)) ?? null;
  }

  nextSeason(): Season | null {
    const now = Date.now();
    return [...this.seasons()].filter(season => season.status === 'Scheduled' && new Date(season.endsAtUtc).getTime() > now).sort((a, b) => new Date(a.startsAtUtc).getTime() - new Date(b.startsAtUtc).getTime())[0] ?? null;
  }

  operationSeasons(): Season[] {
    return this.seasons().filter(season => season.status === 'Scheduled' || season.status === 'Active' || this.isResumable(season) || season.status === 'Cancelled');
  }

  availableActions(season: Season): SeasonAction[] {
    if (this.dirty()) return [];
    const result: SeasonAction[] = [];
    if (season.status === 'Scheduled') result.push('start', 'cancel', 'delete');
    if (season.status === 'Active') result.push('close', 'cancel');
    if (this.isResumable(season)) result.push('resume');
    if (season.status === 'Cancelled') result.push('delete');
    return result;
  }

  isActive(season: Season): boolean { return season.status === 'Active' || season.status === 'Closing'; }
  isResumable(season: Season): boolean {
    const now = Date.now();
    return season.status === 'Cancelled' && !season.carryOverApplied && !season.finalized
      && new Date(season.startsAtUtc).getTime() <= now && now < new Date(season.endsAtUtc).getTime();
  }
  actionIsDangerous(action: SeasonAction): boolean { return action === 'close' || action === 'cancel' || action === 'delete'; }
  formatDate(value: string | Date): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }); }
  formatNumber(value: number): string { return this.locale.number(value); }
  channelOptions(): DiscordChannelOption[] { return normalizeDiscordChannels(this.resources().channels); }

  scrollToActivation(): void {
    document.getElementById('season-step-1')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    document.getElementById('season-enabled')?.focus();
  }

  private percentageValid(value: number): boolean { return Number.isFinite(Number(value)) && Number(value) >= 0 && Number(value) <= 100; }
  private hasDuplicateRotation(settings: SeasonSettings): boolean { return new Set(settings.rotation.map(name => name.trim().toLocaleLowerCase())).size !== settings.rotation.length; }
  private unknownTokens(settings: SeasonSettings): string[] {
    return [...settings.nameTemplate.matchAll(TOKEN_PATTERN)].map(match => match[1]).filter(token => !['number', 'year', 'endYear', 'month', 'monthName', 'quarter', 'rotation'].includes(token) && !/^number:0+$/.test(token) && !/^(start|end):[yMd-]+$/.test(token));
  }

  private clearServerError(field: string): void {
    this.serverErrors.update(errors => {
      const next = { ...errors };
      delete next[field];
      return next;
    });
  }

  private mapServerErrors(validation: ApiValidationItem[]): Record<string, string> {
    return validation.reduce<Record<string, string>>((result, item) => {
      if (item.field && !result[item.field]) result[item.field] = item.message ?? this.i18n.translate('seasons.validation.generic');
      return result;
    }, {});
  }

  private reloadSeasons(guildId: string): void {
    this.api.seasons(guildId).subscribe({
      next: items => { if (this.store.selectedGuild()?.id === guildId) this.seasons.set(this.sortSeasons(items)); },
      error: error => this.pageError.set(this.apiErrors.resolve(error, 'errors.seasonsLoad').message),
    });
  }

  private reloadAfterAction(guildId: string): void {
    forkJoin({ settings: this.api.seasonConfig(guildId), seasons: this.api.seasons(guildId) }).subscribe({
      next: value => {
        if (this.store.selectedGuild()?.id !== guildId) return;
        const settings = this.fromApi(value.settings);
        this.settings.set(settings);
        this.baseline = this.serialize(settings);
        this.seasons.set(this.sortSeasons(value.seasons));
        this.serverErrors.set({});
        this.refreshPreview();
      },
      error: error => this.pageError.set(this.apiErrors.resolve(error, 'errors.seasonsLoad').message),
    });
  }

  private sortSeasons(items: Season[]): Season[] { return [...items].sort((a, b) => Number(b.sequence) - Number(a.sequence)); }

  private toRequest(settings: SeasonSettings): SeasonSettings {
    return {
      ...settings,
      scheduleAnchorUtc: settings.scheduleAnchorUtc ? new Date(settings.scheduleAnchorUtc).toISOString() : null,
      fixedDurationDays: settings.fixedDurationDays == null ? null : Number(settings.fixedDurationDays),
      gapDays: Number(settings.gapDays),
      preparedSeasonCount: Number(settings.preparedSeasonCount),
      publicHistoryCount: Number(settings.publicHistoryCount),
      initialXpPercentage: Number(settings.initialXpPercentage),
      carryOverPercentage: Number(settings.carryOverPercentage),
      carryOverMaximumXp: settings.carryOverMaximumXp == null ? null : Number(settings.carryOverMaximumXp),
      winnerCount: Number(settings.winnerCount),
      rotationOffset: Number(settings.rotationOffset),
    };
  }

  private fromApi(settings: SeasonSettings): SeasonSettings {
    const result = structuredClone(settings);
    result.fixedDurationDays = result.fixedDurationDays == null ? null : Number(result.fixedDurationDays);
    result.gapDays = Number(result.gapDays);
    result.preparedSeasonCount = Number(result.preparedSeasonCount);
    result.publicHistoryCount = Number(result.publicHistoryCount);
    result.initialXpPercentage = Number(result.initialXpPercentage);
    result.carryOverPercentage = Number(result.carryOverPercentage);
    result.carryOverMaximumXp = result.carryOverMaximumXp == null ? null : Number(result.carryOverMaximumXp);
    result.winnerCount = Number(result.winnerCount);
    result.rotationOffset = Number(result.rotationOffset);
    result.rotation ??= [];
    result.announcements ??= { startEnabled: false, endEnabled: false, winnerEnabled: false, warningOffsetsMinutes: [] };
    result.seasonLevelRoles ??= [];
    if (result.scheduleAnchorUtc) {
      const local = new Date(result.scheduleAnchorUtc);
      const pad = (value: number) => value.toString().padStart(2, '0');
      result.scheduleAnchorUtc = `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}T${pad(local.getHours())}:${pad(local.getMinutes())}`;
    }
    return result;
  }

  private serialize(settings: SeasonSettings): string { return JSON.stringify(settings); }
}
