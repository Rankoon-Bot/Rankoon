import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { GuildAccessService } from '../../services/guild-access.service';
import { ApiErrorService } from '../../services/api-error.service';
import { BotMaintainerService, MaintainerGuild } from '../../services/bot-maintainer.service';
import { AppStore, Guild } from '../../store/app.store';

@Component({
  selector: 'app-maintainer-access',
  standalone: true,
  imports: [CommonModule, TranslocoPipe],
  template: `
    <section class="maintainer-page" [attr.aria-busy]="loading()">
      <header class="page-heading">
        <div>
          <p class="eyebrow">{{ 'botManagement.maintainer.eyebrow' | transloco }}</p>
          <h2>{{ 'botManagement.maintainer.title' | transloco }}</h2>
          <p class="description">{{ 'botManagement.maintainer.description' | transloco }}</p>
        </div>
        <button class="secondary-button" type="button" (click)="load()" [disabled]="loading()">
          {{ 'botManagement.maintainer.refresh' | transloco }}
        </button>
      </header>

      <aside class="access-note" role="note">
        <span class="note-icon" aria-hidden="true">i</span>
        <p>{{ 'botManagement.maintainer.notice' | transloco }}</p>
      </aside>

      <p *ngIf="error()" class="error-message" role="alert">{{ error() }}</p>
      <div *ngIf="loading() && guilds().length === 0" class="loading-state" role="status">
        <span class="spinner" aria-hidden="true"></span>
        <span>{{ 'botManagement.maintainer.loading' | transloco }}</span>
      </div>

      <div *ngIf="!loading() && guilds().length === 0" class="empty-state">
        <div class="mascot-slot" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v13a2.5 2.5 0 0 1-2.5 2.5h-11A2.5 2.5 0 0 1 4 18.5z" />
            <path d="M8 8h8M8 12h8M8 16h5" />
          </svg>
        </div>
        <h3>{{ 'botManagement.maintainer.emptyTitle' | transloco }}</h3>
        <p>{{ 'botManagement.maintainer.emptyDescription' | transloco }}</p>
      </div>

      <div *ngIf="guilds().length > 0" class="guild-list">
        <article *ngFor="let guild of guilds()" class="guild-card">
          <div class="guild-identity">
            <img *ngIf="guild.iconUrl" [src]="guild.iconUrl" [alt]="''" class="guild-icon">
            <div *ngIf="!guild.iconUrl" class="guild-placeholder" aria-hidden="true">{{ initials(guild.name) }}</div>
            <div class="guild-copy">
              <h3>{{ guild.name }}</h3>
              <p>{{ 'botManagement.maintainer.guildId' | transloco }}: {{ guild.guildId }}</p>
              <span class="runtime-state" [class.available]="guild.runtimeAvailable">
                {{ (guild.runtimeAvailable ? 'botManagement.maintainer.runtimeAvailable' : 'botManagement.maintainer.runtimeUnavailable') | transloco }}
                <ng-container *ngIf="guild.activeBotIdentity"> · {{ guild.activeBotIdentity }}</ng-container>
              </span>
            </div>
          </div>

          <div class="guild-controls">
            <div class="access-control">
              <span class="access-label">{{ 'botManagement.maintainer.access' | transloco }}</span>
              <button
                type="button"
                class="access-switch"
                role="switch"
                [class.enabled]="guild.enabled"
                [attr.aria-checked]="guild.enabled"
                [attr.aria-label]="(guild.enabled ? 'botManagement.maintainer.disableAccess' : 'botManagement.maintainer.enableAccess') | transloco: { name: guild.name }"
                [disabled]="busyGuild() !== null"
                (click)="toggle(guild)">
                <span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span>
                <span>{{ (guild.enabled ? 'botManagement.maintainer.enabled' : 'botManagement.maintainer.disabled') | transloco }}</span>
              </button>
            </div>
            <button class="open-button" type="button" (click)="openGuild(guild)" [disabled]="!guild.runtimeAvailable">
              {{ (guild.enabled ? 'botManagement.maintainer.openMaintainer' : 'botManagement.maintainer.openRegular') | transloco }}
            </button>
          </div>
        </article>
      </div>
    </section>
  `,
  styleUrl: './maintainer-access.component.scss',
})
export class MaintainerAccessComponent implements OnInit {
  private readonly api = inject(BotMaintainerService);
  private readonly guildAccess = inject(GuildAccessService);
  private readonly appStore = inject(AppStore);
  private readonly apiErrors = inject(ApiErrorService);

  readonly guilds = signal<MaintainerGuild[]>([]);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly busyGuild = signal<string | null>(null);

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading.set(true);
    this.error.set('');
    this.api.guilds().pipe(finalize(() => this.loading.set(false))).subscribe({
      next: guilds => this.guilds.set(guilds),
      error: error => this.error.set(this.apiErrors.resolve(error, 'botManagement.maintainer.loadFailed').message),
    });
  }

  toggle(guild: MaintainerGuild): void {
    if (this.busyGuild()) return;
    this.busyGuild.set(guild.guildId);
    this.error.set('');
    this.api.setGuildAccess(guild.guildId, !guild.enabled, guild.revision).pipe(
      finalize(() => this.busyGuild.set(null)),
    ).subscribe({
      next: update => {
        this.guilds.update(rows => rows.map(row => row.guildId === update.guildId
          ? { ...row, enabled: update.enabled, revision: update.revision }
          : row));
        this.guildAccess.clearCache(guild.guildId);
        if (this.appStore.selectedGuild()?.id === guild.guildId) {
          this.guildAccess.loadCapabilities(guild.guildId, true).subscribe({
            error: error => {
              if (error?.status === 403) {
                this.appStore.setGuildCapabilities(null);
              }
            },
          });
        }
      },
      error: error => {
        this.error.set(this.apiErrors.resolve(error, 'botManagement.maintainer.saveFailed').message);
        if (error?.status === 409) this.load();
      },
    });
  }

  openGuild(guild: MaintainerGuild): void {
    if (!guild.runtimeAvailable) return;
    const selected: Guild = {
      id: guild.guildId,
      name: guild.name,
      icon: null,
      owner: false,
      permissions: '0',
      features: [],
      botInstalled: true,
      rankoonManaged: true,
      authoritativeRuntimeAvailable: true,
      activeBotIdentity: guild.activeBotIdentity,
      platformBotInstalled: guild.activeBotIdentity === 'Rankoon',
      customBotInstalled: guild.activeBotIdentity === 'Custom',
      inviteUrl: '',
    };
    this.guildAccess.selectAndNavigate(selected).subscribe({
      error: error => {
        this.appStore.setSelectedGuild(null);
        this.error.set(this.apiErrors.resolve(error, 'botManagement.maintainer.openFailed').message);
      },
    });
  }

  initials(name: string): string {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase();
  }
}
