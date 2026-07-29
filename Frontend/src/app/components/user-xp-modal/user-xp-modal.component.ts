import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, EventEmitter, Output, ViewChild, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import {
  AdjustmentRequest,
  UserXpModalOpenOptions,
  UserXpModalTab,
  UserXpModalSubject,
  XpAuditDetails,
  XpAuditEntryFilter,
  XpLedgerEntry,
  XpLedgerKind,
  XpLedgerScope,
  XpTimelineItem,
  XpVoiceSegment
} from '../../models/xp-audit.models';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';
import { XpAuditService } from '../../services/xp-audit.service';
import { UserAvatarComponent } from '../user-avatar/user-avatar.component';

type AdjustmentDirection = 'add' | 'subtract';
interface VoiceDayState { expanded: boolean; loading: boolean; error: string; items: XpVoiceSegment[] | null; }

@Component({
  selector: 'app-user-xp-modal',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe, UserAvatarComponent],
  templateUrl: './user-xp-modal.component.html',
  styleUrl: './user-xp-modal.component.scss'
})
export class UserXpModalComponent {
  private readonly api = inject(XpAuditService);
  private readonly i18n = inject(TranslocoService);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly toast = inject(ToastService);
  private selectedGuildId: string | null = null;
  private selectionGeneration = 0;
  private entriesGeneration = 0;
  private voiceGeneration = 0;
  private adjustmentTrigger: HTMLElement | null = null;
  private reversalTrigger: HTMLElement | null = null;
  private modalTrigger: HTMLElement | null = null;

  @Output() readonly xpChanged = new EventEmitter<{ guildId: string; userId: string }>();

  @ViewChild('adjustmentDialog') adjustmentDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('reversalDialog') reversalDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('memberDialog') memberDialog?: ElementRef<HTMLDialogElement>;

  readonly subject = signal<UserXpModalSubject | null>(null);
  readonly details = signal<XpAuditDetails | null>(null);
  readonly entries = signal<XpLedgerEntry[]>([]);
  readonly entryCursor = signal<string | null>(null);
  readonly timelineItems = signal<XpTimelineItem[]>([]);
  readonly timelineCursor = signal<string | null>(null);
  readonly timelineMode = signal<'compact' | 'entries'>('compact');
  readonly activeModalTab = signal<UserXpModalTab>('history');
  readonly voiceDayStates = signal<Record<string, VoiceDayState>>({});
  readonly filters = signal<XpAuditEntryFilter>({});
  readonly detailsLoading = signal(false);
  readonly entriesLoading = signal(false);
  readonly moreEntriesLoading = signal(false);
  readonly adjustmentSaving = signal(false);
  readonly reversalSaving = signal(false);
  readonly detailsError = signal('');
  readonly entriesError = signal('');
  readonly reversalEntry = signal<XpLedgerEntry | null>(null);
  readonly activeFilterCount = computed(() => Object.values(this.filters()).filter(value => value !== null && value !== undefined && value !== '').length);
  readonly voiceTimelineSelected = computed(() => this.filters().source?.toLowerCase() === 'voice');
  readonly visibleTabs = computed<UserXpModalTab[]>(() => this.details()?.permissions.canAdjust ? ['overview', 'history', 'adjust'] : ['overview', 'history']);

  amount: string | number | null = '';
  direction: AdjustmentDirection = 'add';
  scope: 'LifetimeOnly' | 'LifetimeAndSeason' = 'LifetimeOnly';
  reason = '';
  reference = '';
  reversalReason = '';
  reversalReference = '';
  private adjustmentRequestId = this.newRequestId();
  private reversalRequestId = this.newRequestId();

