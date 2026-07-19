import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { ReportingService } from './reporting.service';

describe('ReportingService', () => {
  let service: ReportingService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
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
});
