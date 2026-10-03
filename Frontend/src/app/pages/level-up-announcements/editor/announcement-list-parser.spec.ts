import { LevelAnnouncementKind, LevelProgressScope, TemplateToken } from '../../../models/level-up-announcement.models';
import { parseAnnouncementList } from './announcement-list-parser';

const tokens: TemplateToken[] = [
  { name: 'user.mention', requiresRewardRole: false, requiresSeason: false },
  { name: 'rewardRole.name', requiresRewardRole: true, requiresSeason: false },
  { name: 'season.name', requiresRewardRole: false, requiresSeason: true },
];

describe('parseAnnouncementList', () => {
  it('parses an ungrouped multiline paste into Standard', () => {
    const result = parseAnnouncementList('  Hello {user.mention}!\r\n\r\nLevel {level}!');
    expect(result.groups[0]).toEqual(jasmine.objectContaining({ name: 'Standard', weight: 100, enabled: true }));
    expect(result.groups[0].messages.map(message => message.content)).toEqual(['Hello {user.mention}!', 'Level {level}!']);
    expect(result.diagnostics.some(diagnostic => diagnostic.severity === 'error')).toBeFalse();
  });

  it('parses groups, disabled entries and escaped slashes', () => {
    const result = parseAnnouncementList('// @group Hype | weight=3 | disabled\n// @off Hidden\n\\// starts with slashes\n\n// @group Selten | weight=1\nRare', { availableTokens: tokens });
    expect(result.groups.map(group => [group.name, group.weight, group.enabled])).toEqual([['Hype', 3, false], ['Selten', 1, true]]);
    expect(result.groups[0].messages.map(message => [message.content, message.enabled])).toEqual([['Hidden', false], ['// starts with slashes', true]]);
  });

  it('reports syntax, duplicate, length and context errors without dropping valid groups', () => {
    const result = parseAnnouncementList('// @group A | weight=0\n{rewardRole.name}\n// comment\n// @group A | weight=2\n' + 'x'.repeat(501), { availableTokens: tokens, scope: 'Lifetime' as LevelProgressScope, kind: 'LevelUp' as LevelAnnouncementKind });
    expect(result.groups.length).toBe(2);
    expect(result.diagnostics.map(diagnostic => diagnostic.code)).toEqual(jasmine.arrayContaining(['invalidWeight', 'rewardTokenRequiresRewardSet', 'unknownDirective', 'duplicateGroupName', 'messageTooLong']));
  });

  it('accepts unicode, consecutive directives and flags empty groups', () => {
    const result = parseAnnouncementList('// @group Grüße 🚀 | weight=10\n// @group Leer | weight=1\nEine Nachricht');
    expect(result.groups[0].messages).toHaveSize(0);
    expect(result.groups[1].messages[0].content).toBe('Eine Nachricht');
    expect(result.diagnostics.map(diagnostic => diagnostic.code)).toContain('emptyGroup');
  });
});
