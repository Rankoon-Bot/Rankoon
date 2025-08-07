import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { AuthStore } from '../store/auth.store';
import { AppStore, Guild } from '../store/app.store';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class DiscordService {
  private readonly http = inject(HttpClient);
  private readonly authStore = inject(AuthStore);
  private readonly appStore = inject(AppStore);

  /**
   * Fetches user's Discord guilds via backend
   */
  getUserGuilds(): Observable<Guild[]> {
    return this.http.get<Guild[]>(`${environment.apiBaseUrl}/discord/guilds`);
  }

  /**
   * Fetches guild details via backend
   */
  getGuildDetails(guildId: string): Observable<any> {
    return this.http.get<any>(`${environment.apiBaseUrl}/discord/guilds/${guildId}`);
  }

  /**
   * Updates bot configuration for a guild via backend
   */
  updateBotConfig(guildId: string, config: any): Observable<boolean> {
    return this.http.post<boolean>(`${environment.apiBaseUrl}/discord/guilds/${guildId}/config`, config);
  }

  /**
   * Gets bot statistics for a guild via backend
   */
  getBotStats(guildId: string): Observable<any> {
    return this.http.get<any>(`${environment.apiBaseUrl}/discord/guilds/${guildId}/stats`);
  }
}
