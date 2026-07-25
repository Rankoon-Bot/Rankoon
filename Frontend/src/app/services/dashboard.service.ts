import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { DashboardOverview, DashboardPeriod } from '../models/dashboard.models';

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);
  dashboard(guildId: string, period: DashboardPeriod): Observable<DashboardOverview> {
    return this.http.get<DashboardOverview>(`${environment.apiBaseUrl}/guilds/${guildId}/dashboard`, { params: { period } });
  }
}
