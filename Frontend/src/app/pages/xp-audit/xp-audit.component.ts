import { CommonModule } from '@angular/common';
import { Component, DestroyRef, ViewChild, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subject, debounceTime, distinctUntilChanged, finalize } from 'rxjs';
import { UserAvatarComponent } from '../../components/user-avatar/user-avatar.component';
import { UserXpModalComponent } from '../../components/user-xp-modal/user-xp-modal.component';
import { LocaleService } from '../../i18n/locale.service';
import { XpAuditMember, XpAuditMemberSort } from '../../models/xp-audit.models';
import { ApiErrorService } from '../../services/api-error.service';
import { XpAuditService } from '../../services/xp-audit.service';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-xp-audit',
  standalone: true,
  imports: [CommonModule, TranslocoPipe, UserAvatarComponent, UserXpModalComponent],
  templateUrl: './xp-audit.component.html',
  styleUrl: './xp-audit.component.scss'
})
export class XpAuditComponent {
  private readonly api = inject(XpAuditService);
  private readonly store = inject(AppStore);
  private readonly locale = inject(LocaleService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly searchInput = new Subject<string>();
  private selectedGuildId: string | null = null;
  private membersGeneration = 0;

  @ViewChild('userXpModal') userXpModal?: UserXpModalComponent;

  readonly query = signal('');
  readonly former = signal(false);
  readonly memberSort = signal<XpAuditMemberSort>('TotalXpDescending');
  readonly members = signal<XpAuditMember[]>([]);
  readonly memberCursor = signal<string | null>(null);
  readonly membersLoading = signal(false);
  readonly moreMembersLoading = signal(false);
  readonly membersError = signal('');

  constructor() {
    this.searchInput.pipe(debounceTime(280), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef)).subscribe(() => this.loadMembers());

    effect(() => {
      const guildId = this.store.selectedGuild()?.id ?? null;
      if (guildId === this.selectedGuildId) return;
      this.selectedGuildId = guildId;
      this.membersGeneration++;
      this.members.set([]);
      this.memberCursor.set(null);
      this.membersError.set('');
      if (guildId) this.loadMembers();
    });
  }

  search(value: string): void {
    this.query.set(value);
    this.memberCursor.set(null);
    this.membersGeneration++;
    this.searchInput.next(value);
  }

  toggleFormer(): void {
    this.former.update(value => !value);
    this.memberCursor.set(null);
    this.loadMembers();
  }

  sortMembers(sort: XpAuditMemberSort): void {
    if (sort === this.memberSort()) return;
    this.memberSort.set(sort);
    this.members.set([]);
    this.memberCursor.set(null);
    this.loadMembers();
  }

  loadMembers(more = false): void {
    const guildId = this.selectedGuildId;
    const cursor = more ? this.memberCursor() : null;
    if (!guildId || (more && (!cursor || this.moreMembersLoading()))) return;
    if (!more) {
      this.moreMembersLoading.set(false);
      this.memberCursor.set(null);
    }
    const generation = ++this.membersGeneration;
    (more ? this.moreMembersLoading : this.membersLoading).set(true);
    this.membersError.set('');
    this.api.members(guildId, this.query(), this.former(), this.memberSort(), cursor ?? undefined).pipe(
      finalize(() => { if (generation === this.membersGeneration) (more ? this.moreMembersLoading : this.membersLoading).set(false); }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: page => {
        if (generation !== this.membersGeneration || guildId !== this.selectedGuildId) return;
        this.members.set(more ? this.mergeMembers(this.members(), page.items) : page.items);
        this.memberCursor.set(page.nextCursor);
      },
      error: error => { if (generation === this.membersGeneration) this.membersError.set(this.apiErrors.resolve(error, 'errors.xpAuditMembersLoad').message); }
    });
  }

  openMember(member: XpAuditMember, trigger: Event): void {
    if (!this.selectedGuildId) return;
    this.userXpModal?.open({ ...member, guildId: this.selectedGuildId }, { initialTab: 'history', trigger });
  }

  onXpChanged(): void {
    this.loadMembers();
  }

  formatTotalXp(value: string | number): string {
    return this.locale.number(value, { maximumFractionDigits: 0 });
  }

  private mergeMembers(current: XpAuditMember[], added: XpAuditMember[]): XpAuditMember[] {
    const ids = new Set(current.map(member => member.userId));
    return [...current, ...added.filter(member => !ids.has(member.userId))];
  }
}
