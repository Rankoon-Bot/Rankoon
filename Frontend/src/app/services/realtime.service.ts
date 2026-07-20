import { Injectable, effect, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthStore } from '../store/auth.store';
import { LeaderboardEntry, LeaderboardVisibility, SeasonLeaderboardScope } from './guild.service';

export interface LeaderboardEntryChanged {
  alias: string;
  scope: SeasonLeaderboardScope;
  seasonId: string | null;
  operation: 'upsert' | 'remove';
  userId: string;
  entry: LeaderboardEntry | null;
}

export interface LeaderboardChanged { alias: string; visibility: LeaderboardVisibility; }

@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthStore);
  private readonly entryChangesSubject = new Subject<LeaderboardEntryChanged>();
  private readonly changesSubject = new Subject<LeaderboardChanged>();
  private readonly subscriptions = new Map<string, { alias: string; scope: SeasonLeaderboardScope; seasonId?: string }>();
  private connection?: HubConnection;
  private starting?: Promise<void>;
  private lifecycle = Promise.resolve();
  private lastToken: string | null = null;

  readonly leaderboardEntryChanges$ = this.entryChangesSubject.asObservable();
  readonly leaderboardChanges$ = this.changesSubject.asObservable();

  constructor() {
    effect(() => {
      const token = this.auth.token();
      if (token === this.lastToken) return;
      this.lastToken = token;
      void this.serialize(() => this.restart());
    });
  }

  async subscribeLeaderboard(alias: string, scope: SeasonLeaderboardScope, seasonId?: string): Promise<void> {
    const key = this.key(alias, scope, seasonId);
    this.subscriptions.set(key, { alias, scope, seasonId });
    await this.serialize(async () => {
      await this.start();
      try {
        await this.connection?.invoke('Subscribe', alias, scope, seasonId);
      } catch {
        // The HTTP request remains authoritative for the initial access decision.
        this.subscriptions.delete(key);
      }
    });
  }

  async unsubscribeLeaderboard(alias: string, scope: SeasonLeaderboardScope, seasonId?: string): Promise<void> {
    const key = this.key(alias, scope, seasonId);
    this.subscriptions.delete(key);
    await this.serialize(async () => {
      if (this.connection?.state === 'Connected') await this.connection.invoke('Unsubscribe', alias, scope, seasonId);
    });
  }

  private async restart(): Promise<void> {
    const existing = this.connection;
    if (existing) {
      await existing.stop();
      if (this.connection === existing) {
        this.connection = undefined;
        this.starting = undefined;
      }
    }
    if (this.subscriptions.size > 0) await this.start();
  }

  private async start(): Promise<void> {
    if (this.connection?.state === 'Connected') return;
    if (this.starting) return this.starting;
    this.connection = new HubConnectionBuilder()
      .withUrl(`${environment.apiBaseUrl.replace(/\/api$/, '')}/hubs/leaderboard`, { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.on('leaderboardEntryChanged', (event: LeaderboardEntryChanged) => this.entryChangesSubject.next(event));
    this.connection.on('leaderboardChanged', (event: LeaderboardChanged) => this.changesSubject.next(event));
    this.connection.on('leaderboardAccessRevoked', (event: LeaderboardChanged) => this.changesSubject.next(event));
    this.connection.onreconnected(() => { void this.serialize(() => this.resubscribe()); });
    this.starting = this.connection.start().then(() => this.resubscribe()).finally(() => this.starting = undefined);
    return this.starting;
  }

  private async resubscribe(): Promise<void> {
    if (this.connection?.state !== 'Connected') return;
    for (const subscription of [...this.subscriptions.values()]) {
      try {
        await this.connection.invoke('Subscribe', subscription.alias, subscription.scope, subscription.seasonId);
      } catch {
        this.subscriptions.delete(this.key(subscription.alias, subscription.scope, subscription.seasonId));
      }
    }
  }

  private key(alias: string, scope: SeasonLeaderboardScope, seasonId?: string): string {
    return `${alias}:${scope}:${seasonId ?? ''}`;
  }

  private serialize(operation: () => Promise<void>): Promise<void> {
    const next = this.lifecycle.then(operation, operation);
    this.lifecycle = next.catch(() => undefined);
    return next;
  }
}
