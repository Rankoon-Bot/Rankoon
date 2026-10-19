import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface InternalBotFeedChannel {
  id: string;
  name: string;
  botCanSend: boolean;
}

export interface InternalBotFeedSettings {
  supportGuildId: string;
  supportGuildName: string | null;
  supportGuildAvailable: boolean;
  enabled: boolean;
  channelId: string | null;
  dailySummaryEnabled: boolean;
  dailySummaryHourUtc: number;
  dailySummaryMinuteUtc: number;
  revision: number;
  updatedAtUtc: string | null;
  channels: InternalBotFeedChannel[];
}

export interface InternalBotFeedSettingsUpdate {
  enabled: boolean;
  channelId: string | null;
  dailySummaryEnabled: boolean;
  dailySummaryHourUtc: number;
  dailySummaryMinuteUtc: number;
  revision: number;
}

@Injectable({ providedIn: 'root' })
export class InternalBotFeedService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/bot-management/internal-feed/settings`;

  getSettings(): Observable<InternalBotFeedSettings> {
    return this.http.get<InternalBotFeedSettings>(this.baseUrl);
  }

  saveSettings(update: InternalBotFeedSettingsUpdate): Observable<InternalBotFeedSettings> {
    return this.http.put<InternalBotFeedSettings>(this.baseUrl, update);
  }
}
