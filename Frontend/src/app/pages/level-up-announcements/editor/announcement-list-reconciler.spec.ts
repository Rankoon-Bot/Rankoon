import { LevelAnnouncementGroup } from '../../../models/level-up-announcement.models';
import { parseAnnouncementList } from './announcement-list-parser';
import { reconcileAnnouncementGroups } from './announcement-list-reconciler';

const previous: LevelAnnouncementGroup[] = [{ id: 'group-a', name: 'Standard', enabled: true, weight: 60, conditions: { minimumLevel: null, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] }, messages: [{ id: 'message-a', enabled: true, content: 'Hello' }, { id: 'message-b', enabled: true, content: 'World' }] }, { id: 'group-b', name: 'Rare', enabled: true, weight: 10, conditions: { minimumLevel: null, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] }, messages: [{ id: 'message-c', enabled: true, content: 'Rare' }] }];
const ids = (() => { let number = 0; return () => `new-${++number}`; })();

describe('reconcileAnnouncementGroups', () => {
  it('keeps IDs through edits, renames, moves and deletions', () => {
    const parsed = parseAnnouncementList('// @group Rare | weight=20\nRare\n// @group Renamed | weight=60\nWorld\nNew');
    const result = reconcileAnnouncementGroups(parsed, previous, ids);
    expect(result.map(group => group.id)).toEqual(['group-b', 'group-a']);
    expect(result[0].messages[0].id).toBe('message-c');
    expect(result[1].messages.map(message => message.id)).toEqual(['message-b', 'new-1']);
  });

  it('matches duplicate message text without reusing one ID twice', () => {
    const duplicatePrevious = [{ ...previous[0], messages: [{ id: 'one', enabled: true, content: 'Same' }, { id: 'two', enabled: true, content: 'Same' }] }];
    const parsed = parseAnnouncementList('// @group Standard | weight=1\nSame\nSame');
    const result = reconcileAnnouncementGroups(parsed, duplicatePrevious, ids);
    expect(result[0].messages.map(message => message.id)).toEqual(['one', 'two']);
  });
});
