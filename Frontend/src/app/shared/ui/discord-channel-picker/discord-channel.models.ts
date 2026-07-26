export type DiscordChannelKind =
  | 'Text'
  | 'Voice'
  | 'Category'
  | 'Announcement'
  | 'Forum'
  | 'Stage'
  | 'Thread'
  | 'Unknown';

export interface DiscordChannelOption {
  id: string;
  name: string;
  kind: DiscordChannelKind;
  rawType: string;
  categoryId?: string | null;
  categoryName?: string | null;
  position?: number | null;
  disabled?: boolean;
}

export interface DiscordResourceChannel {
  id: string;
  name: string;
  type: string;
  categoryId?: string | null;
  categoryName?: string | null;
  position?: number | null;
}

export function normalizeDiscordChannelKind(rawType: string): DiscordChannelKind {
  const type = rawType.toLowerCase();
  // Rankoon treats Discord announcement/news channels as text destinations.
  if (type.includes('announcement') || type.includes('news')) return 'Text';
  if (type.includes('forum')) return 'Forum';
  if (type.includes('stage')) return 'Stage';
  if (type.includes('thread')) return 'Thread';
  if (type.includes('category')) return 'Category';
  if (type.includes('voice')) return 'Voice';
  if (type.includes('text')) return 'Text';
  return 'Unknown';
}

export function normalizeDiscordChannels(channels: readonly DiscordResourceChannel[]): DiscordChannelOption[] {
  return channels.map((channel) => ({
    ...channel,
    id: String(channel.id),
    categoryId: channel.categoryId == null ? channel.categoryId : String(channel.categoryId),
    rawType: channel.type,
    kind: normalizeDiscordChannelKind(channel.type),
  }));
}
