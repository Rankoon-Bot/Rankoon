import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { ReportingService } from './reporting.service';
import { testI18n } from '../testing/i18n-testing';

describe('ReportingService', () => {
  let service: ReportingService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [testI18n], providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ReportingService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('requests a guild activity page with only populated query parameters', () => {
    service.activity('guild/1', { search: 'joined', type: 'member', take: 30 }).subscribe();

    const requests = http.match(req => req.url.startsWith(`${environment.apiBaseUrl}/guilds/guild%2F1/reports/activity`));
    expect(requests.length).toBe(2);
    const list = requests.find(request => !request.request.url.endsWith('/summary'))!;
    expect(list.request.method).toBe('GET');
    expect(list.request.params.get('search')).toBe('joined');
    expect(list.request.params.get('name')).toBe('member');
    expect(list.request.params.get('take')).toBe('30');
    expect(list.request.params.has('cursor')).toBeFalse();
    list.flush({ items: [], nextCursor: null });
    requests.find(request => request.request.url.endsWith('/summary'))!.flush({ total: '0', succeeded: '0', failed: '0', averageDurationMs: null, uniqueActors: '0', uniqueCommands: '0', byName: [], byOutcome: [], groups: [], trend: [] });
  });

  it('translates known activity domain IDs while retaining safe stable IDs for filters', () => {
    let result: import('./reporting.models').ActivityReportDto | undefined;
    service.activity('guild-1', { take: 30 }).subscribe(report => result = report);
    const requests = http.match(req => req.url.includes('/reports/activity'));
    requests.find(request => !request.request.url.endsWith('/summary'))!.flush({ items: [{
      id: 'event-1', category: 'activity', name: 'xp.granted', action: 'voice', outcome: 'succeeded', severity: 'info',
      actorId: null, subjectId: null, channelId: null, correlationId: null, durationMs: null, metadata: {}, occurredAt: '2026-07-19T12:00:00Z'
    }], nextCursor: null });
    requests.find(request => request.request.url.endsWith('/summary'))!.flush({ total: '1', succeeded: '1', failed: '0', averageDurationMs: null, uniqueActors: '0', uniqueCommands: '0', byName: [{ key: 'xp.granted', count: '1' }], byOutcome: [], groups: [], trend: [] });

    expect(result?.items[0].title).toBe('XP awarded');
    expect(result?.items[0].description).toBe('Voice activity');
    expect(result?.items[0].outcome).toBe('Succeeded');
    expect(result?.availableTypes).toEqual(['xp.granted']);
  });
});
