import { CommonModule } from '@angular/common';
import { Component, DestroyRef, ElementRef, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { CustomBotAccess, CustomBotIdentity, GuildService } from '../../services/guild.service';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';
import { AuthService } from '../../services/auth.service';
import { Subscription, timer } from 'rxjs';

type CustomBotAction = 'storeToken' | 'install' | 'validate' | 'activate' | 'completeHandover' | 'returnToPlatform' | 'delete' | null;

@Component({ selector: 'app-custom-bot-identity', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe], templateUrl: './custom-bot-identity.component.html', styleUrl: './custom-bot-identity.component.scss' })
export class CustomBotIdentityComponent implements OnInit {
  @ViewChild('platformInstallDialog') private platformInstallDialog?: ElementRef<HTMLDialogElement>;
  private readonly api = inject(GuildService); private readonly store = inject(AppStore); private readonly auth = inject(AuthService); private readonly destroyRef = inject(DestroyRef); private readonly errors = inject(ApiErrorService); private readonly toast = inject(ToastService); private readonly i18n = inject(TranslocoService);
  private platformInstallPolling?: Subscription;
  readonly loading = signal(true); readonly activeAction = signal<CustomBotAction>(null); readonly access = signal<CustomBotAccess | null>(null); readonly identity = signal<CustomBotIdentity | null>(null); readonly token = signal(''); readonly diagnostics = signal<Record<string, boolean> | null>(null); readonly error = signal('');
  readonly platformInstallUrl = signal<string | null>(null); readonly platformInstallError = signal('');
  ngOnInit(): void { this.load(); }
  load(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.loading.set(true); this.api.customBotAccess(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: access => { this.access.set(access); this.api.customBotIdentity(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: identity => { this.identity.set(identity); this.loading.set(false); }, error: error => this.fail(error) }); }, error: error => this.fail(error) }); }
  storeToken(): void { const guildId = this.store.selectedGuild()?.id; const token = this.token().trim(); if (!guildId || !token) return; this.token.set(''); this.run('storeToken', this.api.storeCustomBotToken(guildId, token, this.identity()?.revision), result => { this.identity.set(result.identity); this.diagnostics.set(null); this.toast.success(this.i18n.translate('customBotIdentity.tokenStored')); }); }
  install(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run('install', this.api.customBotInstallUrl(guildId), result => { if (result.installUrl) window.open(result.installUrl, '_blank', 'noopener'); }); }
  validate(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run('validate', this.api.validateCustomBot(guildId), result => { this.identity.set(result.identity); this.diagnostics.set(result.diagnostics); }); }
  activate(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run('activate', this.api.activateCustomBot(guildId, this.identity()?.revision), result => { this.identity.set(result.identity); this.diagnostics.set(result.diagnostics); this.refreshGuilds(); this.toast.success(this.i18n.translate('customBotIdentity.activated')); }); }
  restart(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run('validate', this.api.restartCustomBot(guildId), result => { this.identity.set(result.identity); this.diagnostics.set(result.diagnostics); }); }
  completeHandover(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run('completeHandover', this.api.completeCustomBotHandover(guildId), result => { this.identity.set(result.identity); this.refreshGuilds(); }); }
  deactivate(): void { this.returnToPlatform(false); }
  openPlatformInstall(): void { const url = this.platformInstallUrl(); if (url) window.open(url, '_blank', 'noopener'); }
  closePlatformInstall(): void { this.platformInstallPolling?.unsubscribe(); this.platformInstallPolling = undefined; this.platformInstallUrl.set(null); this.platformInstallError.set(''); if (this.platformInstallDialog?.nativeElement.open) this.platformInstallDialog.nativeElement.close(); }
  retryPlatformReturn(): void { this.platformInstallError.set(''); this.tryCompletePlatformReturn(); }
  delete(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.activeAction.set('delete'); this.error.set(''); this.api.deleteCustomBot(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: () => { this.identity.set(null); this.diagnostics.set(null); this.activeAction.set(null); this.toast.success(this.i18n.translate('customBotIdentity.deleted')); }, error: error => this.fail(error) }); }
  isWorking(action: CustomBotAction): boolean { return this.activeAction() === action; }
  private run(action: Exclude<CustomBotAction, null>, request: ReturnType<GuildService['validateCustomBot']>, done: (result: any) => void): void { this.activeAction.set(action); this.error.set(''); request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: result => { done(result); this.activeAction.set(null); }, error: error => this.fail(error) }); }
  private refreshGuilds(): void { this.auth.getUserGuilds(true).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: guilds => this.store.setGuilds(guilds) }); }
  private returnToPlatform(fromInstallDialog: boolean): void
  {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId || this.activeAction()) return;
    this.activeAction.set('returnToPlatform'); this.error.set('');
    this.api.deactivateCustomBot(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.identity.set(result.identity); this.diagnostics.set(null); this.activeAction.set(null);
        if (fromInstallDialog) this.closePlatformInstall();
        this.refreshGuilds(); this.toast.success(this.i18n.translate('customBotIdentity.deactivated'));
      },
      error: error => {
        this.activeAction.set(null);
        if (error?.error?.errorKey === 'customBotIdentity.platformBotNotInstalled' && typeof error?.error?.parameters?.installUrl === 'string') {
          this.openPlatformInstallDialog(error.error.parameters.installUrl);
          return;
        }
        this.fail(error);
      }
    });
  }
  private openPlatformInstallDialog(installUrl: string): void
  {
    this.platformInstallUrl.set(installUrl); this.platformInstallError.set('');
    setTimeout(() => this.platformInstallDialog?.nativeElement.showModal());
    this.startPlatformInstallPolling();
  }
  private startPlatformInstallPolling(): void
  {
    this.platformInstallPolling?.unsubscribe();
    this.platformInstallPolling = timer(2_000, 3_000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.tryCompletePlatformReturn());
  }
  private tryCompletePlatformReturn(): void
  {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId || this.activeAction()) return;
    this.activeAction.set('returnToPlatform');
    this.api.deactivateCustomBot(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.identity.set(result.identity); this.diagnostics.set(null); this.activeAction.set(null); this.closePlatformInstall();
        this.refreshGuilds(); this.toast.success(this.i18n.translate('customBotIdentity.deactivated'));
      },
      error: error => {
        this.activeAction.set(null);
        if (error?.error?.errorKey === 'customBotIdentity.platformBotNotInstalled') { if (!this.platformInstallPolling) this.startPlatformInstallPolling(); return; }
        this.platformInstallPolling?.unsubscribe(); this.platformInstallPolling = undefined;
        this.platformInstallError.set(this.errors.resolve(error, 'customBotIdentity.error').message);
      }
    });
  }
  avatarUrl(bot: CustomBotIdentity): string | null { return bot.botUserId && bot.botAvatarHash ? `https://cdn.discordapp.com/avatars/${bot.botUserId}/${bot.botAvatarHash}.png?size=128` : null; }
  private fail(error: any): void { const checks = error?.error?.parameters?.diagnostics; if (checks && typeof checks === 'object') this.diagnostics.set(checks); this.error.set(this.errors.resolve(error, 'customBotIdentity.error').message); this.loading.set(false); this.activeAction.set(null); }
}
