import { SelfRolePanel } from '../../services/guild.service';

export function selfRoleHealthState(panel: SelfRolePanel): NonNullable<SelfRolePanel['state']> {
  if (!panel.enabled) return 'Disabled';
  const state = panel.state ?? panel.status;
  return state === 'Published' || state === 'Pending' || state === 'Degraded' ? state : 'Unknown';
}

export function selfRoleHealthDetailKey(panel: SelfRolePanel): string {
  const key = panel.lastError;
  if (!key) return 'selfRoles.healthDetails.unavailable';
  return key.startsWith('selfRoles.healthDetails.') ? key : `apiErrors.${key}`;
}
