import { CdkConnectedOverlay, CdkOverlayOrigin } from '@angular/cdk/overlay';
import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, Input, Output, ViewChild, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { DiscordChannelKind, DiscordChannelOption } from './discord-channel.models';

@Component({
  selector: 'rk-discord-channel-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, CdkOverlayOrigin, CdkConnectedOverlay, TranslocoPipe],
  templateUrl: './discord-channel-picker.component.html',
  styleUrl: './discord-channel-picker.component.scss',
})
export class DiscordChannelPickerComponent {
  private static nextId = 0;
  private readonly channelList = signal<readonly DiscordChannelOption[]>([]);
  private readonly allowedKindList = signal<readonly DiscordChannelKind[]>([]);
  @Input({ required: true }) set channels(value: readonly DiscordChannelOption[]) { this.channelList.set(value); }
  @Input() value: string | null = null;
  @Input({ required: true }) set allowedTypes(value: readonly DiscordChannelKind[]) { this.allowedKindList.set(value); }
  get allowedTypes(): readonly DiscordChannelKind[] { return this.allowedKindList(); }
  @Input() placeholder = '';
  @Input() searchPlaceholder = '';
  @Input() emptyText = '';
  @Input() noResultsText = '';
  @Input() clearable = false;
  @Input() disabled = false;
  @Input() required = false;
  @Input() loading = false;
  @Input() label = '';
  @Input() helpText = '';
  @Input() errorText = '';
  @Input() channelFilter?: (channel: DiscordChannelOption) => boolean;
  @Output() readonly valueChange = new EventEmitter<string | null>();
  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('search') private readonly searchInput?: ElementRef<HTMLInputElement>;

  readonly open = signal(false);
  readonly query = signal('');
  readonly activeType = signal<DiscordChannelKind | 'All'>('All');
  readonly activeIndex = signal(0);
  readonly panelId = `rk-channel-picker-${DiscordChannelPickerComponent.nextId++}`;
  readonly visible = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    const type = this.activeType();
    return this.channelList()
      .filter((channel) => this.allowedKindList().includes(channel.kind))
      .filter((channel) => type === 'All' || channel.kind === type)
      .filter((channel) => !this.channelFilter || this.channelFilter(channel))
      .filter((channel) => !query || `${channel.name} ${channel.categoryName ?? ''} ${this.kindLabel(channel.kind)}`.toLocaleLowerCase().includes(query))
      .sort((a, b) => (a.categoryName ?? '').localeCompare(b.categoryName ?? '') || (a.position ?? Number.MAX_SAFE_INTEGER) - (b.position ?? Number.MAX_SAFE_INTEGER) || a.name.localeCompare(b.name));
  });

  constructor(private readonly i18n: TranslocoService) {}

  selected(): DiscordChannelOption | undefined { return this.channelList().find((channel) => channel.id === this.value); }
  typeLabel(kind: DiscordChannelKind): string { return this.i18n.translate(`channelPicker.types.${kind}`); }
  kindLabel(kind: DiscordChannelKind): string { return this.typeLabel(kind); }
  triggerLabel(): string { const selected = this.selected(); return selected ? `${selected.name}, ${this.typeLabel(selected.kind)}` : this.placeholder; }
  activeOptionId(): string | null { return this.visible()[this.activeIndex()]?.id ?? null; }
  icon(kind: DiscordChannelKind): string { return ({ Text: '#', Voice: '>', Category: '-', Announcement: '!', Forum: 'O', Stage: '*', Thread: '/', Unknown: '?' } as const)[kind]; }
  openPanel(): void { if (this.disabled) return; this.open.set(true); this.activeIndex.set(Math.max(0, this.visible().findIndex((channel) => channel.id === this.value))); setTimeout(() => this.searchInput?.nativeElement.focus()); }
  closePanel(restoreFocus = true): void { this.open.set(false); this.query.set(''); this.activeType.set('All'); if (restoreFocus) setTimeout(() => this.trigger?.nativeElement.focus()); }
  select(channel: DiscordChannelOption): void { if (channel.disabled) return; this.valueChange.emit(channel.id); this.closePanel(); }
  reset(): void { if (!this.disabled && this.value !== null) this.valueChange.emit(null); this.closePanel(); }
  onTriggerKeydown(event: KeyboardEvent): void { if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(event.key)) { event.preventDefault(); this.openPanel(); if (event.key === 'ArrowUp') this.activeIndex.set(Math.max(0, this.visible().length - 1)); } }
  onListKeydown(event: KeyboardEvent): void {
    const items = this.visible();
    if (event.key === 'Escape') { event.preventDefault(); this.closePanel(); return; }
    if (event.key === 'Tab') { this.closePanel(false); return; }
    if (event.key === 'Home' || event.key === 'End') { event.preventDefault(); this.activeIndex.set(event.key === 'Home' ? 0 : Math.max(0, items.length - 1)); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); this.activeIndex.set(Math.max(0, Math.min(items.length - 1, this.activeIndex() + (event.key === 'ArrowDown' ? 1 : -1)))); return; }
    if (event.key === 'Enter' && items[this.activeIndex()]) { event.preventDefault(); this.select(items[this.activeIndex()]); }
  }
}
