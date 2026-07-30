export const GUILD_MODULE_IDS = ['xp', 'leaderboard', 'voice-hubs', 'analytics', 'self-roles', 'xp-audit', 'xp-adjustments', 'xp-announcements', 'diagnostics'] as const;

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
  category: 'Progression' | 'XpModeration' | 'Community' | 'Insights';
  impact: 'ReadOnly' | 'Configuration' | 'Sensitive';
  requiredModuleIds: GuildModuleId[];
}

export interface RolePermission {
  id: string;
  name: string;
  position: number;
  colorHex: string;
  isAdministrator: boolean;
  moduleIds: GuildModuleId[];
  effectiveModuleIds: GuildModuleId[];
  accessSource: 'DiscordAdministrator' | 'Delegated' | 'None';
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
