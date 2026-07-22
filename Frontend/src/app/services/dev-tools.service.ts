import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DevelopmentLeaderboardStatus {
  guildId: string;
  mockUserCount: number;
  xpEventCount: string | number;
  totalMockXp: string | number;
  leaderboardAlias: string | null;
}

export interface DevelopmentXpEventResult {
  requested: number;
  granted: number;
  status: DevelopmentLeaderboardStatus;
}

@Injectable({ providedIn: 'root' })
export class DevToolsService {
  private readonly http = inject(HttpClient);
  private url(guildId: string): string { return `${environment.apiBaseUrl}/dev/guilds/${guildId}/leaderboard-mocks`; }
  status(guildId: string): Observable<DevelopmentLeaderboardStatus> { return this.http.get<DevelopmentLeaderboardStatus>(this.url(guildId)); }
  generate(guildId: string, count: number): Observable<DevelopmentLeaderboardStatus> { return this.http.post<DevelopmentLeaderboardStatus>(this.url(guildId), { count }); }
  triggerEvents(guildId: string, count: number, minimumXp: number, maximumXp: number): Observable<DevelopmentXpEventResult> {
    return this.http.post<DevelopmentXpEventResult>(`${this.url(guildId)}/events`, { count, minimumXp, maximumXp });
  }
  remove(guildId: string): Observable<DevelopmentLeaderboardStatus> { return this.http.delete<DevelopmentLeaderboardStatus>(this.url(guildId)); }
}
