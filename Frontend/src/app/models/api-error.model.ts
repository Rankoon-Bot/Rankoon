export interface ApiValidationItem {
  errorKey?: string;
  message?: string;
  field?: string;
  parameters?: Record<string, unknown>;
}

export interface ApiErrorBody {
  errorKey?: string;
  message?: string;
  parameters?: Record<string, unknown>;
  errors?: Record<string, ApiValidationItem[]>;
}

export interface ResolvedApiError {
  message: string;
  validation: Array<ApiValidationItem & { message: string }>;
}

export const KNOWN_API_ERROR_KEYS = [
  'request.badRequest', 'request.validationFailed', 'request.malformedJson',
  'request.unsupportedMediaType', 'request.methodNotAllowed', 'request.rejected',
  'resource.notFound', 'auth.unauthorized', 'auth.forbidden', 'rateLimit.exceeded',
  'server.internal', 'guild.invalidId', 'user.invalidId', 'user.notFound',
  'auth.refreshTokenRequired', 'auth.refreshTokenInvalid', 'auth.logoutFailed',
  'auth.tokenInvalid', 'auth.tokenMissing', 'auth.guildsUnavailable', 'auth.oauthFailed',
  'leaderboard.invalidCursor', 'leaderboard.invalidAlias', 'leaderboard.aliasConflict',
  'reports.invalidQuery', 'permissions.rolesRequired', 'permissions.nullRole',
  'permissions.duplicateRole', 'permissions.roleNotInGuild', 'permissions.modulesRequired',
  'permissions.duplicateModule', 'permissions.unknownModule', 'permissions.revisionConflict',
  'xp.settingsInvalid', 'xp.settings.groupsRequired', 'xp.settings.collectionsRequired',
  'xp.settings.messagePoints', 'xp.settings.messageCharacters', 'xp.settings.messageCooldown',
  'xp.settings.voicePoints', 'xp.settings.voiceTiming', 'xp.settings.reaction',
  'xp.settings.eventInterest', 'xp.settings.thread', 'xp.settings.channelMultipliers',
  'xp.settings.levelRoles', 'xp.import.guildMismatch', 'xp.import.invalidPlayers'
] as const;
