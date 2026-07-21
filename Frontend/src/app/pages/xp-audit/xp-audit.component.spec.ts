import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { XpAuditComponent } from './xp-audit.component';

describe('XpAuditComponent', () => {
  const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [XpAuditComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    TestBed.inject(AppStore).setSelectedGuild(guild);
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('loads members for the selected guild and appends unique subsequent pages', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const first = http.expectOne(request => request.url.endsWith('/guilds/guild-1/xp-audit/members'));
    expect(first.request.params.get('includeFormerMembers')).toBe('false');
    first.flush({ items: [{ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 }], nextCursor: 'next' });

    fixture.componentInstance.loadMembers(true);
    http.expectOne(request => request.params.get('cursor') === 'next').flush({ items: [
      { userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 },
      { userId: '2', displayName: 'Bea', isCurrentMember: false, totalXp: 20, level: 2 }
    ], nextCursor: null });

    expect(fixture.componentInstance.members().map(member => member.userId)).toEqual(['1', '2']);
  });

  it('sends only populated history filters and resets entries on a filter change', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    fixture.componentInstance.select({ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 });
    http.expectOne(request => request.url.endsWith('/members/1')).flush({ userId: '1', displayName: 'Ada', isCurrentMember: true, lastXpActivityAtUtc: null, lifetime: { importedXp: 0, earnedXp: 0, manualAdjustment: 0, totalXp: 10, level: 1, rank: 1 }, activeSeason: null, permissions: { canAdjust: false, isSelf: false, isOwner: false } });
    http.expectOne(request => request.url.endsWith('/entries') && request.params.keys().length === 0).flush({ items: [], nextCursor: 'old' });

    fixture.componentInstance.updateFilter('direction', 'Positive');
    const filtered = http.expectOne(request => request.url.endsWith('/entries') && request.params.get('direction') === 'Positive');
    expect(filtered.request.params.has('cursor')).toBeFalse();
    filtered.flush({ items: [], nextCursor: null });
  });

  it('validates the adjustment amount and calculates a negative API amount for deductions', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    const component = fixture.componentInstance;
    component.amount = '0';
    expect(component.amountError).toBeTruthy();
    component.amount = '1.12345';
    expect(component.amountError).toBeTruthy();
    component.amount = '1000000.0001';
    expect(component.amountError).toBeTruthy();
    component.amount = 25.25;
    component.reason = 'Manual adjustment';
    expect(component.amountError).toBe('');
    expect(component.canReviewAdjustment).toBeTrue();
    component.direction = 'subtract';
    expect(component.signedAmount()).toBe(-25.25);
  });
});
