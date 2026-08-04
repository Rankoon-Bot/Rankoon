import { LevelAnnouncementKind, LevelProgressScope, TemplateToken } from '../../../models/level-up-announcement.models';

export interface ParsedAnnouncementDocument {
  groups: ParsedAnnouncementGroup[];
  diagnostics: AnnouncementEditorDiagnostic[];
}

export interface ParsedAnnouncementGroup {
  sourceLine: number;
  name: string;
  weight: number;
  enabled: boolean;
  messages: ParsedAnnouncementMessage[];
}

export interface ParsedAnnouncementMessage {
  sourceLine: number;
  content: string;
  enabled: boolean;
}

export interface AnnouncementEditorDiagnostic {
  line: number;
  column?: number;
  severity: 'error' | 'warning';
  code: string;
  messageKey: string;
  params?: Record<string, string | number>;
}

export interface AnnouncementListParserOptions {
  availableTokens?: TemplateToken[];
  scope?: LevelProgressScope;
  kind?: LevelAnnouncementKind;
  maximumWeight?: number;
  maximumMessageLength?: number;
}

const GROUP = /^\s*\/\/\s*@group\s+(.+?)\s*\|\s*weight\s*=\s*(\d+)(?:\s*\|\s*disabled)?\s*$/i;
const GROUP_PREFIX = /^\s*\/\/\s*@group\b/i;
const OFF = /^\s*\/\/\s*@off(?:\s+(.*))?\s*$/i;
const COMMENT = /^\s*\/\//;
const TOKEN = /\{([^{}]+)\}/g;

export function parseAnnouncementList(text: string, options: AnnouncementListParserOptions = {}): ParsedAnnouncementDocument {
  const diagnostics: AnnouncementEditorDiagnostic[] = [];
  const groups: ParsedAnnouncementGroup[] = [];
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  let current: ParsedAnnouncementGroup | undefined;
  let implicit: ParsedAnnouncementGroup | undefined;
  const maximumWeight = options.maximumWeight ?? 10000;
  const maximumMessageLength = options.maximumMessageLength ?? 500;

  const ensureImplicitGroup = (line: number): ParsedAnnouncementGroup => {
    if (implicit) return implicit;
    implicit = { sourceLine: line, name: 'Standard', weight: 100, enabled: true, messages: [] };
    groups.unshift(implicit);
    current = implicit;
    return implicit;
  };

  lines.forEach((rawLine, index) => {
    const line = index + 1;
    const trimmed = rawLine.trim();
    if (!trimmed) return;

    if (GROUP.test(rawLine)) {
      const match = rawLine.match(GROUP)!;
      const name = match[1].trim();
      const weight = Number(match[2]);
      const enabled = !/\|\s*disabled\s*$/i.test(rawLine);
      if (!name) {
        diagnostics.push(error(line, 'emptyGroupName', 'levelUpAnnouncements.errors.emptyGroupName'));
        return;
      }
      if (weight < 1 || weight > maximumWeight) {
        diagnostics.push(error(line, 'invalidWeight', 'levelUpAnnouncements.errors.invalidWeight', { maximum: maximumWeight }));
      }
      current = { sourceLine: line, name, weight, enabled, messages: [] };
      groups.push(current);
      return;
    }

    if (GROUP_PREFIX.test(rawLine)) {
      diagnostics.push(error(line, 'invalidGroupDirective', 'levelUpAnnouncements.errors.invalidGroupDirective'));
      return;
    }

    const off = rawLine.match(OFF);
    if (off) {
      const content = (off[1] ?? '').trim();
      if (!content) {
        diagnostics.push(error(line, 'emptyOffMessage', 'levelUpAnnouncements.errors.emptyOffMessage'));
        return;
      }
      addMessage(current ?? ensureImplicitGroup(line), line, content, false, diagnostics, options);
      return;
    }

    if (COMMENT.test(rawLine)) {
      diagnostics.push(error(line, 'unknownDirective', 'levelUpAnnouncements.errors.unknownDirective'));
      return;
    }

    const escaped = /^\s*\\\/\//.test(rawLine);
    const content = (escaped ? rawLine.replace(/^\s*\\/, '') : rawLine).trim();
    addMessage(current ?? ensureImplicitGroup(line), line, content, true, diagnostics, options);
  });

  const names = new Map<string, number>();
  for (const group of groups) {
    const key = group.name.trim().toLocaleLowerCase();
    if (names.has(key)) diagnostics.push(error(group.sourceLine, 'duplicateGroupName', 'levelUpAnnouncements.errors.duplicateGroupName'));
    names.set(key, group.sourceLine);
    if (group.messages.length === 0) diagnostics.push(error(group.sourceLine, 'emptyGroup', 'levelUpAnnouncements.errors.emptyGroup'));
    if (group.enabled && !group.messages.some(message => message.enabled)) diagnostics.push(error(group.sourceLine, 'activeMessageRequired', 'levelUpAnnouncements.errors.activeMessageRequired'));
  }

  if (groups.length === 0 && lines.some(line => line.trim())) diagnostics.push(warning(1, 'noGroups', 'levelUpAnnouncements.warnings.noGroups'));
  return { groups, diagnostics };
}

function addMessage(group: ParsedAnnouncementGroup, line: number, content: string, enabled: boolean, diagnostics: AnnouncementEditorDiagnostic[], options: AnnouncementListParserOptions): void {
  if (!content) {
    diagnostics.push(error(line, 'emptyMessage', 'levelUpAnnouncements.errors.emptyMessage'));
    return;
  }
  const maximum = options.maximumMessageLength ?? 500;
  if (content.length > maximum) diagnostics.push(error(line, 'messageTooLong', 'levelUpAnnouncements.errors.messageTooLong', { maximum }));
  validateTokens(content, line, diagnostics, options);
  if (group.messages.some(message => message.enabled === enabled && message.content === content)) diagnostics.push(warning(line, 'duplicateMessage', 'levelUpAnnouncements.warnings.duplicateMessage'));
  group.messages.push({ sourceLine: line, content, enabled });
}

function validateTokens(content: string, line: number, diagnostics: AnnouncementEditorDiagnostic[], options: AnnouncementListParserOptions): void {
  if (!options.availableTokens) return;
  const tokens = new Map(options.availableTokens.map(token => [token.name, token]));
  for (const match of content.matchAll(TOKEN)) {
    const name = match[1];
    const token = tokens.get(name);
    if (!token) {
      diagnostics.push(error(line, 'unknownToken', 'levelUpAnnouncements.errors.unknownToken', { token: name }));
      continue;
    }
    if (token.requiresRewardRole && options.kind !== 'Reward') diagnostics.push(error(line, 'rewardTokenRequiresRewardSet', 'levelUpAnnouncements.errors.rewardTokenRequiresRewardSet'));
    if (token.requiresSeason && options.scope !== 'Season') diagnostics.push(error(line, 'seasonTokenRequiresSeasonScope', 'levelUpAnnouncements.errors.seasonTokenRequiresSeasonScope'));
  }
}

function error(line: number, code: string, messageKey: string, params?: Record<string, string | number>): AnnouncementEditorDiagnostic {
  return { line, severity: 'error', code, messageKey, params };
}

function warning(line: number, code: string, messageKey: string, params?: Record<string, string | number>): AnnouncementEditorDiagnostic {
  return { line, severity: 'warning', code, messageKey, params };
}
