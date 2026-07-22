import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AppStore } from '../../store/app.store';
import { GuildResources, GuildService, VcHub } from '../../services/guild.service';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiErrorService } from '../../services/api-error.service';
import { finalize, forkJoin } from 'rxjs';
import { ToastService } from '../../services/toast.service';

export function createVoiceHubNameTemplate(localizedSuffix: string): string {
  return `{username}${localizedSuffix}`;
}

@Component({ selector: 'app-vc-hubs', standalone: true, imports: [CommonModule, FormsModule, TranslocoPipe], styleUrls: ['./vc-hubs.component.scss'], template: `
<section class="page"><header><div><p class="eyebrow">{{ 'voiceHubs.eyebrow' | transloco }}</p><h1>{{ 'voiceHubs.title' | transloco }}</h1><p>{{ 'voiceHubs.subtitle' | transloco }}</p></div><button (click)="newHub()">{{ 'voiceHubs.add' | transloco }}</button></header>
<p class="notice error" *ngIf="error()" role="alert"><span>{{ error() }}</span><button type="button" (click)="load()">{{ 'common.retry' | transloco }}</button></p><p class="loading" *ngIf="loading()" role="status">{{ 'voiceHubs.loading' | transloco }}</p><div class="grid"><article class="hub" *ngFor="let hub of hubs()"><div class="hub-title"><h2>{{ hub.nameTemplate }}</h2><label><input type="checkbox" [(ngModel)]="hub.enabled"> {{ 'common.active' | transloco }}</label></div><div class="fields"><label>{{ 'voiceHubs.existingChannel' | transloco }}<select [(ngModel)]="hub.joinChannelId"><option [ngValue]="0">{{ 'voiceHubs.createByBot' | transloco }}</option><option *ngFor="let channel of voiceChannels()" [ngValue]="channel.id">{{ channel.name }}</option></select></label><label *ngIf="!hub.joinChannelId">{{ 'voiceHubs.newName' | transloco }}<input [(ngModel)]="hub.hubChannelName" [placeholder]="'voiceHubs.createPlaceholder' | transloco"></label><label>{{ 'voiceHubs.category' | transloco }}<select [(ngModel)]="hub.categoryId"><option [ngValue]="null">{{ 'voiceHubs.noCategory' | transloco }}</option><option *ngFor="let channel of categories()" [ngValue]="channel.id">{{ channel.name }}</option></select></label><label>{{ 'common.name' | transloco }}<input [(ngModel)]="hub.nameTemplate" [placeholder]="defaultNameTemplate()"></label><label>{{ 'voiceHubs.limit' | transloco }}<input type="number" [(ngModel)]="hub.userLimit"></label><label>{{ 'voiceHubs.bitrate' | transloco }}<input type="number" [(ngModel)]="hub.bitrate"></label><label>{{ 'voiceHubs.maxChannels' | transloco }}<input type="number" min="1" [(ngModel)]="hub.maxChannelsPerOwner"></label></div><footer><span>{{ 'voiceHubs.commands' | transloco }}</span><button (click)="save(hub)">{{ 'common.save' | transloco }}</button><button class="danger" *ngIf="hub.id" (click)="remove(hub)">{{ 'common.delete' | transloco }}</button></footer></article></div></section>` })
export class VcHubsComponent implements OnInit {
  readonly appStore = inject(AppStore); private readonly api = inject(GuildService); private readonly i18n = inject(TranslocoService); private readonly apiErrors = inject(ApiErrorService); private readonly toast = inject(ToastService); readonly hubs = signal<VcHub[]>([]); readonly resources = signal<GuildResources | null>(null); readonly error = signal(''); readonly loading = signal(false);
  voiceChannels = () => this.resources()?.channels.filter(x => x.type.includes('Voice')) ?? []; categories = () => this.resources()?.channels.filter(x => x.type.includes('Category')) ?? [];
  ngOnInit(): void { this.load(); }
  load(): void { const id = this.appStore.selectedGuild()?.id; if (!id) return; this.loading.set(true); this.error.set(''); forkJoin({ hubs: this.api.hubs(id), resources: this.api.resources(id) }).pipe(finalize(() => this.loading.set(false))).subscribe({ next: result => { this.hubs.set(result.hubs); this.resources.set(result.resources); }, error: error => this.error.set(this.apiErrors.resolve(error, 'errors.voiceHubsLoad').message) }); }
  defaultNameTemplate(): string { return createVoiceHubNameTemplate(this.i18n.translate('voiceHubs.nameTemplateSuffix')); }
  newHub(): void { this.hubs.update(items => [...items, { joinChannelId: 0, hubChannelName: this.i18n.translate('voiceHubs.createPlaceholder'), categoryId: null, nameTemplate: this.defaultNameTemplate(), userLimit: 0, bitrate: 64000, maxChannelsPerOwner: 1, enabled: true }]); }
  save(hub: VcHub): void { const id = this.appStore.selectedGuild()?.id; if (!id) return; const request = hub.id ? this.api.updateHub(id, hub) : this.api.createHub(id, hub); request.subscribe({ next: saved => { this.hubs.update(items => items.map(x => x === hub ? saved : x)); this.toast.success(this.i18n.translate('voiceHubs.saved')); }, error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.save').message) }); }
  remove(hub: VcHub): void { const id = this.appStore.selectedGuild()?.id; if (!id || !hub.id) return; this.api.deleteHub(id, hub.id).subscribe({ next: () => this.hubs.update(items => items.filter(x => x.id !== hub.id)), error: error => this.toast.error(this.apiErrors.resolve(error, 'errors.voiceHubDelete').message) }); }
}
