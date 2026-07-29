import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { GuildAnalyticsService } from './guild-analytics.service';

describe('GuildAnalyticsService', () => {
  let service: GuildAnalyticsService; let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }); service = TestBed.inject(GuildAnalyticsService); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());

  it('uses one purpose-built request with the selected range', () => {
    service.xp('guild/1', '30d').subscribe();
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild%2F1/analytics/xp?range=30d`);
    expect(request.request.method).toBe('GET');
    expect(http.match(() => true).length).toBe(0);
    request.flush({ generatedAt: '', period: {}, kpis: [], trend: [], breakdown: [], insights: [] });
  });

  it('adds a cursor only for an audit continuation request', () => {
    service.audit('guild-1', '7d', 'next value').subscribe();
    const request = http.expectOne(req => req.url.endsWith('/guilds/guild-1/analytics/audit'));
    expect(request.request.params.get('range')).toBe('7d');
    expect(request.request.params.get('cursor')).toBe('next value');
    request.flush({ generatedAt: '', period: {}, kpis: [], trend: [], breakdown: [], insights: [], items: [], nextCursor: null });
  });

  it('requests timeline buckets and custom bounds', () => {
    service.timeline('guild-1', { range: 'custom', bucket: 'week', from: '2026-07-01T00:00:00Z', to: '2026-07-29T00:00:00Z' }).subscribe();
    const request = http.expectOne(req => req.url.endsWith('/guilds/guild-1/analytics/timeline'));
    expect(request.request.params.get('range')).toBe('custom');
    expect(request.request.params.get('bucket')).toBe('week');
    expect(request.request.params.get('from')).toBe('2026-07-01T00:00:00Z');
    expect(request.request.params.get('to')).toBe('2026-07-29T00:00:00Z');
    request.flush({ rangeStart: '', rangeEnd: '', timeZone: 'UTC', bucketSize: 'week', generatedAt: '', summary: {}, buckets: [], previousBuckets: [] });
  });
});
