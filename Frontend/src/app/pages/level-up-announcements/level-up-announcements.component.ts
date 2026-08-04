import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { GuildResources, GuildService } from '../../services/guild.service';
import { LevelAnnouncementGroup, LevelAnnouncementKind, LevelAnnouncementMessage, LevelAnnouncementProfile, LevelProgressScope, LevelUpAnnouncementSettings, TemplateSchema } from '../../models/level-up-announcement.models';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { ToastService } from '../../services/toast.service';
import { DiscordChannelPickerComponent } from '../../shared/ui/discord-channel-picker/discord-channel-picker.component';
import { normalizeDiscordChannels } from '../../shared/ui/discord-channel-picker/discord-channel.models';
import { StickySaveBarComponent } from '../../shared/ui/sticky-save-bar/sticky-save-bar.component';
import { AnnouncementEditorDiagnostic } from './editor/announcement-list-parser';
import { AnnouncementEditorSelection, AnnouncementListEditorComponent } from './editor/announcement-list-editor.component';
import { parseAnnouncementList } from './editor/announcement-list-parser';
import { reconcileAnnouncementGroups } from './editor/announcement-list-reconciler';
import { serializeAnnouncementGroups } from './editor/announcement-list-serializer';

type EditorKey = 'Lifetime:LevelUp' | 'Lifetime:Reward' | 'Season:LevelUp' | 'Season:Reward';
interface EditorState { text: string; groups: LevelAnnouncementGroup[]; diagnostics: AnnouncementEditorDiagnostic[]; }

@Component({
  selector: 'app-level-up-announcements',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe, DiscordChannelPickerComponent, StickySaveBarComponent, AnnouncementListEditorComponent],
  templateUrl: './level-up-announcements.component.html',
  styleUrls: ['./level-up-announcements.component.scss'],
})
export class LevelUpAnnouncementsComponent implements OnInit {
  private readonly api = inject(GuildService);
  private readonly store = inject(AppStore);
  private readonly errors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(TranslocoService);

  readonly settings = signal<LevelUpAnnouncementSettings | null>(null);
  readonly resources = signal<GuildResources>({ roles: [], channels: [] });
  readonly schema = signal<TemplateSchema | null>(null);
  readonly editorStates = signal<Partial<Record<EditorKey, EditorState>>>({});
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly dirty = signal(false);
  readonly scope = signal<LevelProgressScope>('Lifetime');
  readonly kind = signal<LevelAnnouncementKind>('LevelUp');
  readonly selectedGroupId = signal<string | null>(null);
  readonly selectedMessageId = signal<string | null>(null);
  readonly channelOptions = computed(() => normalizeDiscordChannels(this.resources().channels));
  readonly availableTokens = computed(() => (this.schema()?.tokens ?? []).filter(token => (!token.requiresRewardRole || this.kind() === 'Reward') && (!token.requiresSeason || this.scope() === 'Season')));
  private original: LevelUpAnnouncementSettings | null = null;

  ngOnInit(): void { this.load(); }

  get profile(): LevelAnnouncementProfile | undefined {
    const settings = this.settings();
    return this.scope() === 'Lifetime' ? settings?.lifetime : settings?.season;
  }

  get groups(): LevelAnnouncementGroup[] { return this.currentState()?.groups ?? []; }
  get selectedGroup(): LevelAnnouncementGroup | undefined { return this.groups.find(group => group.id === this.selectedGroupId()); }
  get selectedMessage(): LevelAnnouncementMessage | undefined { return this.selectedGroup?.messages.find(message => message.id === this.selectedMessageId()); }
  get currentText(): string { return this.currentState()?.text ?? ''; }
  get currentState(): () => EditorState | undefined { return () => this.editorStates()[this.key()]; }
  get activeGroup(): LevelAnnouncementGroup | undefined { return this.selectedGroup ?? this.groups[0]; }

