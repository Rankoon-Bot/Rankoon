import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface MaintainerGuild {
  guildId: string;
  name: string;
  iconUrl: string | null;
  memberCount: number;
  enabled: boolean;
  revision: number;
  runtimeAvailable: boolean;
  activeBotIdentity: 'Rankoon' | 'Custom' | null;
}

export interface GuildMaintainerAccessUpdate {
  guildId: string;
  enabled: boolean;
  revision: number;
  updatedAtUtc: string;
}

@Injectable({ providedIn: 'root' })
export class BotMaintainerService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/bot-management/maintainer`;

  guilds(): Observable<MaintainerGuild[]> {
    return this.http.get<MaintainerGuild[]>(`${this.baseUrl}/guilds`);
  }

  setGuildAccess(guildId: string, enabled: boolean, revision: number): Observable<GuildMaintainerAccessUpdate> {
    return this.http.put<GuildMaintainerAccessUpdate>(`${this.baseUrl}/guilds/${guildId}`, { enabled, revision });
  }
}
