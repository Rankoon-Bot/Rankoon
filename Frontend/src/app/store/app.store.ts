import { Injectable, signal, computed } from '@angular/core';
import { GuildCapabilities, GuildModuleId } from '../models/guild-permissions.models';

export type { GuildCapabilities, GuildModuleId } from '../models/guild-permissions.models';

export interface Guild {
  id: string;
  name: string;
  icon: string | null;
  owner: boolean;
  permissions: string;
  features: string[];
  botInstalled: boolean;
  inviteUrl: string;
}

export interface BotConfig {
  prefix: string;
  language: string;
  timezone: string;
  automod: {
    enabled: boolean;
    antiSpam: boolean;
    antiLinks: boolean;
    badWords: string[];
  };
  moderation: {
    logChannel: string | null;
    muteRole: string | null;
    autoRole: string | null;
  };
  economy: {
    enabled: boolean;
    currency: {
      name: string;
      symbol: string;
    };
    dailyAmount: number;
    workAmount: number;
  };
}

export interface AppState {
  selectedGuild: Guild | null;
  guilds: Guild[];
  guildCapabilities: GuildCapabilities | null;
  botConfig: BotConfig | null;
  isLoading: boolean;
  error: string | null;
}

@Injectable({
  providedIn: 'root'
})
export class AppStore {
  // Private writable signals
  private readonly _selectedGuild = signal<Guild | null>(null);
  private readonly _guilds = signal<Guild[]>([]);
  private readonly _guildCapabilities = signal<GuildCapabilities | null>(null);
  private readonly _botConfig = signal<BotConfig | null>(null);
  private readonly _isLoading = signal<boolean>(false);
  private readonly _error = signal<string | null>(null);

  // Public readonly signals
  readonly selectedGuild = this._selectedGuild.asReadonly();
  readonly guilds = this._guilds.asReadonly();
  readonly guildCapabilities = this._guildCapabilities.asReadonly();
  readonly botConfig = this._botConfig.asReadonly();
  readonly isLoading = this._isLoading.asReadonly();
  readonly error = this._error.asReadonly();

  // Computed properties
  readonly hasSelectedGuild = computed(() => this._selectedGuild() !== null);
  readonly hasGuilds = computed(() => this._guilds().length > 0);
  readonly hasError = computed(() => this._error() !== null);
  readonly isGuildOwner = computed(() => this._guildCapabilities()?.isOwner === true);
  readonly canAccessSettings = computed(() => this._guildCapabilities()?.canAccessSettings === true);

  constructor() {
    this.loadSelectedGuildFromStorage();
  }

  // Actions
  setSelectedGuild(guild: Guild | null): void {
    if (guild?.id !== this._selectedGuild()?.id) {
      this._guildCapabilities.set(null);
      this._botConfig.set(null);
    }
    this._selectedGuild.set(guild);
    this._error.set(null);
    this.saveSelectedGuildToStorage(guild);
  }

  setGuildCapabilities(capabilities: GuildCapabilities | null): void {
    if (capabilities && capabilities.guildId !== this._selectedGuild()?.id) return;
    this._guildCapabilities.set(capabilities);
  }

  hasModule(moduleId: GuildModuleId): boolean {
    const capabilities = this._guildCapabilities();
    return capabilities?.isOwner === true || capabilities?.moduleIds.includes(moduleId) === true;
  }

  setGuilds(guilds: Guild[]): void {
    this._guilds.set(guilds);
    const selectedGuild = this._selectedGuild();
    if (!selectedGuild) return;

    const currentGuild = guilds.find(guild => guild.id === selectedGuild.id && guild.botInstalled);
    this.setSelectedGuild(currentGuild ?? null);
  }

  addGuild(guild: Guild): void {
    this._guilds.update(guilds => [...guilds, guild]);
  }

  removeGuild(guildId: string): void {
    this._guilds.update(guilds => guilds.filter(g => g.id !== guildId));
  }

  setBotConfig(config: BotConfig | null): void {
    this._botConfig.set(config);
  }

  updateBotConfig(configUpdate: Partial<BotConfig>): void {
    this._botConfig.update(config => config ? { ...config, ...configUpdate } : null);
  }

  setLoading(isLoading: boolean): void {
    this._isLoading.set(isLoading);
  }

  setError(error: string | null): void {
    this._error.set(error);
  }

  clearState(): void {
    this._selectedGuild.set(null);
    this._guilds.set([]);
    this._guildCapabilities.set(null);
    this._botConfig.set(null);
    this._error.set(null);
    this.clearSelectedGuildFromStorage();
  }

  // Storage management for selected guild
  private loadSelectedGuildFromStorage(): void {
    if (typeof window !== 'undefined') {
      const storedGuild = sessionStorage.getItem('rankoon_selected_guild');
      if (storedGuild) {
        try {
          const guild = JSON.parse(storedGuild) as Guild;
          this._selectedGuild.set(guild);
        } catch (error) {
          console.error('Error parsing stored guild:', error);
          this.clearSelectedGuildFromStorage();
        }
      }
    }
  }

  private saveSelectedGuildToStorage(guild: Guild | null): void {
    if (typeof window !== 'undefined') {
      if (guild) {
        sessionStorage.setItem('rankoon_selected_guild', JSON.stringify(guild));
      } else {
        sessionStorage.removeItem('rankoon_selected_guild');
      }
    }
  }

  private clearSelectedGuildFromStorage(): void {
    if (typeof window !== 'undefined') {
      sessionStorage.removeItem('rankoon_selected_guild');
    }
  }

  // State getter for debugging or serialization
  getState(): AppState {
    return {
      selectedGuild: this._selectedGuild(),
      guilds: this._guilds(),
      guildCapabilities: this._guildCapabilities(),
      botConfig: this._botConfig(),
      isLoading: this._isLoading(),
      error: this._error()
    };
  }
}
