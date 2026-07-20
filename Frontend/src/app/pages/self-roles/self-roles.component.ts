import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize, forkJoin } from 'rxjs';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService, SelfRoleMapping, SelfRolePanel, SelfRoleResources } from '../../services/guild.service';

interface UnicodeEmoji { value: string; name: string; }

const UNICODE_EMOJIS: UnicodeEmoji[] = [
  { value: '✅', name: 'check mark' }, { value: '🎮', name: 'video game' },
  { value: '🎨', name: 'artist palette' }, { value: '🎵', name: 'music' },
  { value: '📚', name: 'books' }, { value: '🏆', name: 'trophy' },
  { value: '⚽', name: 'soccer ball' }, { value: '🎬', name: 'movie camera' },
  { value: '💻', name: 'laptop' }, { value: '🌍', name: 'globe' },
  { value: '🔥', name: 'fire' }, { value: '⭐', name: 'star' },
  { value: '🎯', name: 'target' }, { value: '🛡️', name: 'shield' },
  { value: '🧩', name: 'puzzle piece' }, { value: '🚀', name: 'rocket' },
];

@Component({
  selector: 'app-self-roles',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  templateUrl: './self-roles.component.html',
  styleUrls: ['./self-roles.component.scss'],
})
export class SelfRolesComponent implements OnInit {
  private readonly appStore = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);

  readonly panels = signal<SelfRolePanel[]>([]);
  readonly resources = signal<SelfRoleResources | null>(null);
  readonly editor = signal<SelfRolePanel | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly message = signal('');
  readonly validationErrors = signal<string[]>([]);
  readonly unicodeSearch = signal('');
  readonly customSearch = signal('');
  readonly unicodeEmojis = UNICODE_EMOJIS;

  ngOnInit(): void { this.load(); }

  load(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId) return;
    this.loading.set(true);
    this.error.set('');
    forkJoin({ panels: this.api.selfRolePanels(guildId), resources: this.api.selfRoleResources(guildId) })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ panels, resources }) => { this.panels.set(panels); this.resources.set(resources); },
        error: error => this.error.set(this.apiErrors.resolve(error, 'errors.selfRolesLoad').message),
      });
  }

  textChannels(): SelfRoleResources['channels'] {
    return this.resources()?.channels.filter(channel => {
      const type = channel.type.toLowerCase();
      return type.includes('text') || type.includes('announcement') || type.includes('news');
    }) ?? [];
  }

  channelLabel(channelId: string): string {
    return this.textChannels().find(channel => channel.id === channelId)?.name ?? channelId;
  }

  filteredUnicode(): UnicodeEmoji[] {
    const query = this.unicodeSearch().trim().toLowerCase();
    return query ? UNICODE_EMOJIS.filter(emoji => emoji.name.includes(query) || emoji.value.includes(query)) : UNICODE_EMOJIS;
  }

  filteredCustom(): SelfRoleResources['emojis'] {
    const query = this.customSearch().trim().toLowerCase();
    const emojis = this.resources()?.emojis ?? [];
    return query ? emojis.filter(emoji => emoji.name.toLowerCase().includes(query)) : emojis;
  }

  create(): void {
    this.editor.set({ channelId: '', title: '', description: '', color: '#ef3e3a', enabled: true, mappings: [], revision: 0 });
    this.resetFeedback();
  }

  edit(panel: SelfRolePanel): void {
    this.editor.set(this.copyPanel(panel));
    this.resetFeedback();
  }

  cancel(): void { this.editor.set(null); this.resetFeedback(); }

  addMapping(): void {
    const panel = this.editor();
    const emoji = this.filteredUnicode()[0] ?? UNICODE_EMOJIS[0];
    if (!panel || panel.mappings.length >= 20) return;
    panel.mappings.push({ emoji: { kind: 'Unicode', value: emoji.value, name: emoji.name }, roleId: '' });
  }

  removeMapping(index: number): void { this.editor()?.mappings.splice(index, 1); }

  moveMapping(index: number, direction: -1 | 1): void {
    const mappings = this.editor()?.mappings;
    const target = index + direction;
    if (!mappings || target < 0 || target >= mappings.length) return;
    [mappings[index], mappings[target]] = [mappings[target], mappings[index]];
  }

  chooseUnicode(mapping: SelfRoleMapping, emoji: UnicodeEmoji): void {
    mapping.emoji = { kind: 'Unicode', value: emoji.value, name: emoji.name };
  }

  setUnicode(mapping: SelfRoleMapping, value: string): void {
    mapping.emoji = { kind: 'Unicode', value, name: value };
  }

  chooseCustom(mapping: SelfRoleMapping, emoji: SelfRoleResources['emojis'][number]): void {
    mapping.emoji = { kind: 'Custom', value: emoji.id, name: emoji.name };
  }

  save(): void {
    const panel = this.editor();
    const guildId = this.appStore.selectedGuild()?.id;
    if (!panel || !guildId || !this.validate(panel)) return;
    this.saving.set(true);
    this.error.set('');
    const request = panel.id ? this.api.updateSelfRolePanel(guildId, panel) : this.api.createSelfRolePanel(guildId, panel);
    request.pipe(finalize(() => this.saving.set(false))).subscribe({
      next: saved => {
        this.panels.update(items => panel.id ? items.map(item => item.id === saved.id ? saved : item) : [...items, saved]);
        this.editor.set(this.copyPanel(saved));
        this.message.set(this.i18n.translate('selfRoles.saved'));
      },
      error: error => this.error.set(this.apiErrors.resolve(error, 'errors.save').message),
    });
  }

  remove(panel: SelfRolePanel): void {
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId || !panel.id || !confirm(this.i18n.translate('selfRoles.deleteConfirm'))) return;
    this.error.set('');
    this.api.deleteSelfRolePanel(guildId, panel.id).subscribe({
      next: () => { this.panels.update(items => items.filter(item => item.id !== panel.id)); if (this.editor()?.id === panel.id) this.cancel(); },
      error: error => this.error.set(this.apiErrors.resolve(error, 'errors.selfRoleDelete').message),
    });
  }

  emojiLabel(mapping: SelfRoleMapping): string { return mapping.emoji.kind === 'Custom' ? `:${mapping.emoji.name}:` : mapping.emoji.value; }

  customEmojiUrl(mapping: SelfRoleMapping): string | undefined { return this.resources()?.emojis.find(emoji => emoji.id === mapping.emoji.value)?.url; }

  private validate(panel: SelfRolePanel): boolean {
    const errors: string[] = [];
    if (!panel.channelId) errors.push(this.i18n.translate('selfRoles.validation.channel'));
    if (!panel.title.trim()) errors.push(this.i18n.translate('selfRoles.validation.title'));
    if (!panel.mappings.length) errors.push(this.i18n.translate('selfRoles.validation.mappings'));
    if (new Set(panel.mappings.map(mapping => mapping.roleId).filter(Boolean)).size !== panel.mappings.filter(mapping => mapping.roleId).length) errors.push(this.i18n.translate('selfRoles.validation.roles'));
    if (new Set(panel.mappings.map(mapping => `${mapping.emoji.kind}:${mapping.emoji.value}`)).size !== panel.mappings.length) errors.push(this.i18n.translate('selfRoles.validation.emojis'));
    this.validationErrors.set(errors);
    return errors.length === 0;
  }

  private copyPanel(panel: SelfRolePanel): SelfRolePanel { return { ...panel, mappings: panel.mappings.map(mapping => ({ ...mapping, emoji: { ...mapping.emoji } })) }; }
  private resetFeedback(): void { this.error.set(''); this.message.set(''); this.validationErrors.set([]); }
}
