import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { InternalBotFeedService, InternalBotFeedSettings } from '../../services/internal-bot-feed.service';
import { ToastService } from '../../services/toast.service';

interface FeedDraft {
  enabled: boolean;
  channelId: string;
  dailySummaryEnabled: boolean;
  dailySummaryTime: string;
}

@Component({
  selector: 'app-internal-feed-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  template: `
    <section class="feed-page" [attr.aria-busy]="loading() || saving()">
      <header class="page-heading">
        <div>
          <p class="eyebrow">{{ 'botManagement.feed.eyebrow' | transloco }}</p>
          <h2>{{ 'botManagement.feed.title' | transloco }}</h2>
          <p class="description">{{ 'botManagement.feed.description' | transloco }}</p>
        </div>
        <button class="secondary-button" type="button" (click)="load()" [disabled]="loading() || saving()">
          {{ (hasChanges() ? 'botManagement.feed.reloadDiscard' : 'botManagement.feed.reload') | transloco }}
        </button>
      </header>

      <aside class="privacy-note" role="note">
        <span class="note-icon" aria-hidden="true">i</span>
        <p>{{ 'botManagement.feed.privacy' | transloco }}</p>
      </aside>

      <p *ngIf="error()" class="error-message" role="alert">{{ error() }}</p>
      <p *ngIf="savedNotice()" class="success-message" role="status">{{ 'botManagement.feed.saved' | transloco }}</p>

      <div *ngIf="loading() && !settings()" class="loading-state" role="status">
        <span class="spinner" aria-hidden="true"></span>
        <span>{{ 'botManagement.feed.loading' | transloco }}</span>
      </div>

      <ng-container *ngIf="settings() && draft">
        <aside *ngIf="!settings()!.supportGuildAvailable" class="warning-note" role="status">
          {{ 'botManagement.feed.supportUnavailable' | transloco }}
        </aside>

        <form class="settings-panel" (ngSubmit)="save()">
          <div class="panel-heading">
            <div>
              <h3>{{ 'botManagement.feed.destinationTitle' | transloco }}</h3>
              <p>{{ 'botManagement.feed.destinationDescription' | transloco }}</p>
            </div>
            <span class="state-badge" [class.active]="draft.enabled || draft.dailySummaryEnabled">
              {{ ((draft.enabled || draft.dailySummaryEnabled) ? 'botManagement.feed.stateEnabled' : 'botManagement.feed.stateDisabled') | transloco }}
            </span>
          </div>

          <label class="switch-row">
            <input type="checkbox" name="enabled" role="switch" [(ngModel)]="draft.enabled" [disabled]="saving()">
            <span class="switch-copy">
              <strong>{{ 'botManagement.feed.enabledLabel' | transloco }}</strong>
              <span>{{ 'botManagement.feed.enabledDescription' | transloco }}</span>
            </span>
          </label>

          <label class="field">
            <span>{{ 'botManagement.feed.channelLabel' | transloco }}</span>
            <select name="channelId" [(ngModel)]="draft.channelId" [disabled]="saving() || !settings()!.supportGuildAvailable">
              <option value="">{{ 'botManagement.feed.channelPlaceholder' | transloco }}</option>
              <option *ngFor="let channel of settings()!.channels" [value]="channel.id" [disabled]="!channel.botCanSend">
                #{{ channel.name }}{{ channel.botCanSend ? '' : ' · ' + ('botManagement.feed.channelNoPermission' | transloco) }}
              </option>
              <option *ngIf="channelMissing()" [value]="draft.channelId" disabled>
                {{ 'botManagement.feed.channelMissing' | transloco }} ({{ draft.channelId }})
              </option>
            </select>
            <small>{{ 'botManagement.feed.channelHint' | transloco: { guild: settings()!.supportGuildName ?? 'Rankoon' } }}</small>
          </label>

          <div class="subsection">
            <h3>{{ 'botManagement.feed.dailyTitle' | transloco }}</h3>
            <label class="switch-row">
              <input type="checkbox" name="dailySummaryEnabled" role="switch" [(ngModel)]="draft.dailySummaryEnabled" [disabled]="saving()">
              <span class="switch-copy">
                <strong>{{ 'botManagement.feed.dailyEnabledLabel' | transloco }}</strong>
                <span>{{ 'botManagement.feed.dailyEnabledDescription' | transloco }}</span>
              </span>
            </label>
            <label class="field time-field">
              <span>{{ 'botManagement.feed.dailyTimeLabel' | transloco }}</span>
              <input type="time" name="dailySummaryTime" step="60" required [(ngModel)]="draft.dailySummaryTime" [disabled]="saving() || !draft.dailySummaryEnabled">
              <small>{{ 'botManagement.feed.dailyTimeHint' | transloco }}</small>
            </label>
          </div>

          <div *ngIf="hasChanges()" class="draft-note" role="status">{{ 'botManagement.feed.unsaved' | transloco }}</div>
          <footer class="form-actions">
            <span class="updated-at" *ngIf="settings()!.updatedAtUtc">
              {{ 'botManagement.feed.lastSaved' | transloco }} {{ settings()!.updatedAtUtc | date:'medium' }}
            </span>
            <button class="primary-button" type="submit" [disabled]="saving() || loading() || !hasChanges() || !validDraft()">
              {{ (saving() ? 'botManagement.feed.saving' : 'botManagement.feed.save') | transloco }}
            </button>
          </footer>
        </form>
      </ng-container>
    </section>
  `,
  styleUrl: './internal-feed-settings.component.scss',
})
export class InternalFeedSettingsComponent implements OnInit {
  private readonly api = inject(InternalBotFeedService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(TranslocoService);

  readonly settings = signal<InternalBotFeedSettings | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly savedNotice = signal(false);
  draft: FeedDraft | null = null;
  private savedDraft: FeedDraft | null = null;

  ngOnInit(): void { this.load(); }

  hasChanges(): boolean {
    return this.draft != null && this.savedDraft != null && (
      this.draft.enabled !== this.savedDraft.enabled ||
      this.draft.channelId !== this.savedDraft.channelId ||
      this.draft.dailySummaryEnabled !== this.savedDraft.dailySummaryEnabled ||
      this.draft.dailySummaryTime !== this.savedDraft.dailySummaryTime
    );
  }

  channelMissing(): boolean {
    const channelId = this.draft?.channelId;
    return !!channelId && !this.settings()?.channels.some(channel => channel.id === channelId);
  }

  validDraft(): boolean {
    if (!this.draft) return false;
    if ((this.draft.enabled || this.draft.dailySummaryEnabled) && (!this.draft.channelId || !this.settings()?.channels.some(channel => channel.id === this.draft!.channelId && channel.botCanSend))) return false;
    return /^([01]\d|2[0-3]):[0-5]\d$/.test(this.draft.dailySummaryTime);
  }

  load(): void {
    this.loading.set(true);
    this.error.set('');
    this.savedNotice.set(false);
    this.api.getSettings().pipe(finalize(() => this.loading.set(false))).subscribe({
      next: settings => {
        this.settings.set(settings);
        this.draft = this.toDraft(settings);
        this.savedDraft = { ...this.draft };
      },
      error: error => this.error.set(this.apiErrors.resolve(error, 'botManagement.feed.loadFailed').message),
    });
  }

  save(): void {
    const settings = this.settings();
    const draft = this.draft;
    if (!settings || !draft || this.saving() || !this.hasChanges() || !this.validDraft()) return;
    const [hour, minute] = draft.dailySummaryTime.split(':').map(Number);
    this.saving.set(true);
    this.error.set('');
    this.savedNotice.set(false);
    this.api.saveSettings({
      enabled: draft.enabled,
      channelId: draft.channelId || null,
      dailySummaryEnabled: draft.dailySummaryEnabled,
      dailySummaryHourUtc: hour,
      dailySummaryMinuteUtc: minute,
      revision: settings.revision,
    }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: saved => {
        this.settings.set(saved);
        this.draft = this.toDraft(saved);
        this.savedDraft = { ...this.draft };
        this.savedNotice.set(true);
        this.toast.success(this.i18n.translate('botManagement.feed.saved'));
      },
      error: error => this.error.set(this.apiErrors.resolve(error, 'botManagement.feed.saveFailed').message),
    });
  }

  private toDraft(settings: InternalBotFeedSettings): FeedDraft {
    return {
      enabled: settings.enabled,
      channelId: settings.channelId ?? '',
      dailySummaryEnabled: settings.dailySummaryEnabled,
      dailySummaryTime: `${String(settings.dailySummaryHourUtc).padStart(2, '0')}:${String(settings.dailySummaryMinuteUtc).padStart(2, '0')}`,
    };
  }
}
