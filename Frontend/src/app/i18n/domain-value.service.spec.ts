import { TestBed } from '@angular/core/testing';
import { testI18n } from '../testing/i18n-testing';
import { DomainValueService } from './domain-value.service';

describe('DomainValueService', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [testI18n] }));

  it('translates known watchdog states and safely humanizes unknown values', () => {
    const service = TestBed.inject(DomainValueService);
    expect(service.watchdogState('Healthy')).toBe('Healthy');
    expect(service.watchdogState(2)).toBe('Degraded');
    expect(service.watchdogState('future_state')).toBe('Future State');
  });
});
