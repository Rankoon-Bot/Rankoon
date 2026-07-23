import { SELF_ROLE_DEFAULT_COLOR, SELF_ROLE_MAX_EMBEDS, copySelfRoleEmbeds, createSelfRoleEmbed, normalizeSelfRoleEmbeds } from './self-role-embed.utils';

describe('self-role embed utilities', () => {
  it('creates an empty embed with the panel accent color', () => {
    expect(createSelfRoleEmbed()).toEqual({ kind: 'Content', title: '', description: '', color: SELF_ROLE_DEFAULT_COLOR, fields: [] });
    expect(SELF_ROLE_MAX_EMBEDS).toBe(10);
  });

  it('uses the legacy panel fields when a stored panel has no embeds', () => {
    expect(normalizeSelfRoleEmbeds({ title: 'Roles', description: 'Choose carefully', color: '#123456' })).toEqual([
      { kind: 'RoleMappings', title: 'Roles', description: 'Choose carefully', color: '#123456', fields: [] }
    ]);
  });

  it('normalizes partial embeds and does not expose mutable panel data', () => {
    const source = [{ kind: 'Content' as const, title: 'First', description: '', color: '#abcdef', fields: [] }];
    const normalized = normalizeSelfRoleEmbeds({ title: '', description: '', color: '#000000', embeds: source });
    const copy = copySelfRoleEmbeds(normalized);

    normalized[0].title = 'Changed';

    expect(source[0].title).toBe('First');
    expect(copy[0].title).toBe('First');
  });
});
