import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { BotManagementService } from './bot-management.service';

describe('BotManagementService', () => {
  let service: BotManagementService; let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }); service = TestBed.inject(BotManagementService); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());

  it('sends incident filters to the global incidents route', () => {
    service.getIncidents({ range: '24h', status: 'open', severity: 'critical', search: 'gateway' }).subscribe();
    const request = http.expectOne(req => req.url === `${environment.apiBaseUrl}/bot-management/incidents`);
    expect(request.request.params.get('range')).toBe('24h'); expect(request.request.params.get('status')).toBe('open'); expect(request.request.params.get('severity')).toBe('critical');
    request.flush({ generatedAt: '', summary: [], items: [], nextCursor: null });
  });

  for (const action of ['acknowledge', 'resolve', 'reopen'] as const) {
    it(`posts the ${action} operator action`, () => {
      const call = action === 'acknowledge' ? service.acknowledgeIncident('incident/1') : action === 'resolve' ? service.resolveIncident('incident/1') : service.reopenIncident('incident/1');
      call.subscribe(); const request = http.expectOne(`${environment.apiBaseUrl}/bot-management/incidents/incident%2F1/${action}`); expect(request.request.method).toBe('POST'); request.flush({});
    });
  }
});
