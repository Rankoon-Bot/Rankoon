import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AdjustmentRequest, XpAuditDetails, XpAuditEntryPage, XpAuditMemberPage } from '../models/xp-audit.models';

@Injectable({ providedIn: 'root' })
export class XpAuditService {
  private readonly http = inject(HttpClient);
  private base(guildId: string) { return `${environment.apiBaseUrl}/guilds/${guildId}/xp-audit`; }
  members(guildId: string, query = '', includeFormerMembers = false, cursor?: string) { let params = new HttpParams().set('query', query).set('includeFormerMembers', includeFormerMembers); if (cursor) params = params.set('cursor', cursor); return this.http.get<XpAuditMemberPage>(`${this.base(guildId)}/members`, { params }); }
  details(guildId: string, userId: string): Observable<XpAuditDetails> { return this.http.get<XpAuditDetails>(`${this.base(guildId)}/members/${userId}`); }
  entries(guildId: string, userId: string, cursor?: string): Observable<XpAuditEntryPage> { let params = new HttpParams(); if (cursor) params = params.set('cursor', cursor); return this.http.get<XpAuditEntryPage>(`${this.base(guildId)}/members/${userId}/entries`, { params }); }
  adjust(guildId: string, userId: string, body: AdjustmentRequest) { return this.http.post(`${this.base(guildId)}/members/${userId}/adjustments`, body); }
  reverse(guildId: string, entryId: string, body: Pick<AdjustmentRequest, 'reason' | 'reference' | 'requestId'>) { return this.http.post(`${this.base(guildId)}/entries/${entryId}/reverse`, body); }
}
