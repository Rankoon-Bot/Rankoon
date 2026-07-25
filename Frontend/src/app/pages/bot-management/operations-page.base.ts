import { DestroyRef, Directive, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, EMPTY, finalize, Observable } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { BotManagementRange } from './bot-management.models';

@Directive()
export abstract class OperationsPageBase<T> {
  protected readonly route = inject(ActivatedRoute); private readonly router = inject(Router); private readonly destroyRef = inject(DestroyRef); private readonly errors = inject(ApiErrorService);
  readonly range = signal<BotManagementRange>('7d'); readonly data = signal<T | null>(null); readonly loading = signal(true); readonly error = signal<string | null>(null);
  protected initialize(): void { this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => { const value = params.get('range'); this.range.set(value === '24h' || value === '30d' || value === '90d' ? value : '7d'); this.load(); }); }
  changeRange(value: BotManagementRange): void { if (value !== this.range()) void this.router.navigate([], { relativeTo: this.route, queryParams: { range: value } }); }
  load(): void { this.loading.set(true); this.error.set(null); this.request(this.range()).pipe(catchError(error => { this.error.set(this.errors.resolve(error, 'errors.botManagementLoad').message); return EMPTY; }), finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe(data => this.data.set(data)); }
  protected abstract request(range: BotManagementRange): Observable<T>;
}
