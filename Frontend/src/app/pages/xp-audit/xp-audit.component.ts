import { CommonModule, DecimalPipe, DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { debounceTime, Subject } from 'rxjs';
import { AppStore } from '../../store/app.store';
import { XpAuditService } from '../../services/xp-audit.service';
import { AdjustmentRequest, XpAuditDetails, XpAuditMember, XpLedgerEntry } from '../../models/xp-audit.models';

@Component({ selector: 'app-xp-audit', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe, DecimalPipe, DatePipe], templateUrl: './xp-audit.component.html', styleUrl: './xp-audit.component.scss' })
export class XpAuditComponent {
  private readonly api = inject(XpAuditService); private readonly store = inject(AppStore); private readonly searchInput = new Subject<string>();
  readonly query = signal(''); readonly former = signal(false); readonly members = signal<XpAuditMember[]>([]); readonly details = signal<XpAuditDetails | null>(null); readonly entries = signal<XpLedgerEntry[]>([]); readonly memberCursor = signal<string | null>(null); readonly entryCursor = signal<string | null>(null); readonly loading = signal(false); readonly saving = signal(false); readonly error = signal('');
  amount = 0; scope: 'LifetimeOnly' | 'LifetimeAndSeason' = 'LifetimeAndSeason'; reason = ''; reference = ''; private requestId = crypto.randomUUID();
  constructor() { this.searchInput.pipe(debounceTime(250)).subscribe(() => this.loadMembers()); this.loadMembers(); }
  search(value: string) { this.query.set(value); this.memberCursor.set(null); this.searchInput.next(value); }
  toggleFormer() { this.former.update(x => !x); this.memberCursor.set(null); this.loadMembers(); }
  loadMembers(more = false) { const guild = this.store.selectedGuild()?.id; if (!guild) return; this.loading.set(true); this.api.members(guild, this.query(), this.former(), more ? this.memberCursor() ?? undefined : undefined).subscribe({ next: page => { this.members.set(more ? [...this.members(), ...page.items] : page.items); this.memberCursor.set(page.nextCursor); this.loading.set(false); }, error: () => { this.error.set('xpAudit.loadFailed'); this.loading.set(false); } }); }
  select(member: XpAuditMember) { const guild = this.store.selectedGuild()?.id; if (!guild) return; this.api.details(guild, member.userId).subscribe(x => this.details.set(x)); this.api.entries(guild, member.userId).subscribe(x => { this.entries.set(x.items); this.entryCursor.set(x.nextCursor); }); }
  moreEntries() { const guild = this.store.selectedGuild()?.id; const d = this.details(); if (!guild || !d || !this.entryCursor()) return; this.api.entries(guild, d.userId, this.entryCursor()!).subscribe(x => { this.entries.update(v => [...v, ...x.items]); this.entryCursor.set(x.nextCursor); }); }
  submit() { const guild = this.store.selectedGuild()?.id; const d = this.details(); if (!guild || !d || this.saving() || !this.reason.trim() || !this.amount) return; this.saving.set(true); const body: AdjustmentRequest = { amount: this.amount, scope: this.scope, reason: this.reason.trim(), reference: this.reference.trim() || undefined, requestId: this.requestId }; this.api.adjust(guild, d.userId, body).subscribe({ next: () => { this.saving.set(false); this.requestId = crypto.randomUUID(); this.select({ userId: d.userId, displayName: d.displayName, isCurrentMember: d.isCurrentMember, totalXp: d.lifetime.totalXp, level: d.lifetime.level }); }, error: () => this.saving.set(false) }); }
  reverse(entry: XpLedgerEntry) { const guild = this.store.selectedGuild()?.id; const d = this.details(); if (!guild || !d || !this.reason.trim()) return; this.api.reverse(guild, entry.id, { reason: this.reason.trim(), reference: this.reference.trim() || undefined, requestId: crypto.randomUUID() }).subscribe(() => this.select({ userId: d.userId, displayName: d.displayName, isCurrentMember: d.isCurrentMember, totalXp: d.lifetime.totalXp, level: d.lifetime.level })); }
}
