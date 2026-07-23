import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { CustomBotAccess, CustomBotIdentity, GuildService } from '../../services/guild.service';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';

@Component({ selector: 'app-custom-bot-identity', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe], templateUrl: './custom-bot-identity.component.html', styleUrl: './custom-bot-identity.component.scss' })
export class CustomBotIdentityComponent implements OnInit {
  private readonly api = inject(GuildService); private readonly store = inject(AppStore); private readonly destroyRef = inject(DestroyRef); private readonly errors = inject(ApiErrorService); private readonly toast = inject(ToastService); private readonly i18n = inject(TranslocoService);
  readonly loading = signal(true); readonly working = signal(false); readonly access = signal<CustomBotAccess | null>(null); readonly identity = signal<CustomBotIdentity | null>(null); readonly token = signal(''); readonly diagnostics = signal<Record<string, boolean> | null>(null); readonly error = signal('');
  ngOnInit(): void { this.load(); }
  load(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.loading.set(true); this.api.customBotAccess(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: access => { this.access.set(access); this.api.customBotIdentity(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: identity => { this.identity.set(identity); this.loading.set(false); }, error: error => this.fail(error) }); }, error: error => this.fail(error) }); }
  storeToken(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId || !this.token().trim()) return; this.run(this.api.storeCustomBotToken(guildId, this.token(), this.identity()?.revision), result => { this.identity.set(result.identity); this.token.set(''); this.toast.success(this.i18n.translate('customBotIdentity.tokenStored')); }); }
  install(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run(this.api.customBotInstallUrl(guildId), result => { if (result.installUrl) window.open(result.installUrl, '_blank', 'noopener'); }); }
  validate(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run(this.api.validateCustomBot(guildId), result => { this.identity.set(result.identity); this.diagnostics.set(result.diagnostics); }); }
  activate(): void { const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run(this.api.activateCustomBot(guildId, this.identity()?.revision), result => { this.identity.set(result.identity); this.diagnostics.set(result.diagnostics); this.toast.success(this.i18n.translate('customBotIdentity.activated')); }); }
  deactivate(): void { if (!confirm(this.i18n.translate('customBotIdentity.confirmDeactivate'))) return; const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.run(this.api.deactivateCustomBot(guildId), result => { this.identity.set(result.identity); this.toast.success(this.i18n.translate('customBotIdentity.deactivated')); }); }
  delete(): void { if (!confirm(this.i18n.translate('customBotIdentity.confirmDelete'))) return; const guildId = this.store.selectedGuild()?.id; if (!guildId) return; this.working.set(true); this.api.deleteCustomBot(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: () => { this.identity.set(null); this.working.set(false); }, error: error => this.fail(error) }); }
  private run(request: ReturnType<GuildService['validateCustomBot']>, done: (result: any) => void): void { this.working.set(true); this.error.set(''); request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: result => { done(result); this.working.set(false); }, error: error => this.fail(error) }); }
  private fail(error: any): void { this.error.set(this.errors.resolve(error, 'customBotIdentity.error').message); this.loading.set(false); this.working.set(false); }
}
