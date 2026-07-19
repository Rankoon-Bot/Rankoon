import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { firstValueFrom, isObservable, Observable, of, throwError } from 'rxjs';
import { GuildCapabilities } from '../models/guild-permissions.models';
import { GuildAccessService } from '../services/guild-access.service';
import { AppStore, Guild } from '../store/app.store';
import { moduleGuard, ownerGuard } from './auth.guard';

describe('permission guards', () => {
  const guild: Guild = {
    id: 'guild-1', name: 'Guild One', icon: null, owner: false, permissions: '0',
    features: [], botInstalled: true, inviteUrl: ''
  };
  const capabilities: GuildCapabilities = {
    guildId: 'guild-1', isOwner: false, canAccessSettings: true,
    moduleIds: ['xp'], leaderboardAlias: 'guild-one'
  };

  let store: AppStore;
  let router: Router;
  let loaded: Observable<GuildCapabilities>;

  beforeEach(() => {
    sessionStorage.clear();
    loaded = of(capabilities);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: GuildAccessService,
          useValue: {
            loadCapabilities: () => loaded,
            destination: (value: GuildCapabilities) => TestBed.inject(Router).createUrlTree(['/rankings', value.leaderboardAlias])
          }
        }
      ]
    });
    store = TestBed.inject(AppStore);
    router = TestBed.inject(Router);
    store.setSelectedGuild(guild);
  });

  it('allows an owner into modules even when no module IDs are returned', async () => {
    loaded = of({ ...capabilities, isOwner: true, moduleIds: [] });
    expect(await run(moduleGuard, { module: 'reporting' })).toBeTrue();
  });

  it('routes a user without the required module to the public ranking', async () => {
    const result = await run(moduleGuard, { module: 'reporting' });
    expect(router.serializeUrl(result as UrlTree)).toBe('/rankings/guild-one');
  });

  it('maps a forbidden capability response to an explicit selection notice', async () => {
    loaded = throwError(() => ({ status: 403 }));
    const result = await run(ownerGuard, {});
    expect(router.serializeUrl(result as UrlTree)).toBe('/server-selection?access=forbidden');
  });

  async function run(guard: typeof moduleGuard, data: Record<string, unknown>): Promise<boolean | UrlTree> {
    const result = TestBed.runInInjectionContext(() => guard(
      { data } as unknown as ActivatedRouteSnapshot,
      { url: '/' } as RouterStateSnapshot
    ));
    return isObservable(result)
      ? await firstValueFrom(result) as boolean | UrlTree
      : await Promise.resolve(result as boolean | UrlTree);
  }
});
