import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';
import { BotManagementOverview, BotManagementRange } from './bot-management.models';

@Injectable({ providedIn: 'root' })
export class BotManagementService {
  private readonly http = inject(HttpClient);
  getOverview(range: BotManagementRange) { return this.http.get<BotManagementOverview>(`${environment.apiBaseUrl}/bot-management/overview`, { params: { range } }); }
}
