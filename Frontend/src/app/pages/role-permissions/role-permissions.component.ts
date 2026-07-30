import { CommonModule } from '@angular/common';
import { Component, ElementRef, HostListener, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { finalize } from 'rxjs';
import { LocaleService } from '../../i18n/locale.service';
import { GUILD_MODULE_IDS, GuildModuleId, PermissionModule, RolePermission, RolePermissions } from '../../models/guild-permissions.models';
import { ApiErrorService } from '../../services/api-error.service';
import { GuildService } from '../../services/guild.service';
import { ToastService } from '../../services/toast.service';
import { StickySaveBarComponent } from '../../shared/ui/sticky-save-bar/sticky-save-bar.component';
import { AppStore, Guild } from '../../store/app.store';

type PermissionView = 'roles' | 'modules';
type RoleSort = 'position' | 'name' | 'access';
type PermissionPreset = 'analyst' | 'xpModerator' | 'communityManager' | 'fullAccess';

const PRESETS: Record<PermissionPreset, GuildModuleId[]> = {
  analyst: ['analytics', 'diagnostics'],
  xpModerator: ['xp-audit', 'xp-adjustments'],
  communityManager: ['voice-hubs', 'self-roles', 'xp-announcements'],
  fullAccess: [...GUILD_MODULE_IDS],
};

@Component({
  selector: 'app-role-permissions',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, TranslocoPipe, StickySaveBarComponent],
  templateUrl: './role-permissions.component.html',
  styleUrls: ['./role-permissions.component.scss'],
})
export class RolePermissionsComponent {
  private readonly api = inject(GuildService);
  private readonly store = inject(AppStore);
  private readonly i18n = inject(TranslocoService);
  private readonly apiErrors = inject(ApiErrorService);
  private readonly locale = inject(LocaleService);
  private readonly toast = inject(ToastService);

  @ViewChild('rolePicker') private rolePicker?: ElementRef<HTMLDialogElement>;
  @ViewChild('permissionEditor') private permissionEditor?: ElementRef<HTMLDialogElement>;
  @ViewChild('moduleRoleEditor') private moduleRoleEditor?: ElementRef<HTMLDialogElement>;

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly data = signal<RolePermissions | null>(null);
  readonly grants = signal<Map<string, Set<GuildModuleId>>>(new Map());
  readonly search = signal('');
  readonly moduleFilter = signal<GuildModuleId | ''>('');
  readonly elevatedOnly = signal(false);
  readonly view = signal<PermissionView>('roles');
  readonly sort = signal<RoleSort>('position');
  readonly automaticExpanded = signal(false);
  readonly error = signal('');
  readonly forbidden = signal(false);
  readonly stale = signal(false);
  readonly editingRoleId = signal<string | null>(null);
  readonly editorModules = signal<Set<GuildModuleId>>(new Set());
  readonly editingModuleId = signal<GuildModuleId | null>(null);
  readonly moduleEditorRoles = signal<Set<string>>(new Set());

  private baseline = '';
  private baselineGrants = new Map<string, Set<GuildModuleId>>();
  private loadRequest = 0;
  private saveRequest = 0;
  private loadedGuild: Guild | null = null;
  private suppressNextGuildReload = false;
  private approvedGuildChangeId: string | null = null;

