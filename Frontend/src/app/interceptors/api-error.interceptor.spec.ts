import { HttpClient, HttpContext, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ApiErrorService } from '../services/api-error.service';
import { AuthService } from '../services/auth.service';
import { apiErrorInterceptor } from './api-error.interceptor';
import { authInterceptor } from './auth.interceptor';
import { SKIP_API_ERROR_TOAST, SUPPRESSED_API_ERROR_KEYS } from './api-error.context';

describe('API error notifications through auth recovery', () => {
  let http: HttpClient;
  let requests: HttpTestingController;
  let errors: jasmine.SpyObj<ApiErrorService>;
  let auth: jasmine.SpyObj<AuthService>;
  beforeEach(() => {
    errors = jasmine.createSpyObj('ApiErrorService', ['notify']);
    auth = jasmine.createSpyObj('AuthService', ['getCsrfToken', 'renewCsrfToken', 'refreshToken']);
    auth.getCsrfToken.and.returnValue(of('csrf'));
    auth.renewCsrfToken.and.returnValue(of('renewed'));
    auth.refreshToken.and.returnValue(of(true));
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([apiErrorInterceptor, authInterceptor])), provideHttpClientTesting(),
      { provide: ApiErrorService, useValue: errors }, { provide: AuthService, useValue: auth }
    ] });
    http = TestBed.inject(HttpClient);
    requests = TestBed.inject(HttpTestingController);
  });
  afterEach(() => requests.verify());

  it('notifies once for a wrapped Discord failure, preserves the error and never retries the delete', () => {
    let received: HttpErrorResponse | undefined;
    http.delete('/api/guilds/1/vc-hubs/hub').subscribe({ error: error => received = error });
    requests.expectOne('/api/guilds/1/vc-hubs/hub').flush({ errorKey: 'discord.missingAccess' }, { status: 422, statusText: 'Unprocessable Entity' });
    expect(received?.error.errorKey).toBe('discord.missingAccess');
    expect(errors.notify).toHaveBeenCalledOnceWith(received!);
    expect(auth.renewCsrfToken).not.toHaveBeenCalled();
    expect(auth.refreshToken).not.toHaveBeenCalled();
  });

  for (const status of [401, 403]) {
    it('keeps a recovered ' + status + ' attempt quiet', () => {
      http.post('/api/settings', {}).subscribe();
      requests.expectOne('/api/settings').flush({}, { status, statusText: 'Auth' });
      requests.expectOne('/api/settings').flush({ ok: true });
      expect(errors.notify).not.toHaveBeenCalled();
    });
  }

  it('notifies only the final failure after recovery is exhausted', () => {
    http.get('/api/settings').subscribe({ error: () => {} });
    requests.expectOne('/api/settings').flush({}, { status: 401, statusText: 'Unauthorized' });
    requests.expectOne('/api/settings').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(errors.notify).toHaveBeenCalledTimes(1);
  });

  it('suppresses only the expected key and still reports real failures on the same request', () => {
    const context = new HttpContext().set(SUPPRESSED_API_ERROR_KEYS, ['resource.notFound']);
    for (const key of ['resource.notFound', 'server.internal']) {
      http.get('/api/diagnostics', { context }).subscribe({ error: () => {} });
      requests.expectOne('/api/diagnostics').flush({ errorKey: key }, { status: 500, statusText: 'Failure' });
    }
    expect(errors.notify).toHaveBeenCalledTimes(1);
  });

  it('leaves assets and explicit opt-outs quiet', () => {
    http.get('/assets/i18n/en.json').subscribe({ error: () => {} });
    requests.expectOne('/assets/i18n/en.json').flush('', { status: 404, statusText: 'Not Found' });
    http.get('/api/background', { context: new HttpContext().set(SKIP_API_ERROR_TOAST, true) }).subscribe({ error: () => {} });
    requests.expectOne('/api/background').flush({}, { status: 500, statusText: 'Failure' });
    expect(errors.notify).not.toHaveBeenCalled();
  });

  it('keeps an expected anonymous session check quiet but reports server failure', () => {
    for (const status of [401, 500]) {
      http.get('/api/auth/me').subscribe({ error: () => {} });
      requests.expectOne('/api/auth/me').flush({}, { status, statusText: 'Failure' });
    }
    expect(errors.notify).toHaveBeenCalledTimes(1);
  });
});
