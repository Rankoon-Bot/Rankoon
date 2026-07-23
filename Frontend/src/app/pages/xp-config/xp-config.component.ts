import { CommonModule } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { catchError, finalize, forkJoin, of } from 'rxjs';
import {
  GuildResources,
  GuildService,
  RankEntry,
  VoiceWatchdogStatus,
  XpConfig,
  ServerBoosterXpTier,
} from '../../services/guild.service';
import { AppStore } from '../../store/app.store';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-xp-config',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  templateUrl: './xp-config.component.html',
  styleUrls: ['./xp-config.component.scss'],
})
export class XpConfigComponent implements OnInit {
  private readonly appStore = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);

  readonly config = signal<XpConfig | null>(null);
  readonly resources = signal<GuildResources>({ roles: [], channels: [] });
  readonly leaderboard = signal<RankEntry[]>([]);
  readonly watchdog = signal<VoiceWatchdogStatus | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal('');
  readonly saving = signal(false);
  readonly watchdogBusy = signal(false);
  private readonly baseline = signal('');

  selectedRole = '';
  selectedChannel = '';
  selectedCategory = '';

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    const id = this.appStore.selectedGuild()?.id;
    if (!id) {
      this.loading.set(false);
      this.loadError.set(this.i18n.translate('errors.noServer'));
      return;
    }

    this.loading.set(true);
    this.loadError.set('');
    forkJoin({
      config: this.api.config(id),
      resources: this.api.resources(id).pipe(catchError(() => of({ roles: [], channels: [] }))),
      watchdog: this.api.voiceWatchdog(id).pipe(catchError(() => of(null))),
      leaderboard: this.api.leaderboard(id).pipe(catchError(() => of([]))),
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: result => {
          this.normalizeConfig(result.config);
          this.config.set(result.config);
          this.baseline.set(this.serialize(result.config));
          this.resources.set(result.resources);
          this.watchdog.set(result.watchdog);
          this.leaderboard.set(result.leaderboard);
        },
        error: error => this.loadError.set(this.apiErrors.resolve(error, 'errors.xpLoad').message),
      });
  }

  save(): void {
    const id = this.appStore.selectedGuild()?.id;
    const config = this.config();
    if (!id || !config || !this.dirty() || !this.isValid(config)) return;

    this.sortBoosterTiers();

    this.saving.set(true);
    this.api.saveConfig(id, config)
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: saved => {
          this.normalizeConfig(saved);
          this.config.set(saved);
          this.baseline.set(this.serialize(saved));
          this.toast.success(this.i18n.translate('xp.saved'));
          this.refreshWatchdog();
        },
        error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.save').message),
      });
  }

  setWatchdog(enabled: boolean): void {
    const id = this.appStore.selectedGuild()?.id;
    const config = this.config();
    if (!id || !config) return;

    const previousEnabled = config.enabled;
    const previousVoiceEnabled = config.voice.enabled;
    config.voice.enabled = enabled;
    if (enabled) config.enabled = true;
    this.watchdogBusy.set(true);

    this.api.setVoiceWatchdog(id, enabled)
      .pipe(finalize(() => this.watchdogBusy.set(false)))
      .subscribe({
        next: result => {
          this.watchdog.set(result.status);
          const running = this.watchdogIsRunning();
           const message = this.i18n.translate(enabled && !running ? 'xp.watchdogDegraded' : enabled ? 'xp.watchdogStarted' : 'xp.watchdogStopped');
           enabled && !running ? this.toast.error(message) : this.toast.success(message);
        },
         error: error => {
          config.enabled = previousEnabled;
          config.voice.enabled = previousVoiceEnabled;
            this.toast.error(this.apiErrors.resolve(error, 'errors.watchdogToggle').message);
          this.refreshWatchdog();
        },
      });
  }

  refreshWatchdog(): void {
    const id = this.appStore.selectedGuild()?.id;
    if (!id) return;
    this.api.voiceWatchdog(id).subscribe({ next: status => this.watchdog.set(status) });
  }

  addLevelRole(): void {
    this.config()?.levelRoles.push({ level: 1, roleId: '' });
  }

  addMultiplier(): void {
    this.config()?.channelMultipliers.push({ channelId: '', multiplier: 1 });
  }

  addBoosterTier(): void {
    const tiers = this.config()?.serverBooster.tiers;
    if (!tiers || tiers.length >= 10) return;
    const minimumBoostMonths = tiers.length ? Math.max(...tiers.map(tier => Number(tier.minimumBoostMonths))) + 2 : 0;
    const multiplier = tiers.length ? Number([...tiers].sort((a, b) => Number(a.minimumBoostMonths) - Number(b.minimumBoostMonths)).at(-1)!.multiplier) : 1.25;
    tiers.push({ minimumBoostMonths, multiplier });
    this.sortBoosterTiers();
  }

  removeBoosterTier(tier: ServerBoosterXpTier): void {
    const tiers = this.config()?.serverBooster.tiers;
    if (!tiers) return;
    const index = tiers.indexOf(tier);
    if (index >= 0) tiers.splice(index, 1);
  }

  sortBoosterTiers(): void {
    this.config()?.serverBooster.tiers.sort((a, b) => Number(a.minimumBoostMonths) - Number(b.minimumBoostMonths));
  }

  boosterTierErrors(config: XpConfig, tier: ServerBoosterXpTier): string[] {
    const errors: string[] = [];
    const months = Number(tier.minimumBoostMonths);
    const multiplier = Number(tier.multiplier);
    if (!Number.isInteger(months) || months < 0) errors.push('xp.boosterMonthsValidation');
    if (config.serverBooster.tiers.filter(item => Number(item.minimumBoostMonths) === months).length > 1) errors.push('xp.boosterDuplicateValidation');
    if (!Number.isFinite(multiplier) || multiplier < 1 || multiplier > 10 || !/^\d+(\.\d{1,2})?$/.test(String(tier.multiplier))) errors.push('xp.boosterMultiplierValidation');
    const sorted = [...config.serverBooster.tiers].sort((a, b) => Number(a.minimumBoostMonths) - Number(b.minimumBoostMonths));
    const index = sorted.indexOf(tier);
    if (index > 0 && multiplier < Number(sorted[index - 1].multiplier)) errors.push('xp.boosterOrderValidation');
    return errors;
  }

  dirty(): boolean {
    const config = this.config();
    return !!config && this.serialize(config) !== this.baseline();
  }

  reset(): void {
    if (!this.baseline()) return;
    this.config.set(JSON.parse(this.baseline()) as XpConfig);
  }

  activeSources(config: XpConfig): number {
    return [config.message.enabled, config.voice.enabled, config.reaction.enabled, config.eventInterest.enabled, config.thread.enabled].filter(Boolean).length;
  }

  categories = () => this.resources().channels.filter(channel => channel.type.includes('Category'));
  textChannels = () => this.resources().channels.filter(channel => channel.type.includes('Text'));

  excludeRole(): void {
    const config = this.config();
    if (config && this.selectedRole && !config.excludedRoleIds.includes(this.selectedRole)) config.excludedRoleIds.push(this.selectedRole);
    this.selectedRole = '';
  }

  excludeChannel(): void {
    const config = this.config();
    if (config && this.selectedChannel && !config.excludedChannelIds.includes(this.selectedChannel)) config.excludedChannelIds.push(this.selectedChannel);
    this.selectedChannel = '';
  }

  excludeCategory(): void {
    const config = this.config();
    if (config && this.selectedCategory && !config.excludedCategoryIds.includes(this.selectedCategory)) config.excludedCategoryIds.push(this.selectedCategory);
    this.selectedCategory = '';
  }

  remove(list: string[], value: string): void {
    const index = list.indexOf(value);
    if (index >= 0) list.splice(index, 1);
  }

  roleName(id: string): string {
    return this.resources().roles.find(role => role.id === id)?.name ?? id;
  }

  channelName(id: string): string {
    return this.resources().channels.find(channel => channel.id === id)?.name ?? id;
  }

  watchdogState(status: VoiceWatchdogStatus | null): string {
    if (!status) return this.i18n.translate('common.unknown');
    const states = ['starting', 'healthy', 'degraded', 'stale', 'restarting', 'faulted', 'stopped'];
    if (typeof status.state === 'number') return this.i18n.translate(`xp.watchdogStates.${states[status.state] ?? 'unknown'}`);
    const numericState = Number(status.state);
    if (!Number.isNaN(numericState)) return this.i18n.translate(`xp.watchdogStates.${states[numericState] ?? 'unknown'}`);
    return states.includes(status.state.toLowerCase()) ? this.i18n.translate(`xp.watchdogStates.${status.state.toLowerCase()}`) : status.state;
  }

  watchdogIsRunning(): boolean {
    const state = this.watchdog()?.state;
    return state === 1 || state === '1' || state === 'Healthy' || state === 'Starting' || state === 0 || state === '0';
  }

  isValid(config: XpConfig): boolean {
    return config.message.minimumPoints >= 0
      && config.message.maximumPoints >= config.message.minimumPoints
      && config.message.minimumCharacters >= 0
      && config.message.maximumCharacters >= config.message.minimumCharacters
      && config.message.cooldownSeconds >= 0
      && config.voice.pointsPerMinute >= 0
      && config.voice.minimumSessionSeconds >= 0
      && config.reaction.points >= 0
      && config.reaction.cooldownSeconds >= 0
      && config.eventInterest.points >= 0
      && config.thread.createPoints >= 0
      && config.thread.messagePoints >= 0
      && config.thread.cooldownSeconds >= 0
      && config.channelMultipliers.every(rule => !!rule.channelId && rule.multiplier >= 0)
      && new Set(config.channelMultipliers.map(rule => rule.channelId)).size === config.channelMultipliers.length
      && config.serverBooster.tiers.length <= 10
      && config.serverBooster.tiers.every(tier => this.boosterTierErrors(config, tier).length === 0)
      && config.levelRoles.every(role => !!role.roleId && role.level >= 1)
      && new Set(config.levelRoles.map(role => role.roleId)).size === config.levelRoles.length;
  }

  importMee6(event: Event): void {
    const id = this.appStore.selectedGuild()?.id;
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!id || !file) return;

    file.text()
      .then(text => JSON.parse(text) as unknown)
      .then(payload => this.api.importMee6(id, payload).subscribe({
        next: result => {
           this.toast.success(this.locale.plural(result.imported, 'xp.importedOne', 'xp.importedOther'));
          this.api.leaderboard(id).subscribe(entries => this.leaderboard.set(entries));
          input.value = '';
        },
          error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.importFailed').message),
      }))
       .catch(() => this.toast.error(this.i18n.translate('errors.invalidJson')));
  }

  formatDate(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'medium' }); }
  formatNumber(value: string | number): string { return this.locale.number(value); }
  lastCheck(value: string, connectedUsers: string | number): string {
    return this.locale.plural(connectedUsers, 'xp.lastCheckOne', 'xp.lastCheckOther', { date: this.formatDate(value) });
  }

  private normalizeConfig(config: XpConfig): void {
    config.serverBooster ??= { enabled: false, tiers: [] };
    config.serverBooster.tiers ??= [];
    config.serverBooster.tiers.sort((a, b) => Number(a.minimumBoostMonths) - Number(b.minimumBoostMonths));
  }

  private serialize(config: XpConfig): string {
    const clone = structuredClone(config);
    clone.serverBooster.tiers.sort((a, b) => Number(a.minimumBoostMonths) - Number(b.minimumBoostMonths));
    return JSON.stringify(clone);
  }
}
