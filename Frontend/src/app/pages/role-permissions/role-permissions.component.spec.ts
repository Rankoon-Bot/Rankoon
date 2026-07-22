import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { AppStore, Guild } from '../../store/app.store';
import { RolePermissionsComponent } from './role-permissions.component';
import { testI18n } from '../../testing/i18n-testing';
import { LOCALE_STORAGE_KEY } from '../../i18n/locale.service';
import { ToastService } from '../../services/toast.service';

describe('RolePermissionsComponent', () => {
  const guild: Guild = {
    id: 'guild-1', name: 'Guild One', icon: null, owner: true, permissions: '8',
    features: [], botInstalled: true, inviteUrl: ''
  };
  const response = {
    guildId: 'guild-1', isOwner: true, revision: 1, updatedAt: '2026-07-19T12:00:00Z',
    modules: [
      { id: 'xp' as const, name: 'XP', description: 'XP verwalten' },
      { id: 'reporting' as const, name: 'Berichte', description: 'Berichte ansehen' }
    ],
    roles: [
      { id: 'admin', name: 'Admin', position: 10, isAdministrator: true, moduleIds: ['xp' as const, 'reporting' as const] },
      { id: 'moderator', name: 'Moderator', position: 5, isAdministrator: false, moduleIds: ['xp' as const] }
    ]
  };

  let fixture: ComponentFixture<RolePermissionsComponent>;
  let component: RolePermissionsComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    localStorage.setItem(LOCALE_STORAGE_KEY, 'en');
    TestBed.configureTestingModule({
      imports: [RolePermissionsComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    TestBed.inject(TranslocoService).setActiveLang('en');
    TestBed.inject(AppStore).setSelectedGuild(guild);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RolePermissionsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`).flush(response);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem(LOCALE_STORAGE_KEY);
  });

  it('loads clean and allows the owner to remove Discord administrator access', () => {
    expect(component.dirty()).toBeFalse();
    component.toggleModule(response.roles[0], 'xp', false);
    expect(component.hasAllModules(response.roles[0])).toBeFalse();
    expect(component.dirty()).toBeTrue();
  });

  it('tracks changes and saves the complete role matrix', () => {
    component.toggleModule(response.roles[1], 'reporting', true);
    expect(component.dirty()).toBeTrue();

    component.save();
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.revision).toBe(response.revision);
    expect(request.request.body.roles).toEqual([
      { roleId: 'admin', moduleIds: ['xp', 'reporting'] },
      { roleId: 'moderator', moduleIds: ['xp', 'reporting'] }
    ]);
    request.flush(response);

    expect(component.dirty()).toBeFalse();
    expect(TestBed.inject(ToastService).toasts()).toEqual([jasmine.objectContaining({ message: 'Role permissions saved.', type: 'success' })]);
  });
});
