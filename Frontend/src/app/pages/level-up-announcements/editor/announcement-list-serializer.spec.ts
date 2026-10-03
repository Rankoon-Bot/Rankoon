import { LevelAnnouncementGroup } from '../../../models/level-up-announcement.models';
import { parseAnnouncementList } from './announcement-list-parser';
import { serializeAnnouncementGroups } from './announcement-list-serializer';

const group = (overrides: Partial<LevelAnnouncementGroup> = {}): LevelAnnouncementGroup => ({
  id: 'group-id', name: 'Standard', enabled: true, weight: 60,
  conditions: { minimumLevel: null, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] },
  messages: [{ id: 'message-id', enabled: true, content: 'Hello' }, { id: 'off-id', enabled: false, content: 'Disabled' }], ...overrides,
});

describe('serializeAnnouncementGroups', () => {
  it('has stable canonical output and preserves disabled values', () => {
    const groups = [group(), group({ id: 'second', name: 'Selten', enabled: false, weight: 10, messages: [{ id: 'rare', enabled: true, content: '// starts with slash' }] })];
    const first = serializeAnnouncementGroups(groups);
    expect(first).toBe('// @group Standard | weight=60\nHello\n// @off Disabled\n\n// @group Selten | weight=10 | disabled\n\\// starts with slash');
    expect(serializeAnnouncementGroups(groups)).toBe(first);
  });

  it('round-trips the structured message content and state', () => {
    const source = serializeAnnouncementGroups([group()]);
    const parsed = parseAnnouncementList(source);
    expect(parsed.diagnostics.filter(diagnostic => diagnostic.severity === 'error')).toHaveSize(0);
    expect(parsed.groups[0].messages.map(message => [message.content, message.enabled])).toEqual([['Hello', true], ['Disabled', false]]);
  });
});
