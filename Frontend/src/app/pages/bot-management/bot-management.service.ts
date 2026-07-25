import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';
import { BotIncident, BotManagementRange, GlobalUsageResponse, GuildHealthResponse, IncidentQuery, IncidentResponse, OperationsOverview } from './bot-management.models';

@Injectable({ providedIn: 'root' })
export class BotManagementService {
  private readonly http = inject(HttpClient);
  getOperationsOverview(range: BotManagementRange) { return this.http.get<OperationsOverview>(`${environment.apiBaseUrl}/bot-management/overview`, { params: { range } }); }
  getGuilds(range: BotManagementRange) { return this.http.get<GuildHealthResponse>(`${environment.apiBaseUrl}/bot-management/guilds`, { params: { range } }); }
  getUsage(range: BotManagementRange) { return this.http.get<GlobalUsageResponse>(`${environment.apiBaseUrl}/bot-management/usage`, { params: { range } }); }
  getIncidents(query: IncidentQuery) {
    const params = Object.fromEntries(Object.entries(query).filter(([, value]) => value !== undefined && value !== '')) as Record<string, string>;
    return this.http.get<IncidentResponse>(`${environment.apiBaseUrl}/bot-management/incidents`, { params });
  }
  getIncident(id: string) { return this.http.get<BotIncident>(`${environment.apiBaseUrl}/bot-management/incidents/${encodeURIComponent(id)}`); }
  acknowledgeIncident(id: string) { return this.http.post<BotIncident>(`${environment.apiBaseUrl}/bot-management/incidents/${encodeURIComponent(id)}/acknowledge`, {}); }
  resolveIncident(id: string) { return this.http.post<BotIncident>(`${environment.apiBaseUrl}/bot-management/incidents/${encodeURIComponent(id)}/resolve`, {}); }
  reopenIncident(id: string) { return this.http.post<BotIncident>(`${environment.apiBaseUrl}/bot-management/incidents/${encodeURIComponent(id)}/reopen`, {}); }
}
