import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AppStore } from '../../store/app.store';
import { GuildResources, GuildService, VcHub } from '../../services/guild.service';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiErrorService } from '../../services/api-error.service';
import { finalize, forkJoin } from 'rxjs';
import { ToastService } from '../../services/toast.service';
import { DiscordChannelPickerComponent } from '../../shared/ui/discord-channel-picker/discord-channel-picker.component';
import { normalizeDiscordChannels } from '../../shared/ui/discord-channel-picker/discord-channel.models';
import { StickySaveBarComponent } from '../../shared/ui/sticky-save-bar/sticky-save-bar.component';

export function createVoiceHubNameTemplate(localizedSuffix: string): string {
  return `{username}${localizedSuffix}`;
}

@Component({
  selector: 'app-vc-hubs',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe, DiscordChannelPickerComponent, StickySaveBarComponent],
  templateUrl: './vc-hubs.component.html',
  styleUrls: ['./vc-hubs.component.scss'],
})
export class VcHubsComponent implements OnInit {
  readonly appStore = inject(AppStore);
  private readonly api = inject(GuildService);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);
  readonly hubs = signal<VcHub[]>([]);
  readonly resources = signal<GuildResources | null>(null);
  readonly editor = signal<VcHub | null>(null);
  readonly error = signal('');
  readonly loading = signal(false);
  readonly saving = signal(false);
  private baseline: VcHub | null = null;
  private editorIndex = -1;

  channelOptions = () => normalizeDiscordChannels(this.resources()?.channels ?? []);
  ngOnInit(): void { this.load(); }
  load(): void {
    const id = this.appStore.selectedGuild()?.id;
    if (!id) return;
    this.loading.set(true); this.error.set('');
    forkJoin({ hubs: this.api.hubs(id), resources: this.api.resources(id) }).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: result => { this.hubs.set(result.hubs); this.resources.set(result.resources); this.editor.set(null); this.baseline = null; this.editorIndex = -1; },
      error: error => this.error.set(this.apiErrors.resolve(error, 'errors.voiceHubsLoad').message),
    });
  }
  defaultNameTemplate(): string { return createVoiceHubNameTemplate(this.i18n.translate('voiceHubs.nameTemplateSuffix')); }
  newHub(): void {
    const hub: VcHub = { joinChannelId: '0', hubChannelName: this.i18n.translate('voiceHubs.createPlaceholder'), categoryId: null, nameTemplate: this.defaultNameTemplate(), userLimit: 0, bitrate: 64000, maxChannelsPerOwner: 1, enabled: true };
    this.hubs.update(items => [...items, hub]); this.editorIndex = this.hubs().length - 1; this.editor.set(structuredClone(hub)); this.baseline = null;
  }
  edit(hub: VcHub): void { this.editorIndex = this.hubs().indexOf(hub); this.editor.set(structuredClone(hub)); this.baseline = structuredClone(hub); }
  setHubMode(mode: 'create' | 'existing'): void { const hub = this.editor(); if (hub) hub.joinChannelId = mode === 'create' ? '0' : this.channelOptions().find(channel => channel.kind === 'Voice')?.id ?? '0'; }
  isExistingHub(hub: VcHub): boolean { return String(hub.joinChannelId) !== '0'; }
  dirty(): boolean { return !!this.editor() && (this.baseline === null || JSON.stringify(this.editor()) !== JSON.stringify(this.baseline)); }
  valid(): boolean { const hub = this.editor(); return !!hub && !!hub.nameTemplate.trim() && hub.maxChannelsPerOwner >= 1; }
  reset(): void {
    if (!this.editor()) return;
    if (this.baseline === null) { this.hubs.update(items => items.filter((_, index) => index !== this.editorIndex)); this.editor.set(null); this.editorIndex = -1; return; }
    const restored = structuredClone(this.baseline); this.hubs.update(items => items.map((item, index) => index === this.editorIndex ? restored : item)); this.editor.set(structuredClone(restored));
  }
  save(hub = this.editor()): void {
    const id = this.appStore.selectedGuild()?.id;
    if (!id || !hub || this.saving() || !this.valid()) return;
    const index = hub === this.editor() ? this.editorIndex : this.hubs().indexOf(hub);
    this.saving.set(true);
    const request = hub.id ? this.api.updateHub(id, hub) : this.api.createHub(id, hub);
    request.pipe(finalize(() => this.saving.set(false))).subscribe({ next: saved => { this.hubs.update(items => items.map((item, itemIndex) => itemIndex === index || (!!hub.id && item.id === hub.id) ? saved : item)); this.edit(saved); this.toast.success(this.i18n.translate('voiceHubs.saved')); }, error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.save').message) });
  }
  remove(hub: VcHub): void {
    const id = this.appStore.selectedGuild()?.id;
    if (!id || !hub.id) return;
    this.api.deleteHub(id, hub.id).subscribe({ next: () => { this.hubs.update(items => items.filter(x => x.id !== hub.id)); if (this.editor()?.id === hub.id) { this.editor.set(null); this.baseline = null; this.editorIndex = -1; } }, error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.voiceHubDelete').message) });
  }
}
