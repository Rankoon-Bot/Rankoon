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
} from '../../services/guild.service';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-xp-config',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './xp-config.component.html',
  styleUrls: ['./xp-config.component.scss'],
})
export class XpConfigComponent implements OnInit {
  private readonly appStore = inject(AppStore);
  private readonly api = inject(GuildService);

  readonly config = signal<XpConfig | null>(null);
  readonly resources = signal<GuildResources>({ roles: [], channels: [] });
  readonly leaderboard = signal<RankEntry[]>([]);
  readonly watchdog = signal<VoiceWatchdogStatus | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal('');
  readonly saving = signal(false);
  readonly watchdogBusy = signal(false);
  readonly message = signal('');
  readonly messageType = signal<'success' | 'error'>('success');

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
      this.loadError.set('Es ist kein Server ausgewaehlt.');
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
          this.config.set(result.config);
          this.resources.set(result.resources);
          this.watchdog.set(result.watchdog);
          this.leaderboard.set(result.leaderboard);
        },
        error: () => this.loadError.set('Die XP-Einstellungen konnten nicht geladen werden.'),
      });
  }

  save(): void {
    const id = this.appStore.selectedGuild()?.id;
    const config = this.config();
    if (!id || !config || !this.isValid(config)) return;

    this.saving.set(true);
    this.message.set('');
    this.api.saveConfig(id, config)
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: saved => {
          this.config.set(saved);
          this.showMessage('XP-Konfiguration gespeichert.', 'success');
          this.refreshWatchdog();
        },
        error: () => this.showMessage('Speichern fehlgeschlagen. Bitte pruefe deine Eingaben.', 'error'),
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
    this.message.set('');

    this.api.setVoiceWatchdog(id, enabled)
      .pipe(finalize(() => this.watchdogBusy.set(false)))
      .subscribe({
        next: result => {
          this.watchdog.set(result.status);
          const running = this.watchdogIsRunning();
          this.showMessage(
            enabled && !running ? 'VCWatchdog aktiviert, der aktuelle Lauf ist jedoch beeintraechtigt.' : enabled ? 'VCWatchdog gestartet.' : 'VCWatchdog deaktiviert.',
            enabled && !running ? 'error' : 'success',
          );
        },
        error: () => {
          config.enabled = previousEnabled;
          config.voice.enabled = previousVoiceEnabled;
          this.showMessage('Der VCWatchdog konnte nicht umgeschaltet werden.', 'error');
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
    if (!status) return 'Unbekannt';
    const states = ['Startet', 'Aktiv', 'Beeintraechtigt', 'Veraltet', 'Startet neu', 'Fehler', 'Deaktiviert'];
    if (typeof status.state === 'number') return states[status.state] ?? 'Unbekannt';
    const numericState = Number(status.state);
    if (!Number.isNaN(numericState)) return states[numericState] ?? 'Unbekannt';
    const labels: Record<string, string> = { Starting: 'Startet', Healthy: 'Aktiv', Degraded: 'Beeintraechtigt', Stale: 'Veraltet', Restarting: 'Startet neu', Faulted: 'Fehler', Stopped: 'Deaktiviert' };
    return labels[status.state] ?? status.state;
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
      && config.voice.checkIntervalSeconds >= 15
      && config.voice.checkIntervalSeconds <= 300
      && config.voice.holdbackThreshold >= 0
      && config.reaction.points >= 0
      && config.reaction.cooldownSeconds >= 0
      && config.eventInterest.points >= 0
      && config.thread.createPoints >= 0
      && config.thread.messagePoints >= 0
      && config.thread.cooldownSeconds >= 0
      && config.channelMultipliers.every(rule => !!rule.channelId && rule.multiplier >= 0)
      && new Set(config.channelMultipliers.map(rule => rule.channelId)).size === config.channelMultipliers.length
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
          this.showMessage(`${result.imported} MEE6-Mitglieder importiert.`, 'success');
          this.api.leaderboard(id).subscribe(entries => this.leaderboard.set(entries));
          input.value = '';
        },
        error: () => this.showMessage('Der MEE6-Import ist fehlgeschlagen.', 'error'),
      }))
      .catch(() => this.showMessage('Die ausgewaehlte Datei enthaelt kein gueltiges JSON.', 'error'));
  }

  private showMessage(message: string, type: 'success' | 'error'): void {
    this.messageType.set(type);
    this.message.set(message);
  }
}
