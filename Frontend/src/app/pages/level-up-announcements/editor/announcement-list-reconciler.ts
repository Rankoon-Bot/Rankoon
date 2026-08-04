import { LevelAnnouncementGroup, LevelAnnouncementMessage } from '../../../models/level-up-announcement.models';
import { ParsedAnnouncementDocument, ParsedAnnouncementGroup, ParsedAnnouncementMessage } from './announcement-list-parser';

export type AnnouncementIdFactory = () => string;

export function reconcileAnnouncementGroups(document: ParsedAnnouncementDocument, previous: LevelAnnouncementGroup[], createId: AnnouncementIdFactory): LevelAnnouncementGroup[] {
  const usedGroups = new Set<string>();
  return document.groups.map((parsed, index) => {
    const previousGroup = findGroup(parsed, index, previous, usedGroups);
    if (previousGroup) usedGroups.add(previousGroup.id);
    const messages = reconcileMessages(parsed, previousGroup?.messages ?? [], createId);
    return {
      id: previousGroup?.id ?? createId(),
      name: parsed.name,
      enabled: parsed.enabled,
      weight: parsed.weight,
      conditions: previousGroup?.conditions ?? { minimumLevel: null, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] },
      messages,
    };
  });
}

function findGroup(parsed: ParsedAnnouncementGroup, index: number, previous: LevelAnnouncementGroup[], used: Set<string>): LevelAnnouncementGroup | undefined {
  const normalized = parsed.name.trim().toLocaleLowerCase();
  return previous.find(group => !used.has(group.id) && group.name.trim().toLocaleLowerCase() === normalized)
    ?? (!used.has(previous[index]?.id ?? '') ? previous[index] : undefined)
    ?? previous.filter(group => !used.has(group.id)).sort((left, right) => messageOverlap(right, parsed) - messageOverlap(left, parsed))[0];
}

function messageOverlap(group: LevelAnnouncementGroup, parsed: ParsedAnnouncementGroup): number {
  const contents = new Set(parsed.messages.map(message => message.content.trim()));
  return group.messages.filter(message => contents.has(message.content.trim())).length;
}

function reconcileMessages(parsed: ParsedAnnouncementGroup, previous: LevelAnnouncementMessage[], createId: AnnouncementIdFactory): LevelAnnouncementMessage[] {
  const used = new Set<string>();
  return parsed.messages.map((message, index) => {
    const identical = previous.find(candidate => !used.has(candidate.id) && candidate.content.trim() === message.content && candidate.enabled === message.enabled);
    const positional = previous[index] && !used.has(previous[index].id) ? previous[index] : undefined;
    const match = identical ?? positional;
    if (match) used.add(match.id);
    return { id: match?.id ?? createId(), content: message.content, enabled: message.enabled };
  });
}
