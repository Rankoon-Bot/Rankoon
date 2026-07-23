import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { CustomBotAccess } from './guild.service';
import { GuildService } from './guild.service';
import { CustomBotIdentityAccessService } from './custom-bot-identity-access.service';

describe('CustomBotIdentityAccessService', () => {
  const requests = new Map<string, Subject<CustomBotAccess>>();
  let service: CustomBotIdentityAccessService;

  beforeEach(() => {
    requests.clear();
    TestBed.configureTestingModule({ providers: [{ provide: GuildService, useValue: { customBotAccess: (guildId: string) => { const request = new Subject<CustomBotAccess>(); requests.set(guildId, request); return request; } } }] });
    service = TestBed.inject(CustomBotIdentityAccessService);
  });

  it('hides disabled and disallowed access', () => {
    service.load('1'); requests.get('1')!.next(access('FeatureDisabled'));
    expect(service.visible()).toBeFalse();
    service.load('1'); requests.get('1')!.next(access('GuildNotAllowed'));
    expect(service.visible()).toBeFalse();
  });

  it('keeps configured identities visible at capacity', () => {
    service.load('1'); requests.get('1')!.next({ ...access('CapacityReached'), isEligible: true, hasConfiguredIdentity: true });
    expect(service.visible()).toBeTrue();
  });

  it('ignores a stale guild response', () => {
    service.load('1'); const first = requests.get('1')!;
    service.load('2'); requests.get('2')!.next(access('GuildNotAllowed'));
    first.next({ ...access('Available'), isEligible: true, canActivate: true });
    expect(service.visible()).toBeFalse();
  });

  function access(reason: CustomBotAccess['reason']): CustomBotAccess {
    return { isEligible: false, canActivate: false, hasReservation: false, hasConfiguredIdentity: false, activeGuilds: 0, maximumActiveGuilds: 1, reason };
  }
});
