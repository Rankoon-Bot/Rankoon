import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { environment } from '../../../environments/environment';
import { LOCALE_STORAGE_KEY } from '../../i18n/locale.service';
import { GUILD_MODULE_IDS, GuildModuleId, PermissionModule, RolePermissions } from '../../models/guild-permissions.models';
import { ToastService } from '../../services/toast.service';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { RolePermissionsComponent } from './role-permissions.component';

describe('RolePermissionsComponent', () => {
  const guild: Guild = {
    id: 'guild-1', name: 'Guild One', icon: null, owner: true, permissions: '8',
    features: [], botInstalled: true, inviteUrl: '',
  };
  const module = (id: PermissionModule['id'], category: PermissionModule['category'], impact: PermissionModule['impact'], requiredModuleIds: PermissionModule['requiredModuleIds'] = []): PermissionModule => ({ id, category, impact, requiredModuleIds });
  const modules: PermissionModule[] = [
    module('xp', 'Progression', 'Sensitive'),
    module('leaderboard', 'Progression', 'Configuration'),
    module('voice-hubs', 'Community', 'Configuration'),
    module('analytics', 'Insights', 'ReadOnly'),
    module('self-roles', 'Community', 'Sensitive'),
    module('xp-audit', 'XpModeration', 'ReadOnly'),
    module('xp-adjustments', 'XpModeration', 'Sensitive', ['xp-audit']),
    module('xp-announcements', 'Progression', 'Configuration'),
    module('diagnostics', 'Insights', 'ReadOnly'),
  ];
  const response: RolePermissions = {
    guildId: 'guild-1', isOwner: true, revision: 4, updatedAt: '2026-07-19T12:00:00Z', modules,
    roles: [
      { id: 'admin', name: 'Admin', position: 10, colorHex: '#ff0000', isAdministrator: true, moduleIds: [], effectiveModuleIds: [...GUILD_MODULE_IDS], accessSource: 'DiscordAdministrator' },
      { id: 'moderator', name: 'Moderator', position: 5, colorHex: '#00ff00', isAdministrator: false, moduleIds: ['xp'], effectiveModuleIds: ['xp'], accessSource: 'Delegated' },
      { id: 'helper', name: 'Helper', position: 3, colorHex: '#0000ff', isAdministrator: false, moduleIds: [], effectiveModuleIds: [], accessSource: 'None' },
    ],
  };

  let fixture: ComponentFixture<RolePermissionsComponent>;
  let component: RolePermissionsComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    localStorage.setItem(LOCALE_STORAGE_KEY, 'en');
    TestBed.configureTestingModule({
      imports: [RolePermissionsComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
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

  it('keeps administrators automatic and shows only non-empty delegated draft roles', () => {
    expect(component.dirty()).toBeFalse();
    expect(component.administrators().map(role => role.id)).toEqual(['admin']);
    expect(component.delegatedRoles().map(role => role.id)).toEqual(['moderator']);
    expect(component.grants().has('admin')).toBeFalse();
    expect(component.manualGrantCount()).toBe(1);
  });

  it('enforces the XP adjustment dependency and locks XP audit while required', () => {
    component.openRoleEditor(response.roles[1]);
    component.toggleEditorModule(modules.find(item => item.id === 'xp-adjustments')!, true);
    expect(component.editorModules().has('xp-adjustments')).toBeTrue();
    expect(component.editorModules().has('xp-audit')).toBeTrue();
    expect(component.isEditorModuleLocked('xp-audit')).toBeTrue();

    component.toggleEditorModule(modules.find(item => item.id === 'xp-audit')!, false);
    expect(component.editorModules().has('xp-audit')).toBeTrue();
  });

  it('applies the exact analyst and XP moderator presets', () => {
    spyOn(window, 'confirm').and.returnValue(true);
    component.openRoleEditor(response.roles[2]);
    component.applyPreset('analyst' as never);
    expect(component.editorModules()).toEqual(new Set(['analytics', 'diagnostics']));
    component.applyPreset('xpModerator' as never);
    expect(component.editorModules()).toEqual(new Set(['xp-audit', 'xp-adjustments']));
  });

  it('excludes delegated roles from the picker and keeps administrators disabled', () => {
    expect(component.pickerRoles().map(role => role.id)).toEqual(['admin', 'helper']);
    expect(component.pickerRoles()[0].isAdministrator).toBeTrue();
    component.chooseRole(response.roles[0]);
    expect(component.editingRole()).toBeNull();
  });

  it('filters by search and module and sorts by Discord hierarchy', () => {
    component.grants.set(new Map<string, Set<GuildModuleId>>([
      ['moderator', new Set(['xp'])],
      ['helper', new Set(['analytics'])],
    ]));
    expect(component.visibleRoles().map(role => role.id)).toEqual(['moderator', 'helper']);
    component.moduleFilter.set('analytics');
    expect(component.visibleRoles().map(role => role.id)).toEqual(['helper']);
    component.moduleFilter.set('');
    component.search.set('mod');
    expect(component.visibleRoles().map(role => role.id)).toEqual(['moderator']);
  });

  it('removes a role from the draft without issuing a request', () => {
    spyOn(window, 'confirm').and.returnValue(true);
    component.removeRole(response.roles[1]);
    expect(component.grants().has('moderator')).toBeFalse();
    expect(component.dirty()).toBeTrue();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
  });

  it('protects navigation while the draft is dirty', () => {
    const confirm = spyOn(window, 'confirm').and.returnValue(false);
    component.grants.set(new Map([['helper', new Set(['analytics'])]]));
    expect(component.canDeactivate()).toBeFalse();
    expect(confirm).toHaveBeenCalled();
  });

  it('serializes deterministic non-empty non-admin grants only', () => {
    component.grants.set(new Map([
      ['moderator', new Set(['diagnostics', 'xp'])],
      ['helper', new Set()],
      ['admin', new Set(['xp'])],
    ]));

    expect(component.payloadRoles()).toEqual([
      { roleId: 'moderator', moduleIds: ['xp', 'diagnostics'] },
    ]);
  });

  it('saves the deterministic draft and applies the returned revision', () => {
    component.grants.set(new Map([['moderator', new Set(['xp', 'analytics'])]]));
    component.save();
    const request = http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ revision: 4, roles: [{ roleId: 'moderator', moduleIds: ['xp', 'analytics'] }] });
    request.flush({ ...response, revision: 5, roles: response.roles.map(role => role.id === 'moderator' ? { ...role, moduleIds: ['xp', 'analytics'], effectiveModuleIds: ['xp', 'analytics'] } : role) });
    expect(component.dirty()).toBeFalse();
    expect(TestBed.inject(ToastService).toasts()).toEqual([jasmine.objectContaining({ message: 'Role permissions saved.', type: 'success' })]);
  });

  it('retains the local draft and blocks another save after a revision conflict', () => {
    component.grants.set(new Map([['moderator', new Set(['analytics'])]]));
    component.save();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`).flush({}, { status: 409, statusText: 'Conflict' });
    expect(component.stale()).toBeTrue();
    expect(component.grants().get('moderator')).toEqual(new Set(['analytics']));

    component.save();
    http.expectNone(`${environment.apiBaseUrl}/guilds/guild-1/role-permissions`);

    spyOn(window, 'confirm').and.returnValue(true);
    component.reset();
    expect(component.stale()).toBeTrue();
  });

  it('resets all local changes to the loaded baseline', () => {
    spyOn(window, 'confirm').and.returnValue(true);
    component.grants.set(new Map([['helper', new Set(['voice-hubs'])]]));
    expect(component.dirty()).toBeTrue();
    component.reset();
    expect(component.dirty()).toBeFalse();
    expect(component.grants().get('moderator')).toEqual(new Set(['xp']));
    expect(component.grants().has('helper')).toBeFalse();
  });
});
