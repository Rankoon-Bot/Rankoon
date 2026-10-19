import { HttpErrorResponse } from '@angular/common/http';
import { TranslocoService } from '@jsverse/transloco';
import { ToastService } from './toast.service';
import { TestBed } from '@angular/core/testing';
import { testI18n } from '../testing/i18n-testing';
import { ApiErrorService } from './api-error.service';

describe('ApiErrorService', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [testI18n] }));

  it('translates known error keys and structured validation items', () => {
    const result = TestBed.inject(ApiErrorService).resolve(new HttpErrorResponse({ error: {
      errorKey: 'request.validationFailed', message: 'Backend message', errors: { Message: [{ errorKey: 'xp.settings.messagePoints' }] }
    } }));

    expect(result.message).toBe('Please check your input. Message has invalid XP values.');
    expect(result.validation[0].message).toBe('Message has invalid XP values.');
  });

  it('falls back to backend message without legacy error compatibility', () => {
    const service = TestBed.inject(ApiErrorService);
    expect(service.resolve(new HttpErrorResponse({ error: { errorKey: 'unknown', message: 'Backend message' } })).message).toBe('Backend message');
    expect(service.resolve(new HttpErrorResponse({ error: { error: 'Legacy message' } })).message).toBe('Something went wrong.');
  });
  it('maps network and non-JSON gateway failures without exposing response text', () => {
    TestBed.inject(TranslocoService).setTranslation({ apiErrors: { network: { unavailable: 'Check your connection.' }, request: { timeout: 'Check current state before retrying.' }, service: { unavailable: 'Service temporarily unavailable.' } } }, 'en', { merge: true });
    const service = TestBed.inject(ApiErrorService);
    expect(service.resolve(new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') })).message).toBe('Check your connection.');
    expect(service.resolve(new HttpErrorResponse({ status: 504, error: '<html>private proxy details</html>' })).message).toBe('Check current state before retrying.');
    expect(service.resolve(new HttpErrorResponse({ status: 503 })).message).toBe('Service temporarily unavailable.');
  });

  it('ignores malformed validation bodies and retains valid field errors', () => {
    const result = TestBed.inject(ApiErrorService).resolve(new HttpErrorResponse({ error: {
      errorKey: 'request.validationFailed', errors: { Broken: 'not-an-array', Field: [null, 42, 'Helpful detail', { message: 'Another detail' }] }
    } }));
    expect(result.message).toBe('Please check your input. Helpful detail Another detail');
    expect(result.validation.length).toBe(2);
  });

  it('deduplicates interceptor and page notifications for the same response', () => {
    const service = TestBed.inject(ApiErrorService);
    const toast = TestBed.inject(ToastService);
    spyOn(toast, 'error');
    const error = new HttpErrorResponse({ error: { errorKey: 'request.validationFailed' } });
    service.notify(error);
    service.notify(error, 'errors.save');
    expect(toast.error).toHaveBeenCalledOnceWith('Please check your input.');
  });

  it('coalesces identical concurrent errors but allows a later retry to notify again', () => {
    const service = TestBed.inject(ApiErrorService);
    const toast = TestBed.inject(ToastService);
    service.notify(new HttpErrorResponse({ error: { message: 'Failed' } }));
    service.notify(new HttpErrorResponse({ error: { message: 'Failed' } }));
    expect(toast.toasts().length).toBe(1);
    toast.dismiss(toast.toasts()[0].id);
    service.notify(new HttpErrorResponse({ error: { message: 'Failed' } }));
    expect(toast.toasts().length).toBe(1);
    toast.dismiss(toast.toasts()[0].id);
  });
});
