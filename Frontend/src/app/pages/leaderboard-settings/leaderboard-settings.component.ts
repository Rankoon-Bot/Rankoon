import { CommonModule } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { GuildService, LeaderboardSettings, LeaderboardVisibility } from '../../services/guild.service';
import { AppStore } from '../../store/app.store';

@Component({ selector: 'app-leaderboard-settings', standalone: true, imports: [CommonModule, FormsModule, RouterLink], templateUrl: './leaderboard-settings.component.html', styleUrls: ['./leaderboard-settings.component.scss'] })
export class LeaderboardSettingsComponent implements OnInit {
  private readonly app = inject(AppStore);
  private readonly api = inject(GuildService);
  readonly settings = signal<LeaderboardSettings | null>(null);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  alias = '';
  visibility: LeaderboardVisibility = 'MembersOnly';

  ngOnInit(): void { this.load(); }
  load(): void {
    const guildId = this.app.selectedGuild()?.id;
    if (!guildId) { this.loading.set(false); this.error.set('Kein Server ausgewaehlt.'); return; }
    this.loading.set(true); this.error.set('');
    this.api.leaderboardSettings(guildId).pipe(finalize(() => this.loading.set(false))).subscribe({ next: settings => this.apply(settings), error: () => this.error.set('Die Ranglisten-Einstellungen konnten nicht geladen werden.') });
  }
  save(): void {
    const guildId = this.app.selectedGuild()?.id;
    if (!guildId || !this.alias.trim() || this.saving()) return;
    this.saving.set(true); this.error.set(''); this.message.set('');
    this.api.saveLeaderboardSettings(guildId, { alias: this.alias, visibility: this.visibility }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: settings => { this.apply(settings); this.message.set('Ranglisten-Einstellungen gespeichert.'); },
      error: response => this.error.set(response.status === 409 ? 'Dieser Alias ist bereits vergeben.' : response.error?.error ?? 'Speichern fehlgeschlagen.'),
    });
  }
  private apply(settings: LeaderboardSettings): void { this.settings.set(settings); this.alias = settings.alias; this.visibility = settings.visibility; }
}
