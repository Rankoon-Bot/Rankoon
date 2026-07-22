import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, CUSTOM_ELEMENTS_SCHEMA, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize, forkJoin } from 'rxjs';
import 'emoji-picker-element';
import { AppStore } from '../../store/app.store';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService, SelfRoleMapping, SelfRolePanel, SelfRoleResources } from '../../services/guild.service';
import { environment } from '../../../environments/environment';
import { ToastService } from '../../services/toast.service';

type SelfRolePanelWithHealth = SelfRolePanel & { state?: 'Pending' | 'Published' | 'Disabled' | 'Degraded'; lastPublishedAt?: string; lastHealthCheckAt?: string; lastError?: string; lastErrorAt?: string; };

@Component({
  selector: 'app-self-roles',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  templateUrl: './self-roles.component.html',
  styleUrls: ['./self-roles.component.scss'],
})
export class SelfRolesComponent implements OnInit {
  private readonly appStore = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly http = inject(HttpClient);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);

  readonly panels = signal<SelfRolePanelWithHealth[]>([]);
  readonly resources = signal<SelfRoleResources | null>(null);
  readonly editor = signal<SelfRolePanel | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly repairing = signal<string | null>(null);
  readonly confirmation = signal<{ panel: SelfRolePanelWithHealth; action: 'delete' | 'repair' } | null>(null);
  readonly error = signal('');
  readonly validationErrors = signal<string[]>([]);
  readonly pickerIndex = signal<number | null>(null);
  readonly pickerTab = signal<'server' | 'unicode'>('server');
  readonly customSearch = signal('');

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
    // The self-role resource endpoint returns only Discord text channels.
    return this.resources()?.channels ?? [];
  }

  channelLabel(channelId: string): string {
    return this.textChannels().find(channel => channel.id === channelId)?.name ?? channelId;
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
    if (!panel || panel.mappings.length >= 20) return;
    panel.mappings.push({ emoji: { kind: 'Unicode', value: '✅', name: 'check mark' }, roleId: '' });
  }

  removeMapping(index: number): void { this.editor()?.mappings.splice(index, 1); }

  moveMapping(index: number, direction: -1 | 1): void {
    const mappings = this.editor()?.mappings;
    const target = index + direction;
    if (!mappings || target < 0 || target >= mappings.length) return;
    [mappings[index], mappings[target]] = [mappings[target], mappings[index]];
  }

  chooseCustom(mapping: SelfRoleMapping, emoji: SelfRoleResources['emojis'][number]): void {
    if (!emoji.available) return;
    mapping.emoji = { kind: 'Custom', value: emoji.id, name: emoji.name };
  }

  openEmojiPicker(index: number): void {
    this.pickerIndex.set(index);
    this.pickerTab.set((this.resources()?.emojis.length ?? 0) > 0 ? 'server' : 'unicode');
    this.customSearch.set('');
  }

  closeEmojiPicker(): void { this.pickerIndex.set(null); this.customSearch.set(''); }

  pickerMapping(): SelfRoleMapping | null {
    const index = this.pickerIndex();
    return index === null ? null : this.editor()?.mappings[index] ?? null;
  }

  selectUnicode(unicode: string): void {
    const mapping = this.pickerMapping();
    if (mapping) mapping.emoji = { kind: 'Unicode', value: unicode, name: unicode };
    this.closeEmojiPicker();
  }

  selectCustom(emoji: SelfRoleResources['emojis'][number]): void {
    const mapping = this.pickerMapping();
    if (!mapping || !emoji.available) return;
    this.chooseCustom(mapping, emoji);
    this.closeEmojiPicker();
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
        this.toast.success(this.i18n.translate('selfRoles.saved'));
      },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.save').message),
    });
  }

  remove(panel: SelfRolePanel): void {
    if (!panel.id) return;
    this.confirmation.set({ panel, action: 'delete' });
  }

  repair(panel: SelfRolePanelWithHealth): void {
    if (!panel.id || this.repairing()) return;
    this.confirmation.set({ panel, action: 'repair' });
  }

  cancelConfirmation(): void { this.confirmation.set(null); }

  confirmAction(): void {
    const confirmation = this.confirmation();
    if (!confirmation) return;
    this.confirmation.set(null);
    if (confirmation.action === 'repair') {
      this.repairConfirmed(confirmation.panel);
      return;
    }
    const panel = confirmation.panel;
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId || !panel.id) return;
    this.api.deleteSelfRolePanel(guildId, panel.id).subscribe({
      next: () => { this.panels.update(items => items.filter(item => item.id !== panel.id)); if (this.editor()?.id === panel.id) this.cancel(); },
      error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.selfRoleDelete').message),
    });
  }

  private repairConfirmed(panel: SelfRolePanelWithHealth): void {
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId || !panel.id || this.repairing()) return;
    this.repairing.set(panel.id);
    this.http.post<SelfRolePanelWithHealth>(`${environment.apiBaseUrl}/guilds/${guildId}/self-role-panels/${panel.id}/repair`, panel)
      .pipe(finalize(() => this.repairing.set(null)))
      .subscribe({
        next: repaired => {
          this.panels.update(items => items.map(item => item.id === repaired.id ? repaired : item));
          if (this.editor()?.id === repaired.id) this.editor.set(this.copyPanel(repaired));
          this.toast.success(this.i18n.translate('selfRoles.repaired'));
        },
        error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.save').message),
      });
  }

  healthLabel(panel: SelfRolePanelWithHealth): string {
    const state = panel.state ?? panel.status ?? (panel.enabled ? 'Published' : 'Disabled');
    return this.i18n.translate(`selfRoles.health.${state.toLowerCase()}`);
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
  private resetFeedback(): void { this.error.set(''); this.validationErrors.set([]); }
}