  readonly dirty = computed(() => this.serialize(this.grants()) !== this.baseline);
  readonly administrators = computed(() => (this.data()?.roles ?? []).filter(role => role.isAdministrator));
  readonly visibleAdministrators = computed(() => this.automaticExpanded() ? this.administrators() : this.administrators().slice(0, 4));
  readonly delegatedRoles = computed(() => (this.data()?.roles ?? []).filter(role => !role.isAdministrator && (this.grants().get(role.id)?.size ?? 0) > 0));
  readonly manualGrantCount = computed(() => [...this.grants().values()].reduce((count, modules) => count + modules.size, 0));
  readonly visibleRoles = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    const filter = this.moduleFilter();
    const rows = this.delegatedRoles().filter(role =>
      (!query || role.name.toLocaleLowerCase().includes(query)) &&
      (!filter || this.grants().get(role.id)?.has(filter)) &&
      (!this.elevatedOnly() || this.roleHasElevatedImpact(role.id)));
    return rows.sort((left, right) => this.compareRoles(left, right));
  });
  readonly visibleModules = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    const filter = this.moduleFilter();
    return (this.data()?.modules ?? []).filter(module =>
      (!filter || module.id === filter) &&
      (!this.elevatedOnly() || module.impact !== 'ReadOnly') &&
      (!query || this.moduleName(module.id).toLocaleLowerCase().includes(query) || this.moduleDescription(module.id).toLocaleLowerCase().includes(query)));
  });
  readonly pickerRoles = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    return [...(this.data()?.roles ?? [])]
      .filter(role => (role.isAdministrator || !this.grants().has(role.id)) && (!query || role.name.toLocaleLowerCase().includes(query)))
      .sort((left, right) => right.position - left.position);
  });
  readonly editingRole = computed(() => this.data()?.roles.find(role => role.id === this.editingRoleId()) ?? null);
  readonly dirtySummary = computed(() => {
    const current = this.grants();
    const ids = new Set([...this.baselineGrants.keys(), ...current.keys()]);
    let added = 0; let removed = 0; let changed = 0;
    ids.forEach(id => {
      const before = this.baselineGrants.get(id);
      const after = current.get(id);
      if (this.setKey(before) !== this.setKey(after)) changed++;
      for (const moduleId of after ?? []) if (!before?.has(moduleId)) added++;
      for (const moduleId of before ?? []) if (!after?.has(moduleId)) removed++;
    });
    return this.i18n.translate('rolePermissions.dirtyDiff', { added, removed, changed });
  });

  constructor() {
    effect(() => {
      const selected = this.store.selectedGuild();
      if (this.suppressNextGuildReload && selected?.id === this.loadedGuild?.id) {
        this.suppressNextGuildReload = false;
        return;
      }
      if (this.loadedGuild && selected?.id !== this.loadedGuild.id && this.dirty() && !window.confirm(this.i18n.translate('rolePermissions.unsavedLeave'))) {
        this.suppressNextGuildReload = true;
        this.store.setSelectedGuild(this.loadedGuild);
        return;
      }
      if (this.loadedGuild && selected?.id !== this.loadedGuild.id) {
        this.approvedGuildChangeId = selected?.id ?? null;
        this.closeDialogs();
        this.resetWorkspace();
      }
      this.load();
    });
  }

  @HostListener('window:beforeunload', ['$event'])
  protectUnsavedChanges(event: BeforeUnloadEvent): void {
    if (!this.dirty()) return;
    event.preventDefault();
    event.returnValue = '';
  }

  canDeactivate(): boolean {
    return !this.dirty() || this.store.selectedGuild()?.id === this.approvedGuildChangeId || window.confirm(this.i18n.translate('rolePermissions.unsavedLeave'));
  }

  load(force = false): void {
    if (force && this.dirty() && !window.confirm(this.i18n.translate('rolePermissions.loadCurrentConfirm'))) return;
    const guildId = this.store.selectedGuild()?.id;
    const request = ++this.loadRequest;
    if (!guildId) {
      this.loading.set(false);
      this.error.set(this.i18n.translate('errors.noServer'));
      return;
    }
    this.loading.set(true);
    this.error.set('');
    this.forbidden.set(false);
    this.api.rolePermissions(guildId).pipe(finalize(() => {
      if (request === this.loadRequest) this.loading.set(false);
    })).subscribe({
      next: data => {
        if (request !== this.loadRequest || this.store.selectedGuild()?.id !== guildId) return;
        if (!data.isOwner) return this.handleForbidden();
        this.applyResponse(data);
        this.loadedGuild = this.store.selectedGuild();
        this.approvedGuildChangeId = null;
        this.stale.set(false);
      },
      error: error => {
        if (request !== this.loadRequest) return;
        if (error?.status === 403) return this.handleForbidden();
        this.error.set(this.apiErrors.resolve(error, 'errors.permissionsLoad').message);
      },
    });
  }

  save(): void {
    const guildId = this.store.selectedGuild()?.id;
    const data = this.data();
    if (!guildId || !data || !this.dirty() || this.saving() || this.stale()) return;
    const roles = this.payloadRoles();
    const request = ++this.saveRequest;
    this.saving.set(true);
    this.error.set('');
    this.api.saveRolePermissions(guildId, { revision: data.revision, roles }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: response => {
        if (request !== this.saveRequest || this.store.selectedGuild()?.id !== guildId) return;
        this.applyResponse(response);
        this.toast.success(this.i18n.translate('rolePermissions.saved'));
      },
      error: error => {
        if (request !== this.saveRequest || this.store.selectedGuild()?.id !== guildId) return;
        if (error?.status === 403) return this.handleForbidden();
        if (error?.status === 409) {
          this.stale.set(true);
          return;
        }
        const message = this.apiErrors.resolve(error, 'errors.save').message;
        this.error.set(message);
        this.toast.error(message);
      },
    });
  }

  reset(): void {
    if (this.dirty() && !window.confirm(this.i18n.translate('rolePermissions.discardConfirm'))) return;
    this.grants.set(this.cloneGrants(this.baselineGrants));
    this.error.set('');
    this.closeDialogs();
  }

  openRolePicker(): void {
    this.search.set('');
    this.openDialog(this.rolePicker);
  }

  chooseRole(role: RolePermission): void {
    if (role.isAdministrator || this.grants().has(role.id)) return;
    this.rolePicker?.nativeElement.close();
    this.openRoleEditor(role);
  }

  openRoleEditor(role: RolePermission): void {
    if (role.isAdministrator) return;
    this.editingRoleId.set(role.id);
    this.editorModules.set(new Set(this.grants().get(role.id) ?? []));
    this.openDialog(this.permissionEditor);
  }

  toggleEditorModule(module: PermissionModule, checked: boolean): void {
    if (!checked && this.isEditorModuleLocked(module.id)) return;
    const next = new Set(this.editorModules());
    checked ? next.add(module.id) : next.delete(module.id);
    if (checked) module.requiredModuleIds.forEach(required => next.add(required));
    this.editorModules.set(next);
  }

  isEditorModuleLocked(moduleId: GuildModuleId): boolean {
    return (this.data()?.modules ?? []).some(module => this.editorModules().has(module.id) && module.requiredModuleIds.includes(moduleId));
  }

  applyPreset(preset: PermissionPreset): void {
    if (!window.confirm(this.i18n.translate('rolePermissions.replacePresetConfirm', { preset: this.i18n.translate(`rolePermissions.presets.${preset}`) }))) return;
    this.editorModules.set(new Set(PRESETS[preset]));
  }

  applyRoleEditor(): void {
    const role = this.editingRole();
    if (!role || role.isAdministrator || this.editorModules().size === 0) return;
    this.updateGrant(role.id, this.editorModules());
    this.permissionEditor?.nativeElement.close();
  }

  removeRole(role = this.editingRole()): void {
    if (!role || role.isAdministrator || !window.confirm(this.i18n.translate('rolePermissions.removeConfirm', { role: role.name }))) return;
    this.updateGrant(role.id, new Set());
    this.permissionEditor?.nativeElement.close();
  }

  openModuleRoles(module: PermissionModule): void {
    this.editingModuleId.set(module.id);
    this.moduleEditorRoles.set(new Set(this.delegatedRoles().filter(role => this.grants().get(role.id)?.has(module.id)).map(role => role.id)));
    this.openDialog(this.moduleRoleEditor);
  }

  toggleModuleRole(role: RolePermission, checked: boolean): void {
    if (role.isAdministrator) return;
    const next = new Set(this.moduleEditorRoles());
    checked ? next.add(role.id) : next.delete(role.id);
    this.moduleEditorRoles.set(next);
  }

  applyModuleRoles(): void {
    const moduleId = this.editingModuleId();
    const module = this.data()?.modules.find(item => item.id === moduleId);
    if (!moduleId || !module) return;
    const next = this.cloneGrants(this.grants());
    for (const role of this.data()?.roles ?? []) {
      if (role.isAdministrator) continue;
      const modules = new Set(next.get(role.id) ?? []);
      if (this.moduleEditorRoles().has(role.id)) {
        modules.add(moduleId);
        module.requiredModuleIds.forEach(required => modules.add(required));
      } else if (!this.isRequiredBySelected(modules, moduleId)) {
        modules.delete(moduleId);
      }
      modules.size ? next.set(role.id, modules) : next.delete(role.id);
    }
    this.grants.set(next);
    this.moduleRoleEditor?.nativeElement.close();
  }

  rolesForModule(moduleId: GuildModuleId): RolePermission[] {
    return this.delegatedRoles().filter(role => this.grants().get(role.id)?.has(moduleId));
  }

  moduleName(moduleId: GuildModuleId): string { return this.i18n.translate(`modules.${moduleId}.name`); }
  moduleDescription(moduleId: GuildModuleId): string { return this.i18n.translate(`modules.${moduleId}.description`); }
  categoryName(category: PermissionModule['category']): string { return this.i18n.translate(`rolePermissions.categories.${category}`); }
  impactName(impact: PermissionModule['impact']): string { return this.i18n.translate(`rolePermissions.impacts.${impact}`); }
  formatNumber(value: number): string { return this.locale.number(value); }
  formatDate(value: string | null): string { return value ? this.locale.date(value, { dateStyle: 'medium', timeStyle: 'short' }) : this.i18n.translate('rolePermissions.neverUpdated'); }
  roleColor(role: RolePermission): string { return /^#[0-9a-f]{6}$/i.test(role.colorHex) ? role.colorHex : 'currentColor'; }
  roleModules(roleId: string): GuildModuleId[] { return [...(this.grants().get(roleId) ?? [])].sort((a, b) => GUILD_MODULE_IDS.indexOf(a) - GUILD_MODULE_IDS.indexOf(b)); }
  roleModulePreview(roleId: string): GuildModuleId[] { return this.roleModules(roleId).slice(0, 3); }
  roleModuleOverflow(roleId: string): number { return Math.max(0, this.roleModules(roleId).length - 3); }
  trackModule(_: number, module: PermissionModule): string { return module.id; }
  trackRole(_: number, role: RolePermission): string { return role.id; }

  payloadRoles(): { roleId: string; moduleIds: GuildModuleId[] }[] {
    const administrators = new Set((this.data()?.roles ?? []).filter(role => role.isAdministrator).map(role => role.id));
    return [...this.grants()]
      .filter(([roleId, modules]) => !administrators.has(roleId) && modules.size > 0)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([roleId, modules]) => ({ roleId, moduleIds: [...modules].sort((a, b) => GUILD_MODULE_IDS.indexOf(a) - GUILD_MODULE_IDS.indexOf(b)) }));
  }

  private applyResponse(data: RolePermissions): void {
    const normalRoleIds = new Set(data.roles.filter(role => !role.isAdministrator).map(role => role.id));
    const grants = new Map(data.roles
      .filter(role => normalRoleIds.has(role.id) && role.moduleIds.length > 0)
      .map(role => [role.id, new Set(role.moduleIds)] as [string, Set<GuildModuleId>]));
    this.data.set(data);
    this.grants.set(grants);
    this.baselineGrants = this.cloneGrants(grants);
    this.baseline = this.serialize(grants);
    this.error.set('');
  }

  private updateGrant(roleId: string, modules: Set<GuildModuleId>): void {
    const next = this.cloneGrants(this.grants());
    modules.size ? next.set(roleId, new Set(modules)) : next.delete(roleId);
    this.grants.set(next);
  }

  private roleHasElevatedImpact(roleId: string): boolean {
    const modules = this.grants().get(roleId) ?? new Set();
    return (this.data()?.modules ?? []).some(module => modules.has(module.id) && module.impact !== 'ReadOnly');
  }

  private compareRoles(left: RolePermission, right: RolePermission): number {
    if (this.sort() === 'name') return left.name.localeCompare(right.name);
    if (this.sort() === 'access') return (this.grants().get(right.id)?.size ?? 0) - (this.grants().get(left.id)?.size ?? 0) || right.position - left.position;
    return right.position - left.position;
  }

  private isRequiredBySelected(modules: Set<GuildModuleId>, moduleId: GuildModuleId): boolean {
    return (this.data()?.modules ?? []).some(module => modules.has(module.id) && module.requiredModuleIds.includes(moduleId));
  }

  private serialize(grants: Map<string, Set<GuildModuleId>>): string {
    return JSON.stringify([...grants]
      .filter(([, modules]) => modules.size > 0)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([roleId, modules]) => [roleId, [...modules].sort()]));
  }

  private cloneGrants(source: Map<string, Set<GuildModuleId>>): Map<string, Set<GuildModuleId>> {
    return new Map([...source].map(([roleId, modules]) => [roleId, new Set(modules)]));
  }

  private setKey(value?: Set<GuildModuleId>): string { return [...(value ?? [])].sort().join('|'); }

  private openDialog(dialog?: ElementRef<HTMLDialogElement>): void {
    setTimeout(() => {
      const element = dialog?.nativeElement;
      if (element?.isConnected && !element.open) element.showModal();
    });
  }

  private closeDialogs(): void {
    [this.rolePicker, this.permissionEditor, this.moduleRoleEditor].forEach(dialog => {
      if (dialog?.nativeElement.open) dialog.nativeElement.close();
    });
    this.editingRoleId.set(null);
    this.editingModuleId.set(null);
  }

  private handleForbidden(): void {
    this.forbidden.set(true);
    this.error.set('');
    this.closeDialogs();
    this.data.set(null);
  }

  private resetWorkspace(): void {
    this.saveRequest++;
    this.data.set(null);
    this.grants.set(new Map());
    this.baselineGrants = new Map();
    this.baseline = '';
    this.loadedGuild = null;
    this.search.set('');
    this.moduleFilter.set('');
    this.elevatedOnly.set(false);
    this.view.set('roles');
    this.sort.set('position');
    this.automaticExpanded.set(false);
    this.stale.set(false);
  }
}
