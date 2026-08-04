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
type SeasonBulkAction = 'cancelScheduled' | 'deleteCancelled' | 'resetCounter';
type PendingAction = { action: SeasonAction; season: Season; guildId: string } | { action: SeasonBulkAction; season: null; guildId: string };
export type SchedulePreset = 'monthly' | 'quarterly' | 'custom';

const SUPPORTED_TOKENS = ['{number}', '{year}', '{endYear}', '{month}', '{monthName}', '{quarter}', '{rotation}', '{start:yyyy-MM-dd}', '{end:yyyy-MM-dd}'] as const;
const TOKEN_PATTERN = /\{([^{}]+)\}/g;

export function defaultSeasonSettings(): SeasonSettings {
  return {
    enabled: false,
    defaultLeaderboardScope: 'Lifetime',
    timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
    scheduleKind: 'FixedDuration',
    planningMode: 'Explicit',
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

  readonly persistedSettings = signal<SeasonSettings | null>(null);
  readonly draftSettings = signal<SeasonSettings | null>(null);
  readonly settings = this.draftSettings;
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
  readonly supportedTokens = SUPPORTED_TOKENS;

  rotationInput = '';
  private baseline = '';
  private loadRequest = 0;
  private previewRequest = 0;
  private loadedGuild: Guild | null = null;
  private suppressNextGuildReload = false;
  private approvedGuildChangeId: string | null = null;
  initialSeasonCount = 3;
  additionalSeasonCount = 3;
  private setupOperationId: string | null = null;
  private setupRequestSnapshot: string | null = null;

  constructor() {
    effect(() => {
      const selected = this.store.selectedGuild();
      if (this.suppressNextGuildReload && selected?.id === this.loadedGuild?.id) {
        this.suppressNextGuildReload = false;
        return;
      }
      if (this.loadedGuild && selected?.id !== this.loadedGuild.id && this.dirty() && !window.confirm(this.i18n.translate('seasons.unsavedLeave'))) {
        this.suppressNextGuildReload = true;
        this.store.setSelectedGuild(this.loadedGuild);
        return;
      }
      if (this.loadedGuild && selected?.id !== this.loadedGuild.id) {
        this.approvedGuildChangeId = selected?.id ?? null;
        this.confirmDialog?.close();
        this.pending.set(null);
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
    return !this.dirty() || this.store.selectedGuild()?.id === this.approvedGuildChangeId || window.confirm(this.i18n.translate('seasons.unsavedLeave'));
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
        this.persistedSettings.set(structuredClone(settings));
        this.draftSettings.set(structuredClone(settings));
        this.baseline = this.serialize(settings);
        this.seasons.set(this.sortSeasons(value.seasons));
        this.resources.set(value.resources);
        this.loadedGuild = this.store.selectedGuild();
        this.approvedGuildChangeId = null;
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
        this.persistedSettings.set(structuredClone(normalized));
        this.baseline = this.serialize(normalized);
        if (this.settings() && this.serialize(this.settings()!) === requestSnapshot) this.draftSettings.set(structuredClone(normalized));
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
    this.draftSettings.set(JSON.parse(this.baseline) as SeasonSettings);
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
    this.api.previewSeasons(guildId, this.toRequest(settings), this.planningCount()).pipe(finalize(() => {
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
    const dateDependent = /\{(?:year|endYear|month|monthName|quarter|start:|end:)/.test(settings.nameTemplate);
    const previewCount = dateDependent ? Math.min(count, Math.max(1, this.preview().length)) : count;
    return Array.from({ length: previewCount }, (_, index) => {
      const exactItem = this.preview()[index];
      const item = exactItem ?? this.preview().at(-1) ?? first;
      const number = Number(exactItem?.number ?? exactItem?.sequence ?? Number(first?.number ?? first?.sequence ?? 1) + index);
      const rotationIndex = settings.rotation.length ? ((number - 1 + settings.rotationOffset) % settings.rotation.length + settings.rotation.length) % settings.rotation.length : 0;
      const rotation = settings.rotation.length ? settings.rotation[rotationIndex] : '';
      const start = item ? new Date(item.startsAtUtc) : new Date();
      const end = item ? new Date(item.endsAtUtc) : start;
      const startParts = this.seasonDateParts(start, settings.timeZoneId);
      const endParts = this.seasonDateParts(end, settings.timeZoneId);
      return settings.nameTemplate.replace(TOKEN_PATTERN, (_, token: string) => {
        if (token === 'number') return String(number);
        if (/^number:0+$/.test(token)) return String(number).padStart(token.length - 7, '0');
        if (token === 'rotation') return rotation;
        if (token === 'year') return startParts.year;
        if (token === 'endYear') return endParts.year;
        if (token === 'month') return String(Number(startParts.month));
        if (token === 'monthName') return new Intl.DateTimeFormat(this.locale.locale(), { month: 'long', timeZone: settings.timeZoneId }).format(start);
        if (token === 'quarter') return String(Math.floor((Number(startParts.month) - 1) / 3) + 1);
        if (/^start:[yMd-]+$/.test(token)) return this.formatDatePattern(startParts, token.slice(6));
        if (/^end:[yMd-]+$/.test(token)) return this.formatDatePattern(endParts, token.slice(4));
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

  setup(): void {
    const guildId = this.store.selectedGuild()?.id;
    const settings = this.settings();
    if (!guildId || !settings?.enabled || settings.scheduleKind === 'Manual' || !this.valid(settings) || this.planning() || this.planningCount() < 1) return;
    const requestSnapshot = `${this.serialize(settings)}|${this.planningCount()}`;
    if (!this.setupOperationId || this.setupRequestSnapshot !== requestSnapshot) {
      this.setupOperationId = crypto.randomUUID();
      this.setupRequestSnapshot = requestSnapshot;
    }
    this.planning.set(true);
    this.api.setupSeasonSystem(guildId, this.toRequest(settings), this.planningCount(), this.setupOperationId).pipe(finalize(() => this.planning.set(false))).subscribe({
      next: response => {
        if (this.store.selectedGuild()?.id !== guildId) return;
        const saved = this.fromApi(response.settings);
        this.persistedSettings.set(structuredClone(saved));
        this.draftSettings.set(structuredClone(saved));
        this.baseline = this.serialize(saved);
        this.seasons.set(this.sortSeasons([...this.seasons(), ...response.seasons]));
        this.setupOperationId = null;
        this.setupRequestSnapshot = null;
        this.toast.success(this.i18n.translate('seasons.setupSucceeded', { count: response.seasons.length }));
        this.refreshPreview();
      },
      error: error => {
        const resolved = this.apiErrors.resolve(error, 'errors.seasonSetup');
        this.serverErrors.set(this.mapServerErrors(resolved.validation));
        this.pageError.set(resolved.message);
        this.toast.error(resolved.message);
      },
    });
  }

  planningCount(): number { return this.seasons().some(season => season.status === 'Active' || season.status === 'Closing' || season.status === 'Scheduled') ? this.additionalSeasonCount : this.initialSeasonCount; }
  hasConfirmedSeasons(): boolean { return this.seasons().some(season => season.status === 'Active' || season.status === 'Closing' || season.status === 'Scheduled'); }
  setupActionKey(): string { return this.dirty() ? 'seasons.saveAndCreate' : (this.hasConfirmedSeasons() ? 'seasons.createMore' : 'seasons.create'); }

  requestAction(action: SeasonAction, season: Season): void {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId) return;
    if (this.dirty()) {
      this.pageError.set(this.i18n.translate('seasons.unsaved'));
      return;
    }
    this.pending.set({ action, season, guildId });
    this.confirmDialog?.open();
  }

  requestBulkAction(action: SeasonBulkAction): void {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId || this.dirty()) return;
    this.pending.set({ action, season: null, guildId });
    this.confirmDialog?.open();
  }

  confirmAction(): void {
    const pending = this.pending();
    if (!pending || this.actionBusy() || (pending.season && !pending.season.id)) return;
    const guildId = pending.guildId;
    const seasonId = pending.season?.id;
    if (this.store.selectedGuild()?.id !== guildId) {
      this.closeDialog();
      return;
    }
    const request: Observable<unknown> = pending.action === 'cancelScheduled' ? this.api.cancelScheduledSeasons(guildId)
      : pending.action === 'deleteCancelled' ? this.api.deleteCancelledSeasons(guildId)
      : pending.action === 'resetCounter' ? this.api.resetSeasonCounter(guildId)
      : pending.action === 'start' ? this.api.startSeason(guildId, seasonId!)
      : pending.action === 'close' ? this.api.closeSeason(guildId, seasonId!)
      : pending.action === 'resume' ? this.api.resumeSeason(guildId, seasonId!)
      : pending.action === 'delete' ? this.api.deleteSeason(guildId, seasonId!)
      : this.api.cancelSeason(guildId, seasonId!);
    this.actionBusy.set(true);
    request.pipe(finalize(() => this.actionBusy.set(false))).subscribe({
      next: () => {
        if (this.store.selectedGuild()?.id !== guildId) return;
        this.confirmDialog?.close();
        this.pending.set(null);
        this.toast.success(this.i18n.translate(`seasons.${pending.action}Succeeded`));
        if (this.dirty()) this.reloadSeasons(guildId);
        else this.reloadAfterAction(guildId);
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
    return pending ? this.i18n.translate(`seasons.confirm.${pending.action}`, { name: pending.season?.name ?? '', count: pending.action === 'cancelScheduled' ? this.scheduledCount() : this.cancelledCount() }) : '';
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
    if (season.status === 'Scheduled') result.push('start', 'cancel');
    if (season.status === 'Active') result.push('close', 'cancel');
    if (this.isResumable(season)) result.push('resume');
    if (season.status === 'Cancelled' && !this.seasons().some(item => item.previousSeasonId === season.id)) result.push('delete');
    return result;
  }

  scheduledCount(): number { return this.seasons().filter(season => season.status === 'Scheduled').length; }
  cancelledCount(): number { return this.seasons().filter(season => season.status === 'Cancelled').length; }

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

  scrollToStep(step: number): void {
    const target = document.getElementById(`season-step-${step}`);
    target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    target?.querySelector<HTMLElement>('input, select, button, [tabindex]')?.focus({ preventScroll: true });
  }

  private percentageValid(value: number): boolean { return Number.isFinite(Number(value)) && Number(value) >= 0 && Number(value) <= 100; }
  private seasonDateParts(value: Date, timeZone: string): { year: string; month: string; day: string } {
    const parts = new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit', timeZone }).formatToParts(value);
    const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value ?? '';
    return { year: part('year'), month: part('month'), day: part('day') };
  }
  private formatDatePattern(parts: { year: string; month: string; day: string }, pattern: string): string {
    return pattern.replace(/yyyy|yyy|yy|y|MM|M|dd|d/g, token => {
      if (token.startsWith('y')) return token.length === 2 ? parts.year.slice(-2) : token.length === 1 ? String(Number(parts.year)) : parts.year.padStart(token.length, '0');
      const value = token.startsWith('M') ? parts.month : parts.day;
      return token.length === 1 ? String(Number(value)) : value;
    });
  }
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
        this.persistedSettings.set(structuredClone(settings));
        this.draftSettings.set(structuredClone(settings));
        this.baseline = this.serialize(settings);
        this.seasons.set(this.sortSeasons(value.seasons));
        this.serverErrors.set({});
        this.refreshPreview();
      },
      error: error => this.pageError.set(this.apiErrors.resolve(error, 'errors.seasonsLoad').message),
    });
  }

  private sortSeasons(items: Season[]): Season[] {
    const unique = new Map(items.map(item => [item.id ?? String(item.sequence), item]));
    return [...unique.values()].sort((a, b) => Number(b.sequence) - Number(a.sequence));
  }

  private toRequest(settings: SeasonSettings): SeasonSettings {
    return {
      ...settings,
      scheduleAnchorUtc: settings.scheduleAnchorUtc ? this.localDateTimeToUtc(settings.scheduleAnchorUtc, settings.timeZoneId) : null,
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
    result.planningMode ??= 'Explicit';
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
    if (result.scheduleAnchorUtc) result.scheduleAnchorUtc = this.utcToLocalDateTime(result.scheduleAnchorUtc, result.timeZoneId);
    return result;
  }

  private localDateTimeToUtc(value: string, timeZone: string): string {
    const [date, time] = value.split('T');
    const [year, month, day] = date.split('-').map(Number);
    const [hour, minute] = time.split(':').map(Number);
    const wall = Date.UTC(year, month - 1, day, hour, minute);
    const candidates = this.timeZoneOffsets(timeZone, wall).map(offset => wall - offset);
    const matching = candidates.filter(instant => this.utcToLocalDateTime(new Date(instant).toISOString(), timeZone) === value);
    const instant = matching.length ? Math.min(...matching) : wall - this.timeZoneOffset(timeZone, wall);
    return new Date(instant).toISOString();
  }

  private utcToLocalDateTime(value: string, timeZone: string): string {
    const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(value));
    const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value ?? '';
    return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`;
  }

  private timeZoneOffsets(timeZone: string, wall: number): number[] {
    return [...new Set([-172800000, -86400000, 0, 86400000, 172800000].map(delta => this.timeZoneOffset(timeZone, wall + delta)))];
  }

  private timeZoneOffset(timeZone: string, instant: number): number {
    const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(instant));
    const part = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find(item => item.type === type)?.value ?? 0);
    return Date.UTC(part('year'), part('month') - 1, part('day'), part('hour'), part('minute'), part('second')) - instant;
  }

  private serialize(settings: SeasonSettings): string { return JSON.stringify(settings); }
}
