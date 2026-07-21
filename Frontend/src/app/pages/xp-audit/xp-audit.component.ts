import { CommonModule } from '@angular/common';
import { Component, DestroyRef, ElementRef, ViewChild, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subject, debounceTime, distinctUntilChanged, finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { AdjustmentRequest, XpAuditDetails, XpAuditEntryFilter, XpAuditMember, XpLedgerEntry, XpLedgerKind, XpLedgerScope } from '../../models/xp-audit.models';
import { ApiErrorService } from '../../services/api-error.service';
import { XpAuditService } from '../../services/xp-audit.service';
import { AppStore } from '../../store/app.store';

type AdjustmentDirection = 'add' | 'subtract';

@Component({
  selector: 'app-xp-audit',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  templateUrl: './xp-audit.component.html',
  styleUrl: './xp-audit.component.scss'
})
export class XpAuditComponent {
  private readonly api = inject(XpAuditService);
  private readonly store = inject(AppStore);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly searchInput = new Subject<string>();
  private selectedGuildId: string | null = null;
  private membersGeneration = 0;
  private selectionGeneration = 0;
  private entriesGeneration = 0;
  private adjustmentTrigger: HTMLElement | null = null;
  private reversalTrigger: HTMLElement | null = null;

  @ViewChild('adjustmentDialog') adjustmentDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('reversalDialog') reversalDialog?: ElementRef<HTMLDialogElement>;

  readonly query = signal('');
  readonly former = signal(false);
  readonly members = signal<XpAuditMember[]>([]);
  readonly memberCursor = signal<string | null>(null);
  readonly selectedMember = signal<XpAuditMember | null>(null);
  readonly details = signal<XpAuditDetails | null>(null);
  readonly entries = signal<XpLedgerEntry[]>([]);
  readonly entryCursor = signal<string | null>(null);
  readonly filters = signal<XpAuditEntryFilter>({});

  readonly membersLoading = signal(false);
  readonly moreMembersLoading = signal(false);
  readonly detailsLoading = signal(false);
  readonly entriesLoading = signal(false);
  readonly moreEntriesLoading = signal(false);
  readonly adjustmentSaving = signal(false);
  readonly reversalSaving = signal(false);
  readonly membersError = signal('');
  readonly detailsError = signal('');
  readonly entriesError = signal('');
  readonly adjustmentError = signal('');
  readonly reversalError = signal('');
  readonly successMessage = signal('');
  readonly reversalEntry = signal<XpLedgerEntry | null>(null);

  amount: string | number | null = '';
  direction: AdjustmentDirection = 'add';
  scope: 'LifetimeOnly' | 'LifetimeAndSeason' = 'LifetimeOnly';
  reason = '';
  reference = '';
  reversalReason = '';
  reversalReference = '';
  private adjustmentRequestId = this.newRequestId();
  private reversalRequestId = this.newRequestId();

  constructor() {
    this.searchInput.pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef)).subscribe(() => this.loadMembers());
    effect(() => {
      const guildId = this.store.selectedGuild()?.id ?? null;
      if (guildId === this.selectedGuildId) return;
      this.selectedGuildId = guildId;
      this.resetForGuild();
      if (guildId) this.loadMembers();
    });
  }

  search(value: string): void {
    this.query.set(value);
    this.memberCursor.set(null);
    this.searchInput.next(value);
  }

  toggleFormer(): void {
    this.former.update(value => !value);
    this.memberCursor.set(null);
    this.loadMembers();
  }

  loadMembers(more = false): void {
    const guildId = this.selectedGuildId;
    const cursor = more ? this.memberCursor() : null;
    if (!guildId || (more && (!cursor || this.moreMembersLoading())) || (!more && this.membersLoading())) return;
    const generation = ++this.membersGeneration;
    (more ? this.moreMembersLoading : this.membersLoading).set(true);
    this.membersError.set('');
    this.api.members(guildId, this.query(), this.former(), cursor ?? undefined).pipe(
      finalize(() => {
        if (generation === this.membersGeneration) (more ? this.moreMembersLoading : this.membersLoading).set(false);
      }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: page => {
        if (generation !== this.membersGeneration || guildId !== this.selectedGuildId) return;
        this.members.set(more ? this.mergeMembers(this.members(), page.items) : page.items);
        this.memberCursor.set(page.nextCursor);
      },
      error: error => {
        if (generation === this.membersGeneration) this.membersError.set(this.apiErrors.resolve(error, 'errors.xpAuditMembersLoad').message);
      }
    });
  }

  select(member: XpAuditMember): void {
    const guildId = this.selectedGuildId;
    if (!guildId) return;
    const generation = ++this.selectionGeneration;
    this.selectedMember.set(member);
    this.details.set(null);
    this.entries.set([]);
    this.entryCursor.set(null);
    this.detailsError.set('');
    this.entriesError.set('');
    this.successMessage.set('');
    this.detailsLoading.set(true);
    this.entriesLoading.set(true);
    this.api.details(guildId, member.userId).pipe(finalize(() => {
      if (generation === this.selectionGeneration) this.detailsLoading.set(false);
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: details => {
        if (generation !== this.selectionGeneration || guildId !== this.selectedGuildId) return;
        this.details.set(details);
        if (!details.activeSeason) this.scope = 'LifetimeOnly';
      },
      error: error => {
        if (generation === this.selectionGeneration) this.detailsError.set(this.apiErrors.resolve(error, 'errors.xpAuditDetailsLoad').message);
      }
    });
    this.loadEntries(false, generation, member.userId, guildId);
  }

  clearSelection(): void {
    this.selectionGeneration++;
    this.selectedMember.set(null);
    this.details.set(null);
    this.entries.set([]);
    this.entryCursor.set(null);
    this.detailsError.set('');
    this.entriesError.set('');
  }

  updateFilter(key: keyof XpAuditEntryFilter, value: string): void {
    const filterValue = value || null;
    this.filters.update(filters => ({ ...filters, [key]: filterValue }));
    this.entryCursor.set(null);
    const member = this.selectedMember();
    if (member && this.selectedGuildId) this.loadEntries(false, this.selectionGeneration, member.userId, this.selectedGuildId);
  }

  updateDateFilter(key: 'from' | 'to', value: string): void {
    const date = value ? new Date(`${value}T${key === 'to' ? '23:59:59.999' : '00:00:00.000'}`).toISOString() : null;
    this.updateFilter(key, date ?? '');
  }

  resetFilters(): void {
    this.filters.set({});
    this.entryCursor.set(null);
    const member = this.selectedMember();
    if (member && this.selectedGuildId) this.loadEntries(false, this.selectionGeneration, member.userId, this.selectedGuildId);
  }

  loadEntries(more = false, expectedGeneration = this.selectionGeneration, userId = this.selectedMember()?.userId, guildId = this.selectedGuildId): void {
    const cursor = more ? this.entryCursor() : null;
    if (!guildId || !userId || expectedGeneration !== this.selectionGeneration || (more && (!cursor || this.moreEntriesLoading()))) return;
    (more ? this.moreEntriesLoading : this.entriesLoading).set(true);
    this.entriesError.set('');
    const requestGeneration = ++this.entriesGeneration;
    this.api.entries(guildId, userId, { ...this.filters(), cursor }).pipe(finalize(() => {
      if (expectedGeneration === this.selectionGeneration && requestGeneration === this.entriesGeneration) (more ? this.moreEntriesLoading : this.entriesLoading).set(false);
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: page => {
        if (expectedGeneration !== this.selectionGeneration || requestGeneration !== this.entriesGeneration || guildId !== this.selectedGuildId) return;
        this.entries.set(more ? this.mergeEntries(this.entries(), page.items) : page.items);
        this.entryCursor.set(page.nextCursor);
      },
      error: error => {
        if (expectedGeneration === this.selectionGeneration && requestGeneration === this.entriesGeneration) this.entriesError.set(this.apiErrors.resolve(error, 'errors.xpAuditEntriesLoad').message);
      }
    });
  }

  get amountError(): string {
    const amountInput = this.amount == null ? '' : String(this.amount).trim();
    const amount = Number(amountInput);
    if (!amountInput || !Number.isFinite(amount) || amount <= 0) return this.translate('amountRequired');
    if (amount > 1_000_000) return this.translate('amountMaximum');
    if (!/^\d+(\.\d{1,4})?$/.test(amountInput)) return this.translate('amountPrecision');
    return '';
  }

  get reasonError(): string {
    return this.reason.trim().length > 0 && this.reason.trim().length < 10 ? this.translate('reasonMinimum') : '';
  }

  get reversalReasonError(): string {
    return this.reversalReason.trim().length > 0 && this.reversalReason.trim().length < 10 ? this.translate('reasonMinimum') : '';
  }

  get canReviewAdjustment(): boolean {
    return !this.amountError && this.reason.trim().length >= 10 && this.reason.trim().length <= 1000 && this.reference.length <= 250;
  }

  get canConfirmReversal(): boolean {
    return !this.reversalReasonError && this.reversalReason.trim().length >= 10 && this.reversalReason.trim().length <= 1000 && this.reversalReference.length <= 250;
  }

  signedAmount(): number {
    const amount = Number(this.amount ?? 0);
    return this.direction === 'subtract' ? -amount : amount;
  }

  projectedTotal(total: string | number): number {
    return Number(total) + this.signedAmount();
  }

  reviewAdjustment(trigger: Event): void {
    if (!this.canReviewAdjustment || this.adjustmentSaving()) return;
    this.adjustmentError.set('');
    this.adjustmentTrigger = trigger.currentTarget as HTMLElement;
    setTimeout(() => this.adjustmentDialog?.nativeElement.showModal());
  }

  closeAdjustmentDialog(): void {
    this.adjustmentDialog?.nativeElement.close();
    this.adjustmentTrigger?.focus();
  }

  adjust(): void {
    const guildId = this.selectedGuildId;
    const details = this.details();
    if (!guildId || !details || !this.canReviewAdjustment || this.adjustmentSaving()) return;
    const body: AdjustmentRequest = { amount: this.signedAmount(), scope: details.activeSeason ? this.scope : 'LifetimeOnly', reason: this.reason.trim(), reference: this.reference.trim() || undefined, requestId: this.adjustmentRequestId };
    this.adjustmentSaving.set(true);
    this.adjustmentError.set('');
    this.api.adjust(guildId, details.userId, body).pipe(finalize(() => this.adjustmentSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.closeAdjustmentDialog();
        this.resetAdjustmentForm();
        this.select(this.selectedMember()!);
        this.successMessage.set(this.translate('adjustmentSaved'));
      },
      error: error => this.adjustmentError.set(this.apiErrors.resolve(error, 'errors.xpAdjustmentSave').message)
    });
  }

  openReversal(entry: XpLedgerEntry, trigger: Event): void {
    if (this.reversalSaving()) return;
    this.reversalEntry.set(entry);
    this.reversalReason = '';
    this.reversalReference = '';
    this.reversalError.set('');
    this.reversalRequestId = this.newRequestId();
    this.reversalTrigger = trigger.currentTarget as HTMLElement;
    setTimeout(() => this.reversalDialog?.nativeElement.showModal());
  }

  closeReversalDialog(): void {
    this.reversalDialog?.nativeElement.close();
    this.reversalEntry.set(null);
    this.reversalTrigger?.focus();
  }

  reverse(): void {
    const guildId = this.selectedGuildId;
    const entry = this.reversalEntry();
    if (!guildId || !entry || !this.canConfirmReversal || this.reversalSaving()) return;
    this.reversalSaving.set(true);
    this.reversalError.set('');
    this.api.reverse(guildId, entry.id, { reason: this.reversalReason.trim(), reference: this.reversalReference.trim() || undefined, requestId: this.reversalRequestId }).pipe(finalize(() => this.reversalSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.closeReversalDialog();
        this.select(this.selectedMember()!);
        this.successMessage.set(this.translate('reversalSaved'));
      },
      error: error => this.reversalError.set(this.apiErrors.resolve(error, 'errors.xpAdjustmentReverse').message)
    });
  }

  initials(name: string): string {
    return name.trim().split(/\s+/).slice(0, 2).map(part => part[0]).join('').toUpperCase() || '?';
  }

  formatXp(value: string | number): string {
    return this.locale.number(value, { maximumFractionDigits: 4 });
  }

  formatSignedXp(value: string | number): string {
    const amount = Number(value);
    return `${amount > 0 ? '+' : amount < 0 ? '−' : ''}${this.formatXp(Math.abs(amount))} XP`;
  }

  formatDate(value: string | null): string {
    return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : this.translate('noActivity');
  }

  copyDiscordId(userId: string): void {
    void navigator.clipboard?.writeText(userId);
  }

  kindLabel(kind: XpLedgerKind): string { return this.translate(`kinds.${kind}`); }
  scopeLabel(scope: XpLedgerScope): string { return this.translate(`scopes.${scope}`); }
  sourceLabel(source: string): string {
    const key = `sources.${source}`;
    const translated = this.translate(key);
    return translated === `xpAudit.${key}` ? this.translate('sources.other') : translated;
  }
  isReversible(entry: XpLedgerEntry, details: XpAuditDetails): boolean {
    return details.permissions.canAdjust && entry.kind === 'ManualAdjustment' && !entry.reversedByLedgerEntryId;
  }

  private resetForGuild(): void {
    this.membersGeneration++;
    this.selectionGeneration++;
    this.entriesGeneration++;
    this.members.set([]);
    this.memberCursor.set(null);
    this.clearSelection();
    this.filters.set({});
    this.membersError.set('');
    this.successMessage.set('');
    this.resetAdjustmentForm();
  }

  private resetAdjustmentForm(): void {
    this.amount = '';
    this.direction = 'add';
    this.scope = 'LifetimeOnly';
    this.reason = '';
    this.reference = '';
    this.adjustmentRequestId = this.newRequestId();
  }

  private mergeMembers(current: XpAuditMember[], added: XpAuditMember[]): XpAuditMember[] {
    const ids = new Set(current.map(member => member.userId));
    return [...current, ...added.filter(member => !ids.has(member.userId))];
  }

  private mergeEntries(current: XpLedgerEntry[], added: XpLedgerEntry[]): XpLedgerEntry[] {
    const ids = new Set(current.map(entry => entry.id));
    return [...current, ...added.filter(entry => !ids.has(entry.id))];
  }

  private translate(key: string): string {
    return this.i18n.translate(`xpAudit.${key}`);
  }

  private newRequestId(): string {
    return crypto.randomUUID();
  }
}
