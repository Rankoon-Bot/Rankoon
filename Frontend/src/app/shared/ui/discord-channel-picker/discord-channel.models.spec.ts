import { normalizeDiscordChannelKind, normalizeDiscordChannels } from './discord-channel.models';

describe('Discord channel normalization', () => {
  it('normalizes every supported backend channel family centrally', () => {
    expect(normalizeDiscordChannelKind('GuildText')).toBe('Text');
    expect(normalizeDiscordChannelKind('GuildVoice')).toBe('Voice');
    expect(normalizeDiscordChannelKind('GuildCategory')).toBe('Category');
    expect(normalizeDiscordChannelKind('GuildAnnouncement')).toBe('Announcement');
    expect(normalizeDiscordChannelKind('GuildForum')).toBe('Forum');
    expect(normalizeDiscordChannelKind('GuildStageVoice')).toBe('Stage');
    expect(normalizeDiscordChannelKind('PublicThread')).toBe('Thread');
    expect(normalizeDiscordChannelKind('FutureType')).toBe('Unknown');
  });

  it('preserves resource metadata while adding normalized fields', () => {
    expect(normalizeDiscordChannels([{ id: '1', name: 'general', type: 'GuildText', categoryName: 'Community', position: 2 }])[0])
      .toEqual(jasmine.objectContaining({ id: '1', kind: 'Text', rawType: 'GuildText', categoryName: 'Community', position: 2 }));
  });
});
