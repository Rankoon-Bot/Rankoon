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

  it('renders loaded rows and appends unique cursor pages', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const first = http.expectOne(request => request.url.endsWith('/guilds/guild-1/xp-audit/members'));
    expect(first.request.params.get('includeFormerMembers')).toBe('false');
    expect(first.request.params.get('sort')).toBe('TotalXpDescending');
    first.flush({ items: [{ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 }], nextCursor: 'next' });

    fixture.componentInstance.loadMembers(true);
    http.expectOne(request => request.params.get('cursor') === 'next').flush({ items: [
      { userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 },
      { userId: '2', displayName: 'Bea', isCurrentMember: false, totalXp: 20, level: 2 }
    ], nextCursor: null });
    fixture.detectChanges();

    expect(fixture.componentInstance.members().map(member => member.userId)).toEqual(['1', '2']);
    expect(fixture.nativeElement.querySelectorAll('.member-item').length).toBe(2);
    expect(fixture.nativeElement.querySelector('.member-item').tagName).toBe('BUTTON');
  });

  it('resets cursor paging when member sorting changes', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: 'old' });

    fixture.componentInstance.sortMembers('NameAscending');
    const sorted = http.expectOne(request => request.params.get('sort') === 'NameAscending');
    expect(sorted.request.params.has('cursor')).toBeFalse();
    sorted.flush({ items: [{ userId: '2', displayName: 'Bea', isCurrentMember: true, totalXp: 20, level: 2 }], nextCursor: null });

    expect(fixture.componentInstance.memberSort()).toBe('NameAscending');
    expect(fixture.componentInstance.members().map(member => member.userId)).toEqual(['2']);
    fixture.componentInstance.sortMembers('NameAscending');
    http.expectNone(request => request.url.endsWith('/members'));
  });

  it('ignores an older member response after a newer load', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const stale = http.expectOne(request => request.url.endsWith('/members'));

    fixture.componentInstance.query.set('bea');
    fixture.componentInstance.loadMembers();
    const current = http.expectOne(request => request.params.get('query') === 'bea');
    current.flush({ items: [{ userId: '2', displayName: 'Bea', isCurrentMember: true, totalXp: 20, level: 2 }], nextCursor: null });
    stale.flush({ items: [{ userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 }], nextCursor: null });

    expect(fixture.componentInstance.members().map(member => member.userId)).toEqual(['2']);
  });

  it('opens the reusable modal and refreshes members when XP changes', () => {
    const fixture = TestBed.createComponent(XpAuditComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
    const member = { userId: '1', displayName: 'Ada', isCurrentMember: true, totalXp: 10, level: 1 };
    spyOn(fixture.componentInstance.userXpModal!, 'open');

    const trigger = new MouseEvent('click');
    fixture.componentInstance.openMember(member, trigger);
    expect(fixture.componentInstance.userXpModal!.open).toHaveBeenCalledWith({ ...member, guildId: 'guild-1' }, { initialTab: 'history', trigger });

    fixture.componentInstance.onXpChanged();
    http.expectOne(request => request.url.endsWith('/members')).flush({ items: [], nextCursor: null });
  });
});
