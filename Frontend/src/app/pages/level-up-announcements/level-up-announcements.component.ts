import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { GuildResources, GuildService } from '../../services/guild.service';
import { LevelAnnouncementGroup, LevelAnnouncementKind, LevelAnnouncementMessage, LevelAnnouncementProfile, LevelProgressScope, LevelUpAnnouncementSettings, TemplateSchema } from '../../models/level-up-announcement.models';
import { AppStore } from '../../store/app.store'; import { ApiErrorService } from '../../services/api-error.service'; import { ToastService } from '../../services/toast.service';
import { DiscordChannelPickerComponent } from '../../shared/ui/discord-channel-picker/discord-channel-picker.component'; import { normalizeDiscordChannels } from '../../shared/ui/discord-channel-picker/discord-channel.models'; import { StickySaveBarComponent } from '../../shared/ui/sticky-save-bar/sticky-save-bar.component';

@Component({ selector: 'app-level-up-announcements', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe, DiscordChannelPickerComponent, StickySaveBarComponent], templateUrl: './level-up-announcements.component.html', styleUrls: ['./level-up-announcements.component.scss'] })
export class LevelUpAnnouncementsComponent implements OnInit {
  private readonly api = inject(GuildService); private readonly store = inject(AppStore); private readonly errors = inject(ApiErrorService); private readonly toast = inject(ToastService); private readonly i18n = inject(TranslocoService);
  readonly settings = signal<LevelUpAnnouncementSettings | null>(null); readonly resources = signal<GuildResources>({ roles: [], channels: [] }); readonly schema = signal<TemplateSchema | null>(null); readonly loading = signal(true); readonly saving = signal(false); readonly error = signal(''); readonly dirty = signal(false); readonly scope = signal<LevelProgressScope>('Lifetime'); readonly kind = signal<LevelAnnouncementKind>('LevelUp'); readonly selectedGroupId = signal<string | null>(null); readonly selectedMessageId = signal<string | null>(null);
  readonly channelOptions = computed(() => normalizeDiscordChannels(this.resources().channels)); private original = '';
  ngOnInit(): void { this.load(); }
  get profile(): LevelAnnouncementProfile | undefined { const settings = this.settings(); return this.scope() === 'Lifetime' ? settings?.lifetime : settings?.season; }
  get groups(): LevelAnnouncementGroup[] { return this.kind() === 'LevelUp' ? this.profile?.levelUp.groups ?? [] : this.profile?.rewards.groups ?? []; }
  get selectedGroup(): LevelAnnouncementGroup | undefined { return this.groups.find(x => x.id === this.selectedGroupId()); }
  get selectedMessage(): LevelAnnouncementMessage | undefined { return this.selectedGroup?.messages.find(x => x.id === this.selectedMessageId()); }
  load(): void { const id = this.store.selectedGuild()?.id; if (!id) return; this.loading.set(true); this.api.levelUpAnnouncements(id).pipe(finalize(() => this.loading.set(false))).subscribe({ next: value => { this.settings.set(value.settings); this.original = JSON.stringify(value.settings); this.selectFirst(); this.api.resources(id).subscribe({ next: x => this.resources.set(x) }); this.api.levelUpTemplateSchema(id).subscribe({ next: x => this.schema.set(x) }); }, error: e => this.error.set(this.errors.resolve(e, 'errors.xpLoad').message) }); }
  selectScope(scope: LevelProgressScope): void { this.scope.set(scope); this.selectFirst(); }
  selectKind(kind: LevelAnnouncementKind): void { this.kind.set(kind); this.selectFirst(); }
  selectFirst(): void { const group = this.groups[0]; this.selectedGroupId.set(group?.id ?? null); this.selectedMessageId.set(group?.messages[0]?.id ?? null); }
  changed(): void { this.dirty.set(true); }
  reset(): void { this.settings.set(JSON.parse(this.original)); this.selectFirst(); this.dirty.set(false); }
  valid(): boolean { const profile = this.profile; return !!profile && (!profile.enabled || !!profile.channelId); }
  save(): void { const guild = this.store.selectedGuild(); const settings = this.settings(); if (!guild || !settings || !this.valid() || this.saving()) return; this.saving.set(true); this.api.saveLevelUpAnnouncements(guild.id, settings).pipe(finalize(() => this.saving.set(false))).subscribe({ next: saved => { this.settings.set(saved); this.original = JSON.stringify(saved); this.dirty.set(false); this.toast.success(this.i18n.translate('levelUpAnnouncements.saved')); }, error: e => this.toast.error(this.errors.resolve(e, 'errors.save').message) }); }
  addGroup(): void { const group: LevelAnnouncementGroup = { id: this.id(), name: this.i18n.translate('levelUpAnnouncements.newTemplate'), enabled: true, weight: 1, conditions: { minimumLevel: null, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] }, messages: [this.newMessage()] }; this.groups.push(group); this.selectedGroupId.set(group.id); this.selectedMessageId.set(group.messages[0].id); this.changed(); }
  removeGroup(group: LevelAnnouncementGroup): void { const groups = this.kind() === 'LevelUp' ? this.profile!.levelUp.groups : this.profile!.rewards.groups; groups.splice(groups.indexOf(group), 1); this.selectFirst(); this.changed(); }
  addMessage(): void { const group = this.selectedGroup; if (!group) return; const message = this.newMessage(); group.messages.push(message); this.selectedMessageId.set(message.id); this.changed(); }
  removeMessage(message: LevelAnnouncementMessage): void { const group = this.selectedGroup; if (!group || group.messages.length === 1) return; group.messages.splice(group.messages.indexOf(message), 1); this.selectedMessageId.set(group.messages[0]?.id ?? null); this.changed(); }
  probability(group: LevelAnnouncementGroup): number { const total = this.groups.filter(x => x.enabled).reduce((sum, x) => sum + Math.max(1, x.weight), 0); return total ? Math.round(group.weight * 1000 / total) / 10 : 0; }
  availableTokens() { return (this.schema()?.tokens ?? []).filter(x => (!x.requiresRewardRole || this.kind() === 'Reward') && (!x.requiresSeason || this.scope() === 'Season')); }
  insertToken(token: string, input: HTMLTextAreaElement): void { const message = this.selectedMessage; if (!message) return; const start = input.selectionStart ?? message.content.length; message.content = message.content.slice(0, start) + `{${token}}` + message.content.slice(input.selectionEnd ?? start); this.changed(); setTimeout(() => { input.focus(); input.selectionStart = input.selectionEnd = start + token.length + 2; }); }
  test(): void { const id = this.store.selectedGuild()?.id; const message = this.selectedMessage; if (!id || !message) return; this.api.testLevelUpAnnouncement(id, { scope: this.scope(), kind: this.kind(), group: this.selectedGroup, message, level: 23, previousLevel: 22, totalXp: 12000, gainedXp: 50, source: 'message', rewardRoleAwarded: this.kind() === 'Reward', seasonName: 'Season' }).subscribe({ next: () => this.toast.success(this.i18n.translate('levelUpAnnouncements.testSent')), error: e => this.toast.error(this.errors.resolve(e, 'errors.save').message) }); }
  private newMessage(): LevelAnnouncementMessage { return { id: this.id(), enabled: true, content: 'Congratulations {user.mention}! You reached level {level}.' }; }
  private id(): string { return crypto.randomUUID().replaceAll('-', ''); }
}