  load(): void {
    const guildId = this.store.selectedGuild()?.id;
    if (!guildId) return;
    this.loading.set(true);
    this.api.levelUpAnnouncements(guildId).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: value => {
        const settings = this.normalizeSettings(value.settings);
        this.settings.set(settings);
        this.original = structuredClone(settings);
        this.editorStates.set(this.createEditorStates(settings));
        this.selectFirst();
        this.api.resources(guildId).subscribe({ next: resources => this.resources.set(resources) });
        this.api.levelUpTemplateSchema(guildId).subscribe({ next: schema => this.schema.set(schema) });
      },
      error: error => this.error.set(this.errors.resolve(error, 'errors.xpLoad').message),
    });
  }

  selectScope(scope: LevelProgressScope): void { this.scope.set(scope); this.selectFirst(); }
  selectKind(kind: LevelAnnouncementKind): void { this.kind.set(kind); this.selectFirst(); }

  selectFirst(): void {
    const group = this.groups[0];
    this.selectedGroupId.set(group?.id ?? null);
    this.selectedMessageId.set(group?.messages.find(message => message.enabled)?.id ?? group?.messages[0]?.id ?? null);
  }

  onEditorText(text: string): void {
    this.updateState(state => ({ ...state, text }));
    this.dirty.set(true);
  }

  onEditorGroups(groups: LevelAnnouncementGroup[]): void {
    this.updateState(state => ({ ...state, groups }));
    this.updateSettingsGroups(groups);
    this.dirty.set(true);
    if (!this.selectedGroupId() || !groups.some(group => group.id === this.selectedGroupId())) this.selectFirst();
  }

  onDiagnostics(diagnostics: AnnouncementEditorDiagnostic[]): void { this.updateState(state => ({ ...state, diagnostics })); }

  onSelection(selection: AnnouncementEditorSelection): void {
    const group = this.groups[selection.groupIndex];
    if (!group) return;
    const message = selection.messageIndex === null ? group.messages.find(item => item.enabled) ?? group.messages[0] : group.messages[selection.messageIndex];
    this.selectedGroupId.set(group.id);
    this.selectedMessageId.set(message?.id ?? null);
  }

  setSelectedGroup(group: LevelAnnouncementGroup): void {
    this.selectedGroupId.set(group.id);
    this.selectedMessageId.set(group.messages.find(message => message.enabled)?.id ?? group.messages[0]?.id ?? null);
  }

  setChannel(channelId: string | null): void {
    const settings = this.settings();
    if (!settings) return;
    const profileKey = this.scope() === 'Lifetime' ? 'lifetime' : 'season';
    this.settings.set({ ...settings, [profileKey]: { ...settings[profileKey], channelId } });
    this.dirty.set(true);
  }

  changed(): void { this.dirty.set(true); }

  reset(): void {
    if (!this.original) return;
    const restored = this.normalizeSettings(this.original);
    this.settings.set(restored);
    this.editorStates.set(this.createEditorStates(restored));
    this.selectFirst();
    this.dirty.set(false);
  }

  valid(): boolean {
    const settings = this.settings();
    if (!settings) return false;
    const channelsValid = (!settings.lifetime.enabled || !!settings.lifetime.channelId) && (!settings.season.enabled || !!settings.season.channelId);
    const conditionsValid = [settings.lifetime, settings.season]
      .flatMap(profile => [profile.levelUp, profile.rewards])
      .flatMap(set => set.groups)
      .flatMap(group => [group.conditions.minimumLevel, group.conditions.maximumLevel, group.conditions.everyNthLevel])
      .every(value => this.isValidCondition(value));
    return channelsValid && conditionsValid && Object.values(this.editorStates()).every(state => !state?.diagnostics.some(diagnostic => diagnostic.severity === 'error'));
  }

  invalidCondition(value: number | null): boolean { return !this.isValidCondition(value); }

  save(): void {
    const guild = this.store.selectedGuild();
    const settings = this.settings();
    if (!guild || !settings || this.saving() || !this.finalizeEditors() || !this.valid()) return;
    this.saving.set(true);
    this.api.saveLevelUpAnnouncements(guild.id, settings).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: saved => {
        const settings = this.normalizeSettings(saved);
        this.settings.set(settings);
        this.original = structuredClone(settings);
        this.editorStates.set(this.createEditorStates(settings));
        this.dirty.set(false);
        this.toast.success(this.i18n.translate('levelUpAnnouncements.saved'));
      },
      error: error => this.toast.error(this.errors.resolve(error, 'errors.save').message),
    });
  }

  test(): void {
    const guildId = this.store.selectedGuild()?.id;
    const group = this.selectedGroup;
    const message = this.selectedMessage;
    if (!guildId || !group || !message) return;
    this.api.testLevelUpAnnouncement(guildId, { scope: this.scope(), kind: this.kind(), group, message, level: 23, previousLevel: 22, totalXp: 12000, gainedXp: 50, source: 'message', rewardRoleAwarded: this.kind() === 'Reward', seasonName: 'Season' }).subscribe({
      next: () => this.toast.success(this.i18n.translate('levelUpAnnouncements.testSent')),
      error: error => this.toast.error(this.errors.resolve(error, 'errors.save').message),
    });
  }

  probability(group: LevelAnnouncementGroup): number {
    const total = this.groups.filter(item => item.enabled).reduce((sum, item) => sum + item.weight, 0);
    return total ? Math.round(group.weight * 1000 / total) / 10 : 0;
  }

  activeMessages(group: LevelAnnouncementGroup): number { return group.messages.filter(message => message.enabled).length; }

  private key(): EditorKey { return `${this.scope()}:${this.kind()}` as EditorKey; }

  private createEditorStates(settings: LevelUpAnnouncementSettings): Partial<Record<EditorKey, EditorState>> {
    return {
      'Lifetime:LevelUp': this.stateFor(settings.lifetime.levelUp.groups),
      'Lifetime:Reward': this.stateFor(settings.lifetime.rewards.groups),
      'Season:LevelUp': this.stateFor(settings.season.levelUp.groups),
      'Season:Reward': this.stateFor(settings.season.rewards.groups),
    };
  }

  private stateFor(groups: LevelAnnouncementGroup[]): EditorState { return { text: serializeAnnouncementGroups(groups), groups, diagnostics: [] }; }

  private isValidCondition(value: number | null): boolean {
    if (value === null) return true;
    const numericValue = Number(value);
    return Number.isFinite(numericValue) && numericValue >= 1;
  }

  private normalizeSettings(settings: LevelUpAnnouncementSettings): LevelUpAnnouncementSettings {
    const normalized = structuredClone(settings);
    [normalized.lifetime, normalized.season].forEach(profile => {
      [profile.levelUp, profile.rewards].forEach(set => set.groups.forEach(group => {
        group.conditions.minimumLevel = this.normalizeCondition(group.conditions.minimumLevel);
        group.conditions.maximumLevel = this.normalizeCondition(group.conditions.maximumLevel);
        group.conditions.everyNthLevel = this.normalizeCondition(group.conditions.everyNthLevel);
      }));
    });
    return normalized;
  }

  private normalizeCondition(value: unknown): number | null {
    if (value === null || value === undefined || value === '') return null;
    return Number(value);
  }

  private updateState(update: (state: EditorState) => EditorState): void {
    const key = this.key();
    const current = this.editorStates()[key] ?? this.stateFor(this.groups);
    this.editorStates.update(states => ({ ...states, [key]: update(current) }));
  }

  private updateSettingsGroups(groups: LevelAnnouncementGroup[]): void {
    const settings = this.settings();
    if (!settings) return;
    const profileKey = this.scope() === 'Lifetime' ? 'lifetime' : 'season';
    const setKey = this.kind() === 'LevelUp' ? 'levelUp' : 'rewards';
    this.settings.set({ ...settings, [profileKey]: { ...settings[profileKey], [setKey]: { groups } } });
  }

  private finalizeEditors(): boolean {
    const settings = this.settings();
    if (!settings) return false;
    let hasErrors = false;
    const states = { ...this.createEditorStates(settings), ...this.editorStates() };
    (Object.keys(states) as EditorKey[]).forEach(key => {
      const state = states[key];
      if (!state) return;
      const [scope, kind] = key.split(':') as [LevelProgressScope, LevelAnnouncementKind];
      const parsed = parseAnnouncementList(state.text, { availableTokens: this.schema()?.tokens, scope, kind });
      const errors = parsed.diagnostics.filter(diagnostic => diagnostic.severity === 'error');
      states[key] = { ...state, diagnostics: parsed.diagnostics };
      if (errors.length) { hasErrors = true; return; }
      const groups = reconcileAnnouncementGroups(parsed, state.groups, () => crypto.randomUUID().replaceAll('-', ''));
      states[key] = { ...states[key]!, groups };
      const [scopeKey, setKey] = key.split(':') as [LevelProgressScope, 'LevelUp' | 'Reward'];
      const profileKey = scopeKey === 'Lifetime' ? 'lifetime' : 'season';
      const mappedSet = setKey === 'LevelUp' ? 'levelUp' : 'rewards';
      settings[profileKey][mappedSet].groups = groups;
    });
    this.editorStates.set(states);
    this.settings.set({ ...settings });
    return !hasErrors;
  }
}
