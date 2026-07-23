import { Component, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TranslocoPipe } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService } from '../../services/guild.service';
import { ChannelDiagnostic, DiagnosticStatus, FeatureDiagnostic, PermissionCheck, PermissionDiagnosticReport } from '../../models/permission-diagnostics.models';

@Component({
  selector: 'app-permission-diagnostics', standalone: true, imports: [CommonModule, TranslocoPipe],
  templateUrl: './permission-diagnostics.component.html', styleUrl: './permission-diagnostics.component.scss'
})
export class PermissionDiagnosticsComponent {
  private readonly app = inject(AppStore); private readonly guilds = inject(GuildService); private readonly errors = inject(ApiErrorService);
  readonly report = signal<PermissionDiagnosticReport | null>(null); readonly loading = signal(false); readonly error = signal('');
  readonly criticalOnly = signal(false); readonly query = signal(''); readonly selected = signal<PermissionCheck | null>(null);
  private guildId: string | null = null;
  constructor() { effect(() => { const id = this.app.selectedGuild()?.id ?? null; if (id === this.guildId) return; this.guildId = id; this.report.set(null); if (id) this.loadLatest(id); }); }
  loadLatest(id = this.guildId): void { if (!id) return; this.loading.set(true); this.guilds.latestPermissionDiagnostics(id).pipe(finalize(() => this.loading.set(false))).subscribe({ next: report => this.report.set(report), error: () => this.run() }); }
  run(): void { if (!this.guildId || this.loading()) return; this.loading.set(true); this.error.set(''); this.guilds.scanPermissionDiagnostics(this.guildId).pipe(finalize(() => this.loading.set(false))).subscribe({ next: report => this.report.set(report), error: error => this.error.set(this.errors.resolve(error, 'errors.generic').message) }); }
  status(value: DiagnosticStatus): string { return `status status--${value.toLowerCase()}`; }
  issues(): { check: PermissionCheck; count: number }[] {
    const groups = new Map<string, { check: PermissionCheck; count: number }>();
    for (const check of [...(this.report()?.globalChecks ?? []), ...(this.report()?.featureChecks.flatMap(x => x.checks) ?? [])].filter(x => x.status === 'Critical' || x.status === 'Warning' || x.status === 'Unknown')) {
      const key = [check.status, check.titleKey, check.remediationKey, ...check.missingPermissions.slice().sort()].join('|');
      const current = groups.get(key);
      if (current) current.count++;
      else groups.set(key, { check, count: 1 });
    }
    return [...groups.values()];
  }
  channels(): ChannelDiagnostic[] { const query = this.query().toLowerCase(); return (this.report()?.channelChecks ?? []).filter(channel => (!this.criticalOnly() || channel.checks.some(check => check.status === 'Critical')) && (!query || channel.channelName.toLowerCase().includes(query))); }
  permissions(feature: FeatureDiagnostic): { name: string; missing: number; total: number }[] {
    const permissions = new Map<string, { missing: number; total: number }>();
    for (const check of feature.checks) for (const name of check.requiredPermissions) {
      const current = permissions.get(name) ?? { missing: 0, total: 0 };
      current.total++;
      if (check.missingPermissions.includes(name)) current.missing++;
      permissions.set(name, current);
    }
    return [...permissions].map(([name, value]) => ({ name, ...value }));
  }
  channelSummary(channel: ChannelDiagnostic): { status: DiagnosticStatus; key: string; parameters?: Record<string, string> } {
    const missing = [...new Set(channel.checks.flatMap(check => check.missingPermissions))];
    if (missing.length) return { status: 'Critical', key: 'diagnostics.channel.missingPermissions', parameters: { permissions: missing.join(', ') } };
    if (channel.checks.every(check => check.status === 'NotApplicable')) return { status: 'NotApplicable', key: 'diagnostics.channel.notRequired' };
    return { status: 'Healthy', key: 'diagnostics.channel.permissionsAvailable' };
  }
  featureName(key: string): string { return `diagnostics.feature.${key.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())}`; }
}
