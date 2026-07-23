export const GUILD_MODULE_IDS = ['xp', 'xp-audit', 'xp-adjustments', 'xp-announcements', 'leaderboard', 'voice-hubs', 'reporting', 'self-roles', 'diagnostics'] as const;

export type GuildModuleId = typeof GUILD_MODULE_IDS[number];

export interface GuildCapabilities {
  guildId: string;
  isOwner: boolean;
  canAccessSettings: boolean;
  moduleIds: GuildModuleId[];
  leaderboardAlias: string;
}

export interface PermissionModule {
  id: GuildModuleId;
  name: string;
  description: string;
}

export interface RolePermission {
  id: string;
  name: string;
  position: number;
  isAdministrator: boolean;
  moduleIds: GuildModuleId[];
}

export interface RolePermissions {
  guildId: string;
  isOwner: boolean;
  revision: number;
  modules: PermissionModule[];
  roles: RolePermission[];
  updatedAt: string | null;
}

export interface SaveRolePermissions {
  revision: number;
  roles: { roleId: string; moduleIds: GuildModuleId[] }[];
}