  open(subject: UserXpModalSubject, options: UserXpModalOpenOptions = {}): void {
    const guildId = subject.guildId;
    const generation = ++this.selectionGeneration;
    this.selectedGuildId = guildId;
    this.moreEntriesLoading.set(false);
    this.resetAdjustmentForm();
    this.reversalEntry.set(null);
    this.reversalDialog?.nativeElement.close();
    this.adjustmentDialog?.nativeElement.close();
    this.subject.set(subject);
    this.modalTrigger = this.resolveTrigger(options.trigger);
    this.activeModalTab.set(options.initialTab ?? 'history');
    this.details.set(null);
    this.entries.set([]);
    this.entryCursor.set(null);
    this.timelineItems.set([]);
    this.timelineCursor.set(null);
    this.detailsError.set('');
    this.entriesError.set('');
    this.invalidateVoiceDays();
    this.detailsLoading.set(true);
    this.entriesLoading.set(true);

    this.api.details(guildId, subject.userId).pipe(finalize(() => {
      if (generation === this.selectionGeneration) this.detailsLoading.set(false);
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: details => {
        if (generation !== this.selectionGeneration || guildId !== this.selectedGuildId) return;
        this.details.set(details);
        if (!details.activeSeason) this.scope = 'LifetimeOnly';
        if (!this.visibleTabs().includes(this.activeModalTab())) this.activeModalTab.set('history');
      },
      error: error => {
        if (generation !== this.selectionGeneration) return;
        if (error instanceof HttpErrorResponse && error.status === 403) {
          this.toast.error(this.translate('noAuditPermission'));
          this.close();
          return;
        }
        this.detailsError.set(this.resolveLoadError(error, 'xpAudit.openFailed'));
      }
    });
    this.showAndFocusDialog(generation);
    this.loadHistory(false, generation, subject.userId, guildId);
  }

  close(): void {
    if (this.adjustmentSaving() || this.reversalSaving()) return;
    const dialog = this.memberDialog?.nativeElement;
    if (dialog?.open) dialog.close();
    else this.onDialogClosed();
  }

  onDialogClosed(): void {
    const trigger = this.modalTrigger;
    this.clearSubject();
    this.modalTrigger = null;
    trigger?.focus();
  }

  updateFilter(key: keyof XpAuditEntryFilter, value: string): void {
    this.filters.update(filters => ({ ...filters, [key]: value || null }));
    if (key === 'source' && value.toLowerCase() === 'voice') this.timelineMode.set('compact');
    this.invalidateVoiceDays();
    this.entryCursor.set(null);
    this.timelineCursor.set(null);
    this.loadHistory();
  }

  updateDateFilter(key: 'from' | 'to', value: string): void {
    const date = value ? new Date(`${value}T${key === 'to' ? '23:59:59.999' : '00:00:00.000'}Z`).toISOString() : null;
    this.updateFilter(key, date ?? '');
  }

  resetFilters(): void {
    this.filters.set({});
    this.invalidateVoiceDays();
    this.entryCursor.set(null);
    this.timelineCursor.set(null);
    this.loadHistory();
  }

  dateFilterValue(key: 'from' | 'to'): string { return this.filters()[key]?.slice(0, 10) ?? ''; }
  setTimelineMode(mode: 'compact' | 'entries'): void { if (mode === 'entries' && this.voiceTimelineSelected()) return; if (this.timelineMode() !== mode) { this.timelineMode.set(mode); this.entryCursor.set(null); this.timelineCursor.set(null); this.entries.set([]); this.timelineItems.set([]); this.loadHistory(); } }
  setTab(tab: UserXpModalTab): void { if (this.visibleTabs().includes(tab)) { this.activeModalTab.set(tab); this.focusActiveTab(); } }
  onTabsKeydown(event: KeyboardEvent): void { const tabs = this.visibleTabs(); const index = tabs.indexOf(this.activeModalTab()); if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') { event.preventDefault(); this.setTab(tabs[(index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]); } }
  loadHistory(more = false, generation = this.selectionGeneration, userId = this.subject()?.userId, guildId = this.selectedGuildId): void { if (this.timelineMode() === 'entries') this.loadEntries(more, generation, userId, guildId); else this.loadTimeline(more, generation, userId, guildId); }

  loadTimeline(more = false, expectedGeneration = this.selectionGeneration, userId = this.subject()?.userId, guildId = this.selectedGuildId): void {
    const cursor = more ? this.timelineCursor() : null;
    if (!guildId || !userId || expectedGeneration !== this.selectionGeneration || (more && (!cursor || this.moreEntriesLoading()))) return;
    if (!more) this.moreEntriesLoading.set(false);
    (more ? this.moreEntriesLoading : this.entriesLoading).set(true);
    this.entriesError.set('');
    const request = ++this.entriesGeneration;
    this.api.timeline(guildId, userId, { ...this.filters(), cursor }).pipe(finalize(() => {
      if (request === this.entriesGeneration) (more ? this.moreEntriesLoading : this.entriesLoading).set(false);
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: page => {
        if (request !== this.entriesGeneration || expectedGeneration !== this.selectionGeneration || guildId !== this.selectedGuildId) return;
        const ids = new Set(this.timelineItems().map(item => item.id));
        this.timelineItems.set(more ? [...this.timelineItems(), ...page.items.filter(item => !ids.has(item.id))] : page.items);
        this.timelineCursor.set(page.nextCursor);
      },
      error: error => { if (request === this.entriesGeneration) this.entriesError.set(this.resolveLoadError(error, 'errors.xpAuditEntriesLoad')); }
    });
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

  loadEntries(more = false, expectedGeneration = this.selectionGeneration, userId = this.subject()?.userId, guildId = this.selectedGuildId): void {
    const cursor = more ? this.entryCursor() : null;
    if (!guildId || !userId || expectedGeneration !== this.selectionGeneration || (more && (!cursor || this.moreEntriesLoading()))) return;
    if (!more) this.moreEntriesLoading.set(false);
    (more ? this.moreEntriesLoading : this.entriesLoading).set(true);
    this.entriesError.set('');
    const request = ++this.entriesGeneration;
    this.api.entries(guildId, userId, { ...this.filters(), cursor }).pipe(finalize(() => {
      if (expectedGeneration === this.selectionGeneration && request === this.entriesGeneration) (more ? this.moreEntriesLoading : this.entriesLoading).set(false);
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: page => {
        if (expectedGeneration !== this.selectionGeneration || request !== this.entriesGeneration || guildId !== this.selectedGuildId) return;
        this.entries.set(more ? this.mergeEntries(this.entries(), page.items) : page.items);
        this.entryCursor.set(page.nextCursor);
      },
      error: error => { if (expectedGeneration === this.selectionGeneration && request === this.entriesGeneration) this.entriesError.set(this.resolveLoadError(error, 'errors.xpAuditEntriesLoad')); }
    });
  }

  get amountError(): string {
    const input = this.amount == null ? '' : String(this.amount).trim();
    const amount = Number(input);
    if (!input || !Number.isFinite(amount) || amount <= 0) return this.translate('amountRequired');
    if (amount > 1_000_000) return this.translate('amountMaximum');
    if (!/^\d+(\.\d{1,4})?$/.test(input)) return this.translate('amountPrecision');
    return '';
  }
  get reasonError(): string { return this.reason.trim().length > 0 && this.reason.trim().length < 10 ? this.translate('reasonMinimum') : ''; }
  get reversalReasonError(): string { return this.reversalReason.trim().length > 0 && this.reversalReason.trim().length < 10 ? this.translate('reasonMinimum') : ''; }
  get canReviewAdjustment(): boolean { return !this.amountError && this.reason.trim().length >= 10 && this.reason.trim().length <= 1000 && this.reference.length <= 250; }
  get canConfirmReversal(): boolean { return !this.reversalReasonError && this.reversalReason.trim().length >= 10 && this.reversalReason.trim().length <= 1000 && this.reversalReference.length <= 250; }
  signedAmount(): number { const amount = Number(this.amount ?? 0); return this.direction === 'subtract' ? -amount : amount; }
  projectedTotal(total: string | number): number { return Number(total) + this.signedAmount(); }

  reviewAdjustment(trigger: Event): void {
    if (!this.canReviewAdjustment || this.adjustmentSaving()) return;
    this.adjustmentTrigger = trigger.currentTarget as HTMLElement;
    setTimeout(() => this.adjustmentDialog?.nativeElement.showModal());
  }
  closeAdjustmentDialog(): void { this.adjustmentDialog?.nativeElement.close(); this.adjustmentTrigger?.focus(); }

  adjust(): void {
    const guildId = this.selectedGuildId; const details = this.details();
    if (!guildId || !details || !this.canReviewAdjustment || this.adjustmentSaving()) return;
    const body: AdjustmentRequest = { amount: this.signedAmount(), scope: details.activeSeason ? this.scope : 'LifetimeOnly', reason: this.reason.trim(), reference: this.reference.trim() || undefined, requestId: this.adjustmentRequestId };
    const generation = this.selectionGeneration;
    this.adjustmentSaving.set(true);
    this.api.adjust(guildId, details.userId, body).pipe(finalize(() => this.adjustmentSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        if (generation !== this.selectionGeneration || guildId !== this.selectedGuildId) return;
        this.closeAdjustmentDialog(); this.resetAdjustmentForm(); this.emitChangeAndRefresh(); this.toast.success(this.translate('adjustmentSaved'));
      },
      error: error => { if (generation === this.selectionGeneration) this.toast.error(this.apiErrors.resolve(error, 'errors.xpAdjustmentSave').message); }
    });
  }

  openReversal(entry: XpLedgerEntry, trigger: Event): void {
    if (this.reversalSaving()) return;
    this.reversalEntry.set(entry); this.reversalReason = ''; this.reversalReference = ''; this.reversalRequestId = this.newRequestId();
    this.reversalTrigger = trigger.currentTarget as HTMLElement;
    setTimeout(() => this.reversalDialog?.nativeElement.showModal());
  }
  closeReversalDialog(): void { this.reversalDialog?.nativeElement.close(); this.reversalEntry.set(null); this.reversalTrigger?.focus(); }

  reverse(): void {
    const guildId = this.selectedGuildId; const entry = this.reversalEntry();
    if (!guildId || !entry || !this.canConfirmReversal || this.reversalSaving()) return;
    const generation = this.selectionGeneration;
    this.reversalSaving.set(true);
    this.api.reverse(guildId, entry.id, { reason: this.reversalReason.trim(), reference: this.reversalReference.trim() || undefined, requestId: this.reversalRequestId }).pipe(finalize(() => this.reversalSaving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        if (generation !== this.selectionGeneration || guildId !== this.selectedGuildId) return;
        this.closeReversalDialog(); this.emitChangeAndRefresh(); this.toast.success(this.translate('reversalSaved'));
      },
      error: error => { if (generation === this.selectionGeneration) this.toast.error(this.apiErrors.resolve(error, 'errors.xpAdjustmentReverse').message); }
    });
  }

  formatTotalXp(value: string | number): string { return this.locale.number(value, { maximumFractionDigits: 0 }); }
  formatTimelineXp(value: string | number): string { return this.locale.number(value, { maximumFractionDigits: 2 }); }
  formatRawXp(value: string | number): string { return this.locale.number(value, { maximumFractionDigits: 6 }); }
  formatSignedXp(value: string | number): string { const amount = Number(value); return `${amount > 0 ? '+' : amount < 0 ? '−' : ''}${this.formatRawXp(Math.abs(amount))} XP`; }
  formatDate(value: string | null): string { return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : this.translate('noActivity'); }
  copyDiscordId(userId: string): void { if (!navigator.clipboard) { this.toast.error(this.translate('copyFailed')); return; } void navigator.clipboard.writeText(userId).then(() => this.toast.success(this.translate('discordIdCopied'))).catch(() => this.toast.error(this.translate('copyFailed'))); }
  formatDuration(seconds: number | null): string { if (!seconds) return '—'; const hours = Math.floor(seconds / 3600); const minutes = Math.floor((seconds % 3600) / 60); if (hours) return `${this.translate('durationHours', { count: hours })}${minutes ? ` ${this.translate('durationMinutes', { count: minutes })}` : ''}`; return minutes ? this.translate('durationMinutes', { count: minutes }) : this.translate('durationSeconds', { count: seconds }); }
  formatDay(day: string): string { return this.locale.date(`${day}T00:00:00.000Z`, { dateStyle: 'medium', timeZone: 'UTC' }); }
  formatTimeRange(from: string, to: string): string { return `${this.locale.date(from, { timeStyle: 'short' })}–${this.locale.date(to, { timeStyle: 'short' })}`; }
  kindLabel(kind: XpLedgerKind): string { return this.translate(`kinds.${kind}`); }
  scopeLabel(scope: XpLedgerScope): string { return this.translate(`scopes.${scope}`); }
  sourceLabel(source: string): string { const key = `sources.${source}`; const translated = this.translate(key); return translated === `xpAudit.${key}` ? this.translate('sources.other') : translated; }
  isReversible(entry: XpLedgerEntry, details: XpAuditDetails): boolean { return details.permissions.canAdjust && entry.kind === 'ManualAdjustment' && !entry.reversedByLedgerEntryId; }

  private loadVoiceDay(item: XpTimelineItem, key: string): void {
    const guildId = this.selectedGuildId; const userId = this.subject()?.userId;
    if (!guildId || !userId || !item.dayKey) return;
    const generation = this.voiceGeneration;
    this.setVoiceDayState(key, { expanded: true, loading: true, error: '', items: null });
    this.api.voiceDaySegments(guildId, userId, item.dayKey, this.filters()).pipe(finalize(() => {
      const state = this.voiceDayStates()[key]; if (generation === this.voiceGeneration && state?.loading) this.setVoiceDayState(key, { ...state, loading: false });
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: page => { if (generation === this.voiceGeneration && key === this.voiceDayCacheKey(item)) this.setVoiceDayState(key, { expanded: true, loading: false, error: '', items: page.items }); },
      error: error => { if (generation === this.voiceGeneration && key === this.voiceDayCacheKey(item)) this.setVoiceDayState(key, { expanded: true, loading: false, error: this.resolveLoadError(error, 'errors.xpAuditEntriesLoad'), items: null }); }
    });
  }

  private showAndFocusDialog(generation: number): void {
    setTimeout(() => {
      if (generation !== this.selectionGeneration || !this.subject()) return;
      const dialog = this.memberDialog?.nativeElement;
      if (!dialog?.isConnected) return;
      if (!dialog.open) dialog.showModal();
      this.focusActiveTab();
    });
  }
  private focusActiveTab(): void { setTimeout(() => this.memberDialog?.nativeElement.querySelector<HTMLElement>(`[role="tab"][aria-selected="true"]`)?.focus()); }
  private resolveTrigger(trigger: Event | HTMLElement | null | undefined): HTMLElement | null { return trigger instanceof HTMLElement ? trigger : trigger?.currentTarget instanceof HTMLElement ? trigger.currentTarget : null; }
  private resolveLoadError(error: unknown, fallback: string): string { return error instanceof HttpErrorResponse && error.status === 403 ? this.apiErrors.resolve(error, 'apiErrors.auth.forbidden').message : this.apiErrors.resolve(error, fallback).message; }
  private clearSubject(): void { this.selectionGeneration++; this.entriesGeneration++; this.selectedGuildId = null; this.subject.set(null); this.details.set(null); this.entries.set([]); this.entryCursor.set(null); this.timelineItems.set([]); this.timelineCursor.set(null); this.detailsError.set(''); this.entriesError.set(''); this.invalidateVoiceDays(); }
  private resetAdjustmentForm(): void { this.amount = ''; this.direction = 'add'; this.scope = 'LifetimeOnly'; this.reason = ''; this.reference = ''; this.adjustmentRequestId = this.newRequestId(); }
  private mergeEntries(current: XpLedgerEntry[], added: XpLedgerEntry[]): XpLedgerEntry[] { const ids = new Set(current.map(entry => entry.id)); return [...current, ...added.filter(entry => !ids.has(entry.id))]; }
  private voiceDayCacheKey(item: XpTimelineItem): string { return `${this.selectedGuildId}|${this.subject()?.userId}|${item.dayKey}|${JSON.stringify(this.filters())}`; }
  private setVoiceDayState(key: string, state: VoiceDayState): void { this.voiceDayStates.update(states => { const bounded = { ...states, [key]: state }; const keys = Object.keys(bounded); if (keys.length > 32) delete bounded[keys[0]]; return bounded; }); }
  private invalidateVoiceDays(): void { this.voiceGeneration++; this.voiceDayStates.set({}); }
  private emitChangeAndRefresh(): void { const subject = this.subject(); if (!subject) return; this.xpChanged.emit({ guildId: subject.guildId, userId: subject.userId }); const tab = this.activeModalTab(); this.open(subject, { initialTab: tab === 'adjust' ? 'history' : tab, trigger: this.modalTrigger }); }
  private translate(key: string, params?: Record<string, unknown>): string { return this.i18n.translate(`xpAudit.${key}`, params); }
  private newRequestId(): string { return crypto.randomUUID(); }
}
