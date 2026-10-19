import { SelfRolePanel } from '../../services/guild.service';
import { selfRoleHealthDetailKey, selfRoleHealthState } from './self-role-health.utils';

describe('self-role health presentation', () => {
  const panel: SelfRolePanel = { channelId: '1', enabled: true, revision: 1, mappings: [], embeds: [] };

  it('does not assume an unchecked panel is working', () => {
    expect(selfRoleHealthState(panel)).toBe('Unknown');
    expect(selfRoleHealthState({ ...panel, status: 'Published' })).toBe('Published');
  });

  it('shows disabled panels as disabled despite stale health errors', () => {
    expect(selfRoleHealthState({ ...panel, enabled: false, state: 'Degraded' })).toBe('Disabled');
  });

  it('distinguishes inconclusive checks from confirmed problems', () => {
    expect(selfRoleHealthState({ ...panel, state: 'Unknown' })).toBe('Unknown');
    expect(selfRoleHealthState({ ...panel, state: 'Degraded' })).toBe('Degraded');
  });

  it('resolves health explanations and legacy errors through translations', () => {
    expect(selfRoleHealthDetailKey({ ...panel, lastError: 'selfRoles.healthDetails.messageMissing' })).toBe('selfRoles.healthDetails.messageMissing');
    expect(selfRoleHealthDetailKey({ ...panel, lastError: 'selfRoles.publishFailed' })).toBe('apiErrors.selfRoles.publishFailed');
  });
});
