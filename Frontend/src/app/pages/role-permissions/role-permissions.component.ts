import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PermissionModule, RolePermission, RolePermissions, GuildModuleId } from '../../models/guild-permissions.models';
import { GuildService } from '../../services/guild.service';
import { AppStore } from '../../store/app.store';

@Component({
  selector: 'app-role-permissions',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './role-permissions.component.html',
  styleUrls: ['./role-permissions.component.scss']
})
export class RolePermissionsComponent implements OnInit {
  private readonly api = inject(GuildService);
  private readonly appStore = inject(AppStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly baseline = signal('');

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly data = signal<RolePermissions | null>(null);
  readonly assignments = signal<Record<string, GuildModuleId[]>>({});
  readonly search = signal('');
  readonly error = signal('');
  readonly success = signal('');
  readonly forbidden = signal(false);
  readonly dirty = computed(() => this.serialize(this.assignments()) !== this.baseline());
  readonly filteredRoles = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    return (this.data()?.roles ?? [])
      .filter(role => !query || role.name.toLocaleLowerCase().includes(query))
      .sort((a, b) => b.position - a.position);
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    if (!guildId) return;
    this.loading.set(true);
    this.error.set('');
    this.forbidden.set(false);
    this.api.rolePermissions(guildId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: data => {
        if (this.appStore.selectedGuild()?.id !== guildId) return;
        if (!data.isOwner) {
          this.forbidden.set(true);
          this.loading.set(false);
          return;
        }
        this.applyResponse(data);
        this.loading.set(false);
      },
      error: error => {
        this.forbidden.set(error?.status === 403);
        this.error.set(error?.status === 403
          ? 'Nur der Server-Owner kann Rollenberechtigungen verwalten.'
          : 'Die Rollenberechtigungen konnten nicht geladen werden.');
        this.loading.set(false);
      }
    });
  }

  hasModule(role: RolePermission, moduleId: GuildModuleId): boolean {
    return this.assignments()[role.id]?.includes(moduleId) === true;
  }

  toggleModule(role: RolePermission, moduleId: GuildModuleId, checked: boolean): void {
    this.assignments.update(current => {
      const modules = new Set(current[role.id] ?? []);
      checked ? modules.add(moduleId) : modules.delete(moduleId);
      return { ...current, [role.id]: [...modules] };
    });
    this.success.set('');
  }

  hasAllModules(role: RolePermission): boolean {
    const modules = this.data()?.modules ?? [];
    return modules.every(module => this.hasModule(role, module.id));
  }

  hasSomeModules(role: RolePermission): boolean {
    return !this.hasAllModules(role) && (this.assignments()[role.id]?.length ?? 0) > 0;
  }

  toggleAll(role: RolePermission, checked: boolean): void {
    this.assignments.update(current => ({
      ...current,
      [role.id]: checked ? (this.data()?.modules.map(module => module.id) ?? []) : []
    }));
    this.success.set('');
  }

  save(): void {
    const guildId = this.appStore.selectedGuild()?.id;
    const data = this.data();
    if (!guildId || !data || !this.dirty()) return;
    this.saving.set(true);
    this.error.set('');
    this.success.set('');
    this.api.saveRolePermissions(guildId, {
      revision: data.revision,
      roles: data.roles.map(role => ({
        roleId: role.id,
        moduleIds: this.assignments()[role.id] ?? []
      }))
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => {
        if (this.appStore.selectedGuild()?.id !== guildId) return;
        this.applyResponse(response);
        this.success.set('Rollenberechtigungen wurden gespeichert.');
        this.saving.set(false);
      },
      error: error => {
        this.forbidden.set(error?.status === 403);
        this.error.set(error?.status === 403
          ? 'Dein Zugriff wurde abgelehnt. Lade die Seite neu oder prüfe den Server-Owner.'
          : error?.status === 409
            ? 'Die Berechtigungen wurden zwischenzeitlich geändert. Lade die Seite neu und prüfe deine Änderungen.'
            : 'Die Änderungen konnten nicht gespeichert werden.');
        this.saving.set(false);
      }
    });
  }

  trackModule(_: number, module: PermissionModule): string { return module.id; }
  trackRole(_: number, role: RolePermission): string { return role.id; }

  private applyResponse(data: RolePermissions): void {
    const assignments = Object.fromEntries(data.roles.map(role => [role.id, [...role.moduleIds]]));
    this.data.set(data);
    this.assignments.set(assignments);
    this.baseline.set(this.serialize(assignments));
  }

  private serialize(assignments: Record<string, GuildModuleId[]>): string {
    return JSON.stringify(Object.entries(assignments)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([roleId, modules]) => [roleId, [...modules].sort()]));
  }
}
