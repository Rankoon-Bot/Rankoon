import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { testI18n } from '../testing/i18n-testing';
import { DurationFormatterService } from './duration-formatter.service';

describe('DurationFormatterService', () => {
  let formatter: DurationFormatterService;
  let i18n: TranslocoService;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [testI18n] });
    formatter = TestBed.inject(DurationFormatterService);
    i18n = TestBed.inject(TranslocoService);
  });

  it('normalizes invalid, negative, and decimal values to complete seconds', () => {
    i18n.setActiveLang('en');
    expect(formatter.compact(0)).toBe('0 sec');
    expect(formatter.compact(Number.NaN)).toBe('0 sec');
    expect(formatter.compact(Number.POSITIVE_INFINITY)).toBe('0 sec');
    expect(formatter.compact(-10)).toBe('0 sec');
    expect(formatter.compact(59)).toBe('59 sec');
    expect(formatter.compact(59.9)).toBe('59 sec');
  });

  it('formats unit boundaries in English', () => {
    i18n.setActiveLang('en');
    expect(formatter.compact(60)).toBe('1 min');
    expect(formatter.compact(61)).toBe('1 min 1 sec');
    expect(formatter.compact(3600)).toBe('1 hr');
    expect(formatter.compact(86400)).toBe('1 d');
    expect(formatter.compact(604800)).toBe('1 wk');
    expect(formatter.compact(2592000)).toBe('1 mo');
  });

  it('limits compact output and writes every unit in full output', () => {
    i18n.setActiveLang('en');
    const allUnits = 2592000 * 2 + 604800 + 86400 * 3 + 3600 * 4 + 60 * 12 + 8;
    expect(formatter.compact(allUnits)).toBe('2 mo 1 wk');
    expect(formatter.full(allUnits)).toBe('2 months, 1 week, 3 days, 4 hours, 12 minutes, and 8 seconds');
  });

  it('fully localizes German compact and full output', () => {
    i18n.setActiveLang('de');
    expect(formatter.compact(2592000 * 2 + 604800)).toBe('2 Mon. 1 Wo.');
    expect(formatter.full(86400 * 3 + 3600 * 4 + 60 * 12 + 8)).toBe('3 Tage, 4 Stunden, 12 Minuten und 8 Sekunden');
  });
});
