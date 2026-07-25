import { CommonModule } from '@angular/common';
import { Component, DestroyRef, ElementRef, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subject, debounceTime, distinctUntilChanged, finalize, switchMap, tap } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { AdjustmentRequest, XpAuditDetails, XpAuditEntryFilter, XpAuditMember, XpLedgerEntry, XpLedgerKind, XpLedgerScope, XpTimelineItem, XpVoiceSegment } from '../../models/xp-audit.models';
import { ApiErrorService } from '../../services/api-error.service';
import { XpAuditService } from '../../services/xp-audit.service';
import { AppStore } from '../../store/app.store';
import { ToastService } from '../../services/toast.service';
import { UserAvatarComponent } from '../../components/user-avatar/user-avatar.component';

type AdjustmentDirection = 'add' | 'subtract';
interface VoiceDayState { expanded: boolean; loading: boolean; error: string; items: XpVoiceSegment[] | null; }

@Component({
  selector: 'app-xp-audit',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe, UserAvatarComponent],
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
  private readonly toast = inject(ToastService);
  private readonly searchInput = new Subject<string>();
  private selectedGuildId: string | null = null;
  private membersGeneration = 0;
  private selectionGeneration = 0;
  private entriesGeneration = 0;
  private voiceGeneration = 0;
  private adjustmentTrigger: HTMLElement | null = null;
  private reversalTrigger: HTMLElement | null = null;
  private modalTrigger: HTMLElement | null = null;

  @ViewChild('adjustmentDialog') adjustmentDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('reversalDialog') reversalDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('memberDialog') memberDialog?: ElementRef<HTMLDialogElement>;

  readonly query = signal('');
  readonly former = signal(false);
  readonly members = signal<XpAuditMember[]>([]);
  readonly memberCursor = signal<string | null>(null);
  readonly selectedMember = signal<XpAuditMember | null>(null);
  readonly details = signal<XpAuditDetails | null>(null);
  readonly entries = signal<XpLedgerEntry[]>([]);
  readonly entryCursor = signal<string | null>(null);
  readonly timelineItems = signal<XpTimelineItem[]>([]);
  readonly timelineCursor = signal<string | null>(null);
  readonly timelineMode = signal<'compact' | 'entries'>('compact');
  readonly activeModalTab = signal<'overview' | 'history' | 'adjust'>('history');
  readonly voiceDayStates = signal<Record<string, VoiceDayState>>({});
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
  readonly reversalEntry = signal<XpLedgerEntry | null>(null);
  readonly activeFilterCount = computed(() => Object.values(this.filters()).filter(value => value !== null && value !== undefined && value !== '').length);
  readonly voiceTimelineSelected = computed(() => this.filters().source?.toLowerCase() === 'voice');
  readonly visibleTabs = computed<Array<'overview' | 'history' | 'adjust'>>(() => this.details()?.permissions.canAdjust ? ['overview', 'history', 'adjust'] : ['overview', 'history']);

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
    this.searchInput.pipe(debounceTime(280), distinctUntilChanged(), tap(() => { this.membersLoading.set(true); this.membersError.set(''); }), switchMap(() => {
      const guildId = this.selectedGuildId;
      return guildId ? this.api.members(guildId, this.query(), this.former()) : [];
    }), takeUntilDestroyed(this.destroyRef)).subscribe({ next: page => { this.members.set(page.items); this.memberCursor.set(page.nextCursor); this.membersLoading.set(false); }, error: error => { this.membersLoading.set(false); this.membersError.set(this.apiErrors.resolve(error, 'errors.xpAuditMembersLoad').message); } });
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

  select(member: XpAuditMember, trigger?: Event): void {
    const guildId = this.selectedGuildId;
    if (!guildId) return;
    const generation = ++this.selectionGeneration;
    this.selectedMember.set(member);
    this.modalTrigger = trigger?.currentTarget as HTMLElement ?? null;
    this.activeModalTab.set('history');
    this.details.set(null);
    this.entries.set([]);
    this.entryCursor.set(null);
    this.detailsError.set('');
    this.entriesError.set('');
    this.timelineItems.set([]); this.timelineCursor.set(null);
    this.invalidateVoiceDays();
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
    setTimeout(() => { const dialog = this.memberDialog?.nativeElement; if (dialog?.isConnected && !dialog.open) dialog.showModal(); });
    this.loadHistory(false, generation, member.userId, guildId);
  }

  clearSelection(): void {
    this.selectionGeneration++;
    this.selectedMember.set(null);
    this.details.set(null);
    this.entries.set([]);
    this.entryCursor.set(null);
    this.detailsError.set('');
    this.entriesError.set('');
    this.timelineItems.set([]); this.timelineCursor.set(null);
    this.invalidateVoiceDays();
  }

  closeMemberDialog(): void {
    if (this.adjustmentSaving() || this.reversalSaving()) return;
    this.memberDialog?.nativeElement.close(); this.clearSelection(); this.modalTrigger?.focus(); this.modalTrigger = null;
  }

  updateFilter(key: keyof XpAuditEntryFilter, value: string): void {
    const filterValue = value || null;
    this.filters.update(filters => ({ ...filters, [key]: filterValue }));
    if (key === 'source' && value.toLowerCase() === 'voice') this.timelineMode.set('compact');
    this.invalidateVoiceDays();
    this.entryCursor.set(null);
    const member = this.selectedMember();
    if (member && this.selectedGuildId) this.loadHistory(false, this.selectionGeneration, member.userId, this.selectedGuildId);
  }

  updateDateFilter(key: 'from' | 'to', value: string): void {
    const date = value ? new Date(`${value}T${key === 'to' ? '23:59:59.999' : '00:00:00.000'}Z`).toISOString() : null;
    this.updateFilter(key, date ?? '');
  }

  resetFilters(): void {
    this.filters.set({});
    this.invalidateVoiceDays();
    this.entryCursor.set(null);
    const member = this.selectedMember();
    if (member && this.selectedGuildId) this.loadHistory(false, this.selectionGeneration, member.userId, this.selectedGuildId);
  }
  dateFilterValue(key: 'from' | 'to'): string { return this.filters()[key]?.slice(0, 10) ?? ''; }

  setTimelineMode(mode: 'compact' | 'entries'): void { if (mode === 'entries' && this.voiceTimelineSelected()) return; if (this.timelineMode() !== mode) { this.timelineMode.set(mode); this.entryCursor.set(null); this.timelineCursor.set(null); this.entries.set([]); this.timelineItems.set([]); this.loadHistory(); } }
  setTab(tab: 'overview' | 'history' | 'adjust'): void { if (this.visibleTabs().includes(tab)) this.activeModalTab.set(tab); }
  onTabsKeydown(event: KeyboardEvent): void { const tabs = this.visibleTabs(); const index = tabs.indexOf(this.activeModalTab()); if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') { event.preventDefault(); this.setTab(tabs[(index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]); } }
  loadHistory(more = false, generation = this.selectionGeneration, userId = this.selectedMember()?.userId, guildId = this.selectedGuildId): void { if (this.timelineMode() === 'entries') this.loadEntries(more, generation, userId, guildId); else this.loadTimeline(more, generation, userId, guildId); }
  loadTimeline(more = false, expectedGeneration = this.selectionGeneration, userId = this.selectedMember()?.userId, guildId = this.selectedGuildId): void {
    const cursor = more ? this.timelineCursor() : null; if (!guildId || !userId || expectedGeneration !== this.selectionGeneration || (more && !cursor)) return;
    (more ? this.moreEntriesLoading : this.entriesLoading).set(true); this.entriesError.set(''); const request = ++this.entriesGeneration;
    this.api.timeline(guildId, userId, { ...this.filters(), cursor }).pipe(finalize(() => { if (request === this.entriesGeneration) (more ? this.moreEntriesLoading : this.entriesLoading).set(false); }), takeUntilDestroyed(this.destroyRef)).subscribe({ next: page => { if (request !== this.entriesGeneration || expectedGeneration !== this.selectionGeneration) return; const ids = new Set(this.timelineItems().map(x => x.id)); this.timelineItems.set(more ? [...this.timelineItems(), ...page.items.filter(x => !ids.has(x.id))] : page.items); this.timelineCursor.set(page.nextCursor); }, error: error => { if (request === this.entriesGeneration) this.entriesError.set(this.apiErrors.resolve(error, 'errors.xpAuditEntriesLoad').message); } });
  }
  voiceDayState(item: XpTimelineItem): VoiceDayState | null { return this.voiceDayStates()[this.voiceDayCacheKey(item)] ?? null; }
  toggleVoiceDay(item: XpTimelineItem): void {
    const key = this.voiceDayCacheKey(item); const current = this.voiceDayStates()[key];
    if (current?.loading) return;
    if (current?.items) { this.setVoiceDayState(key, { ...current, expanded: !current.expanded }); return; }
    if (current?.expanded) { this.setVoiceDayState(key, { ...current, expanded: false }); return; }
    this.loadVoiceDay(item, key);
  }
  retryVoiceDay(item: XpTimelineItem): void { this.loadVoiceDay(item, this.voiceDayCacheKey(item)); }

  private loadVoiceDay(item: XpTimelineItem, key: string): void {
    const guild = this.selectedGuildId; const user = this.selectedMember()?.userId;
    if (!guild || !user || !item.dayKey) return;
    const generation = this.voiceGeneration;
    this.setVoiceDayState(key, { expanded: true, loading: true, error: '', items: null });
    this.api.voiceDaySegments(guild, user, item.dayKey, this.filters()).pipe(finalize(() => {
      const state = this.voiceDayStates()[key]; if (generation === this.voiceGeneration && state?.loading) this.setVoiceDayState(key, { ...state, loading: false });
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: page => { if (generation === this.voiceGeneration && key === this.voiceDayCacheKey(item)) this.setVoiceDayState(key, { expanded: true, loading: false, error: '', items: page.items }); },
      error: error => { if (generation === this.voiceGeneration && key === this.voiceDayCacheKey(item)) this.setVoiceDayState(key, { expanded: true, loading: false, error: this.apiErrors.resolve(error, 'errors.xpAuditEntriesLoad').message, items: null }); }
    });
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
    this.api.adjust(guildId, details.userId, body).pipe(finalize(() => this.adjustmentSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.closeAdjustmentDialog();
        this.resetAdjustmentForm();
        this.refreshSelected();
        this.toast.success(this.translate('adjustmentSaved'));
      },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.xpAdjustmentSave').message)
    });
  }

  openReversal(entry: XpLedgerEntry, trigger: Event): void {
    if (this.reversalSaving()) return;
    this.reversalEntry.set(entry);
    this.reversalReason = '';
    this.reversalReference = '';
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
    this.api.reverse(guildId, entry.id, { reason: this.reversalReason.trim(), reference: this.reversalReference.trim() || undefined, requestId: this.reversalRequestId }).pipe(finalize(() => this.reversalSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.closeReversalDialog();
        this.refreshSelected();
        this.toast.success(this.translate('reversalSaved'));
      },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.xpAdjustmentReverse').message)
    });
  }

  initials(name: string): string {
    return name.trim().split(/\s+/).slice(0, 2).map(part => part[0]).join('').toUpperCase() || '?';
  }

  formatTotalXp(value: string | number): string {
    return this.locale.number(value, { maximumFractionDigits: 0 });
  }
  formatXp(value: string | number): string { return this.formatTotalXp(value); }
  formatTimelineXp(value: string | number): string { return this.locale.number(value, { maximumFractionDigits: 2 }); }
  formatRawXp(value: string | number): string { return this.locale.number(value, { maximumFractionDigits: 6 }); }

  formatSignedXp(value: string | number): string {
    const amount = Number(value);
    return `${amount > 0 ? '+' : amount < 0 ? '−' : ''}${this.formatRawXp(Math.abs(amount))} XP`;
  }

  formatDate(value: string | null): string {
    return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : this.translate('noActivity');
  }

  copyDiscordId(userId: string): void {
    if (!navigator.clipboard) { this.toast.error(this.translate('copyFailed')); return; }
    void navigator.clipboard.writeText(userId).then(() => this.toast.success(this.translate('discordIdCopied'))).catch(() => this.toast.error(this.translate('copyFailed')));
  }
  formatDuration(seconds: number | null): string {
    if (!seconds) return '—';
    const hours = Math.floor(seconds / 3600); const minutes = Math.floor((seconds % 3600) / 60);
    if (hours) return `${this.translate('durationHours', { count: hours })}${minutes ? ` ${this.translate('durationMinutes', { count: minutes })}` : ''}`;
    return minutes ? this.translate('durationMinutes', { count: minutes }) : this.translate('durationSeconds', { count: seconds });
  }
  formatDay(day: string): string { return this.locale.date(`${day}T00:00:00.000Z`, { dateStyle: 'medium', timeZone: 'UTC' }); }
  formatTimeRange(from: string, to: string): string { return `${this.locale.date(from, { timeStyle: 'short' })}–${this.locale.date(to, { timeStyle: 'short' })}`; }

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
    this.voiceGeneration++;
    this.members.set([]);
    this.memberCursor.set(null);
    this.clearSelection();
    this.filters.set({});
    this.invalidateVoiceDays();
    this.membersError.set('');
    this.resetAdjustmentForm();
    this.memberDialog?.nativeElement.close(); this.reversalDialog?.nativeElement.close(); this.adjustmentDialog?.nativeElement.close();
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
  private voiceDayCacheKey(item: XpTimelineItem): string { return `${this.selectedGuildId}|${this.selectedMember()?.userId}|${item.dayKey}|${JSON.stringify(this.filters())}`; }
  private setVoiceDayState(key: string, state: VoiceDayState): void { this.voiceDayStates.update(states => {
    const bounded = { ...states, [key]: state }; const keys = Object.keys(bounded); if (keys.length > 32) delete bounded[keys[0]]; return bounded;
  }); }
  private invalidateVoiceDays(): void { this.voiceGeneration++; this.voiceDayStates.set({}); }
  private refreshSelected(): void { const member = this.selectedMember(); if (!member || !this.selectedGuildId) return; const tab = this.activeModalTab(); this.select(member); this.activeModalTab.set(tab === 'adjust' ? 'history' : tab); }

  private translate(key: string, params?: Record<string, unknown>): string {
    return this.i18n.translate(`xpAudit.${key}`, params);
  }

  private newRequestId(): string {
    return crypto.randomUUID();
  }
}
