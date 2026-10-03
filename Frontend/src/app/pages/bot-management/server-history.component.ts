import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../i18n/locale.service';
import { BotGuildHistoryResponse, BotManagementRange } from './bot-management.models';
import { BotManagementService } from './bot-management.service';
import { OperationsPageBase } from './operations-page.base';
import { OperationsReportComponent } from './operations-report.component';

@Component({
  selector: 'app-server-history', standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe, OperationsReportComponent],
  template: `
    <app-operations-report page="history" [range]="range()" [metrics]="data()?.metrics ?? []"
      [loading]="loading()" [error]="error()" (rangeChange)="setRange($event)" (retry)="load()" />
    <section class="rk-panel history">
      <p>{{ 'botManagement.history.explanation' | transloco }}</p>
      <form class="filters" (ngSubmit)="apply()">
        <label>{{ 'botManagement.search' | transloco }}<input name="search" type="search" maxlength="100" [(ngModel)]="draftSearch" /></label>
        <label>{{ 'botManagement.columns.status' | transloco }}<select name="status" [(ngModel)]="draftStatus">
          @for (status of statuses; track status) { <option [value]="status">{{ ('botManagement.history.' + status) | transloco }}</option> }
        </select></label>
        <button class="rk-button" type="submit" [disabled]="loading()">{{ 'botManagement.incidents.apply' | transloco }}</button>
        <button class="rk-button" type="button" [disabled]="loading()" (click)="load()">{{ 'botManagement.history.refresh' | transloco }}</button>
      </form>
      @if (data(); as report) {
        <p class="muted" role="status">{{ 'botManagement.history.tracking' | transloco }} {{ date(report.trackingSince) }} · {{ 'botManagement.history.updated' | transloco }} {{ date(report.generatedAt) }}</p>
        <p>{{ 'botManagement.history.results' | transloco: { total: report.total } }}</p>
        <ol class="entries">
          @for (item of report.items; track item.id) {
            <li>
              <div class="entry-heading"><div><strong>{{ item.guildName }}</strong><small>{{ item.guildId }} · {{ 'botManagement.history.bot' | transloco }} {{ item.botId }} · {{ item.identity }}</small></div>
                <span class="badge" [class.removed]="item.removedAt">{{ ('botManagement.history.' + (item.removedAt ? 'removed' : 'present')) | transloco }}</span></div>
              <dl>
                <div><dt>{{ ('botManagement.history.' + (item.joinObserved ? 'installed' : 'knownJoin')) | transloco }}</dt><dd>{{ date(item.joinedAt) }}</dd></div>
                <div><dt>{{ 'botManagement.history.firstObserved' | transloco }}</dt><dd>{{ date(item.firstObservedAt) }}</dd></div>
                <div><dt>{{ 'botManagement.history.membersSnapshot' | transloco }}</dt><dd>{{ item.memberCount | number }}</dd></div>
                @if (item.removedAt) { <div><dt>{{ ('botManagement.history.' + (item.removalEvidence === 'detected' ? 'detected' : 'uninstalled')) | transloco }}</dt><dd>{{ date(item.removedAt) }}</dd></div> }
              </dl>
              @if (!item.joinObserved) { <small>{{ 'botManagement.history.baselineNote' | transloco }}</small> }
              @if (item.removalEvidence === 'detected') { <small>{{ 'botManagement.history.detectedNote' | transloco }}</small> }
            </li>
          } @empty { <li class="empty"><div class="mascot-slot" aria-hidden="true"></div>{{ 'botManagement.history.empty' | transloco }}</li> }
        </ol>
        <nav class="pagination" [attr.aria-label]="'botManagement.history.pagination' | transloco">
          <button class="rk-button" type="button" [disabled]="loading() || offset() === 0" (click)="page(offset() - 50)">{{ 'botManagement.history.previous' | transloco }}</button>
          <button class="rk-button" type="button" [disabled]="loading() || report.nextOffset === null" (click)="page(report.nextOffset!)">{{ 'botManagement.history.next' | transloco }}</button>
        </nav>
      }
    </section>`,
  styles: [`
    .history { margin-top: var(--rk-space-5); display: grid; gap: var(--rk-space-4); }
    p { margin: 0; } .muted, small, dt { color: var(--rk-text-muted); } small { display: block; overflow-wrap: anywhere; }
    .filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); align-items: end; gap: var(--rk-space-3); }
    label { display: grid; gap: var(--rk-space-2); min-width: 0; }
    input, select { width: 100%; min-height: var(--rk-control-height); }
    .entries { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--rk-space-3); }
    li { padding: var(--rk-space-4); border: 1px solid var(--rk-border-subtle); border-radius: var(--rk-radius-md); background: var(--rk-surface-2); }
    .entry-heading { display: flex; justify-content: space-between; flex-wrap: wrap; gap: var(--rk-space-3); }
    strong { overflow-wrap: anywhere; } .badge { align-self: start; color: var(--rk-info); background: var(--rk-info-subtle); padding: var(--rk-space-1) var(--rk-space-2); border-radius: var(--rk-radius-sm); }
    .badge.removed { color: var(--rk-warning); background: var(--rk-warning-subtle); }
    dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--rk-space-3); margin: var(--rk-space-4) 0; } dd { margin: var(--rk-space-1) 0 0; overflow-wrap: anywhere; }
    .pagination { display: flex; flex-wrap: wrap; justify-content: end; gap: var(--rk-space-3); }
    @media (max-width: 768px) { .filters, dl { grid-template-columns: 1fr; } }
  `]
})
export class ServerHistoryComponent extends OperationsPageBase<BotGuildHistoryResponse> {
  private readonly api = inject(BotManagementService);
  private readonly locale = inject(LocaleService);
  readonly statuses = ['all', 'present', 'removed'];
  readonly offset = signal(0);
  draftSearch = ''; draftStatus = 'all';
  private search = ''; private status = 'all';
  private requestedRange: BotManagementRange | null = null;
  constructor() { super(); this.initialize(); }
  protected request(range: BotManagementRange) {
    if (range !== this.requestedRange) this.offset.set(0);
    this.requestedRange = range;
    return this.api.getServerHistory(range, this.search, this.status, this.offset());
  }
  apply(): void { this.search = this.draftSearch.trim(); this.status = this.draftStatus; this.offset.set(0); this.load(); }
  page(offset: number): void { this.offset.set(Math.max(0, offset)); this.load(); }
  setRange(range: BotManagementRange): void { this.offset.set(0); this.changeRange(range); }
  date(value: string | null): string { return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : '—'; }
}
