import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, EMPTY, finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';
import { BotIncident, BotManagementRange, IncidentQuery, IncidentResponse, IncidentSeverity, IncidentStatus } from './bot-management.models';
import { BotManagementService } from './bot-management.service';

@Component({ selector: 'app-incidents', standalone: true, imports: [CommonModule, TranslocoPipe], templateUrl: './incidents.component.html', styleUrl: './incidents.component.scss' })
export class IncidentsComponent {
  private readonly api = inject(BotManagementService); private readonly errors = inject(ApiErrorService); private readonly destroyRef = inject(DestroyRef); private readonly toast = inject(ToastService);
  private readonly i18n = inject(TranslocoService);
  readonly locale = inject(LocaleService); readonly data = signal<IncidentResponse | null>(null); readonly loading = signal(true); readonly loadingMore = signal(false); readonly actionPending = signal(false); readonly error = signal<string | null>(null); readonly selected = signal<BotIncident | null>(null); readonly detailLoading = signal(false);
  readonly range = signal<BotManagementRange>('7d'); readonly status = signal<IncidentStatus | ''>(''); readonly severity = signal<IncidentSeverity | ''>(''); readonly search = signal('');
  constructor() { this.load(); }
  load(cursor?: string): void {
    const more = Boolean(cursor); (more ? this.loadingMore : this.loading).set(true); this.error.set(null);
    const query: IncidentQuery = { range: this.range(), status: this.status() || undefined, severity: this.severity() || undefined, search: this.search().trim() || undefined, cursor };
    this.api.getIncidents(query).pipe(catchError(error => { this.error.set(this.errors.resolve(error, 'errors.incidentsLoad').message); return EMPTY; }), finalize(() => (more ? this.loadingMore : this.loading).set(false)), takeUntilDestroyed(this.destroyRef)).subscribe(page => this.data.set(more && this.data() ? { ...page, items: [...this.data()!.items, ...page.items] } : page));
  }
  reset(): void { this.range.set('7d'); this.status.set(''); this.severity.set(''); this.search.set(''); this.load(); }
  select(incident: BotIncident): void { this.selected.set(incident); this.detailLoading.set(true); this.api.getIncident(incident.id).pipe(catchError(error => { this.error.set(this.errors.resolve(error, 'errors.incidentLoad').message); return EMPTY; }), finalize(() => this.detailLoading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe(value => this.selected.set(value)); }
  close(): void { this.selected.set(null); }
  act(action: 'acknowledge' | 'resolve' | 'reopen'): void { const incident = this.selected(); if (!incident || this.actionPending()) return; this.actionPending.set(true); const request = action === 'acknowledge' ? this.api.acknowledgeIncident(incident.id) : action === 'resolve' ? this.api.resolveIncident(incident.id) : this.api.reopenIncident(incident.id); request.pipe(finalize(() => this.actionPending.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({ next: value => { this.selected.set(value); this.data.update(data => data ? { ...data, items: data.items.map(item => item.id === value.id ? value : item) } : data); }, error: error => this.error.set(this.errors.resolve(error, 'errors.incidentAction').message) }); }
  async copy(value: string): Promise<void> { await navigator.clipboard.writeText(value); this.toast.success(this.i18n.translate('botManagement.incidents.copied')); }
  date(value: string): string { return this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }); }
}
