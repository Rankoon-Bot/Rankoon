import { HttpErrorResponse } from '@angular/common/http';
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
});
