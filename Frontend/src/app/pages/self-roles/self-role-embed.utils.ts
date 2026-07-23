import { SelfRoleEmbed, SelfRoleMapping, SelfRolePanel } from '../../services/guild.service';

export const SELF_ROLE_MAX_EMBEDS = 10;
export const SELF_ROLE_MAX_FIELDS = 25;
export const SELF_ROLE_MAX_TITLE = 256;
export const SELF_ROLE_MAX_DESCRIPTION = 4096;
export const SELF_ROLE_MAX_FIELD_NAME = 256;
export const SELF_ROLE_MAX_FIELD_VALUE = 1024;
export const SELF_ROLE_MAX_TEXT = 6000;
export const SELF_ROLE_DEFAULT_COLOR = '#5865F2';

export function createSelfRoleEmbed(kind: 'Content' | 'RoleMappings' = 'Content'): SelfRoleEmbed {
  return { kind, title: '', description: '', color: SELF_ROLE_DEFAULT_COLOR, fields: [] };
}

export function legend(mappings: SelfRoleMapping[]): string {
  return mappings.map(mapping => `${mapping.emoji.kind === 'Custom' ? `<:${mapping.emoji.name}:${mapping.emoji.value}>` : mapping.emoji.value} <@&${mapping.roleId}>`).join('\n');
}

export function finalDescription(embed: SelfRoleEmbed, mappings: SelfRoleMapping[]): string {
  const text = embed.description.trim(); const generated = embed.kind === 'RoleMappings' ? legend(mappings) : '';
  return text && generated ? `${text}\n\n${generated}` : text || generated;
}

export function normalizeSelfRoleEmbeds(panel: Pick<SelfRolePanel, 'title' | 'description' | 'color'> & { embeds?: SelfRoleEmbed[] }): SelfRoleEmbed[] {
  const source = panel.embeds?.length ? panel.embeds : [{ ...createSelfRoleEmbed('RoleMappings'), title: panel.title ?? '', description: panel.description ?? '', color: panel.color || SELF_ROLE_DEFAULT_COLOR }];
  return source.map((embed, index) => ({ ...createSelfRoleEmbed(index === 0 && !source.some(item => item.kind === 'RoleMappings') ? 'RoleMappings' : 'Content'), ...embed, fields: (embed.fields ?? []).map(field => ({ ...field })) }));
}

export function copySelfRoleEmbeds(embeds: SelfRoleEmbed[]): SelfRoleEmbed[] { return embeds.map(embed => ({ ...embed, fields: embed.fields.map(field => ({ ...field })) })); }

export function embedTextLength(embeds: SelfRoleEmbed[], mappings: SelfRoleMapping[]): number {
  return embeds.reduce((total, embed) => total + embed.title.trim().length + finalDescription(embed, mappings).length + embed.fields.reduce((fields, field) => fields + field.name.trim().length + field.value.trim().length, 0), 0);
}

export function embedValidation(embeds: SelfRoleEmbed[], mappings: SelfRoleMapping[]): string[] {
  const errors: string[] = [];
  if (!embeds.length || embeds.length > SELF_ROLE_MAX_EMBEDS || embeds.filter(embed => embed.kind === 'RoleMappings').length !== 1) errors.push('structure');
  if (!embeds.some(embed => embed.title.trim() || embed.description.trim() || embed.fields.length || (embed.kind === 'RoleMappings' && mappings.length))) errors.push('empty');
  for (const embed of embeds) {
    if (embed.title.trim().length > SELF_ROLE_MAX_TITLE || finalDescription(embed, mappings).length > SELF_ROLE_MAX_DESCRIPTION || embed.fields.length > SELF_ROLE_MAX_FIELDS) errors.push('limit');
    if (embed.fields.some(field => !field.name.trim() || !field.value.trim() || field.name.trim().length > SELF_ROLE_MAX_FIELD_NAME || field.value.trim().length > SELF_ROLE_MAX_FIELD_VALUE)) errors.push('field');
  }
  if (embedTextLength(embeds, mappings) > SELF_ROLE_MAX_TEXT) errors.push('total');
  return errors;
}
