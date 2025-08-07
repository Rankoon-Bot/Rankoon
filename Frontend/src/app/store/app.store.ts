import { Injectable, signal, computed } from '@angular/core';

export interface Guild {
  id: string;
  name: string;
  icon: string | null;
  owner: boolean;
  permissions: string;
  features: string[];
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
  private readonly _botConfig = signal<BotConfig | null>(null);
  private readonly _isLoading = signal<boolean>(false);
  private readonly _error = signal<string | null>(null);

  // Public readonly signals
  readonly selectedGuild = this._selectedGuild.asReadonly();
  readonly guilds = this._guilds.asReadonly();
  readonly botConfig = this._botConfig.asReadonly();
  readonly isLoading = this._isLoading.asReadonly();
  readonly error = this._error.asReadonly();

  // Computed properties
  readonly hasSelectedGuild = computed(() => this._selectedGuild() !== null);
  readonly hasGuilds = computed(() => this._guilds().length > 0);
  readonly hasError = computed(() => this._error() !== null);

  // Actions
  setSelectedGuild(guild: Guild | null): void {
    this._selectedGuild.set(guild);
    this._error.set(null);
  }

  setGuilds(guilds: Guild[]): void {
    this._guilds.set(guilds);
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
    this._botConfig.set(null);
    this._error.set(null);
  }

  // State getter for debugging or serialization
  getState(): AppState {
    return {
      selectedGuild: this._selectedGuild(),
      guilds: this._guilds(),
      botConfig: this._botConfig(),
      isLoading: this._isLoading(),
      error: this._error()
    };
  }
}
