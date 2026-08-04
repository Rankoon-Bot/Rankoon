import { LevelAnnouncementGroup } from '../../../models/level-up-announcement.models';

export function serializeAnnouncementGroups(groups: LevelAnnouncementGroup[]): string {
  return groups.map(group => {
    const disabled = group.enabled ? '' : ' | disabled';
    const messages = group.messages.map(message => {
      const content = message.content.trim();
      if (!message.enabled) return `// @off ${content}`;
      return content.startsWith('//') ? `\\${content}` : content;
    });
    return [`// @group ${group.name.trim()} | weight=${group.weight}${disabled}`, ...messages].join('\n');
  }).join('\n\n');
}
