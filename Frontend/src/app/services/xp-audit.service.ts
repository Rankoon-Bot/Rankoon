import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AdjustmentRequest, XpAuditDetails, XpAuditEntryFilter, XpAuditEntryPage, XpAuditMemberPage, XpAuditMemberSort, XpAuditTimelinePage, XpVoiceSegmentPage } from '../models/xp-audit.models';

@Injectable({ providedIn: 'root' })
export class XpAuditService {
  private readonly http = inject(HttpClient);

  private base(guildId: string): string {
    return `${environment.apiBaseUrl}/guilds/${guildId}/xp-audit`;
  }

  members(guildId: string, query = '', includeFormerMembers = false, sort: XpAuditMemberSort = 'TotalXpDescending', cursor?: string): Observable<XpAuditMemberPage> {
    let params = new HttpParams().set('query', query).set('includeFormerMembers', includeFormerMembers).set('sort', sort);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<XpAuditMemberPage>(`${this.base(guildId)}/members`, { params });
  }

  details(guildId: string, userId: string): Observable<XpAuditDetails> {
    return this.http.get<XpAuditDetails>(`${this.base(guildId)}/members/${userId}`);
  }

  entries(guildId: string, userId: string, filter: XpAuditEntryFilter = {}): Observable<XpAuditEntryPage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(filter)) {
      if (value !== null && value !== undefined && value !== '') params = params.set(key, value);
    }
    return this.http.get<XpAuditEntryPage>(`${this.base(guildId)}/members/${userId}/entries`, { params });
  }
  timeline(guildId: string, userId: string, filter: XpAuditEntryFilter = {}): Observable<XpAuditTimelinePage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(filter)) if (value !== null && value !== undefined && value !== '') params = params.set(key, value);
    return this.http.get<XpAuditTimelinePage>(`${this.base(guildId)}/members/${userId}/timeline`, { params });
  }

  voiceDaySegments(guildId: string, userId: string, dayKey: string, filter: XpAuditEntryFilter = {}): Observable<XpVoiceSegmentPage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(filter)) if (key !== 'cursor' && value !== null && value !== undefined && value !== '') params = params.set(key, value);
    return this.http.get<XpVoiceSegmentPage>(`${this.base(guildId)}/members/${userId}/voice-days/${encodeURIComponent(dayKey)}/segments`, { params });
  }

  adjust(guildId: string, userId: string, body: AdjustmentRequest): Observable<unknown> {
    return this.http.post(`${this.base(guildId)}/members/${userId}/adjustments`, body);
  }

  reverse(guildId: string, entryId: string, body: Pick<AdjustmentRequest, 'reason' | 'reference' | 'requestId'>): Observable<unknown> {
    return this.http.post(`${this.base(guildId)}/entries/${entryId}/reverse`, body);
  }
}
