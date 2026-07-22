import { Injectable, effect, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthStore } from '../store/auth.store';
import { LeaderboardEntry, LeaderboardVisibility, LeaderboardWindow, LeaderboardWindowRequest, SeasonLeaderboardScope } from './guild.service';

// Retained for consumers outside the leaderboard page during the event-contract transition.
export interface LeaderboardEntryChanged {
  alias: string;
  scope: SeasonLeaderboardScope;
  seasonId: string | null;
  operation: 'upsert' | 'remove';
  userId: string;
  entry: LeaderboardEntry | null;
}
export interface LeaderboardChanged { alias: string; scope: SeasonLeaderboardScope; seasonId: string | null; visibility: LeaderboardVisibility; }

@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthStore);
  private readonly entryChangesSubject = new Subject<LeaderboardEntryChanged>();
  private readonly changesSubject = new Subject<LeaderboardChanged>();
  private readonly invalidationsSubject = new Subject<LeaderboardChanged>();
  private readonly accessRevokedSubject = new Subject<LeaderboardChanged>();
  private readonly connectionsSubject = new Subject<void>();
  private readonly subscriptions = new Map<string, { alias: string; scope: SeasonLeaderboardScope; seasonId?: string }>();
  private readonly activeSubscriptions = new Set<string>();
  private connection?: HubConnection;
  private starting?: Promise<void>;
  private lifecycle = Promise.resolve();
  private lastToken: string | null = null;

  readonly leaderboardEntryChanges$ = this.entryChangesSubject.asObservable();
  readonly leaderboardChanges$ = this.changesSubject.asObservable();
  readonly leaderboardInvalidations$ = this.invalidationsSubject.asObservable();
  readonly leaderboardAccessRevoked$ = this.accessRevokedSubject.asObservable();
  readonly leaderboardConnections$ = this.connectionsSubject.asObservable();

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
    if (this.subscriptions.has(key)) return;
    this.subscriptions.set(key, { alias, scope, seasonId });
    await this.serialize(async () => {
      await this.start();
      await this.subscribeActive(key);
    });
  }

  async unsubscribeLeaderboard(alias: string, scope: SeasonLeaderboardScope, seasonId?: string): Promise<void> {
    const key = this.key(alias, scope, seasonId);
    this.subscriptions.delete(key);
    await this.serialize(async () => {
      if (!this.activeSubscriptions.delete(key) || this.connection?.state !== 'Connected') return;
      await this.connection.invoke('Unsubscribe', alias, scope, seasonId);
    });
  }

  async getLeaderboardWindow(request: LeaderboardWindowRequest): Promise<LeaderboardWindow> {
    await this.start();
    if (this.connection?.state !== 'Connected') throw new Error('Leaderboard connection is unavailable.');
    return await this.connection.invoke<LeaderboardWindow>('GetWindow', request);
  }

  private async restart(): Promise<void> {
    const existing = this.connection;
    if (existing) {
      await existing.stop();
      if (this.connection === existing) {
        this.connection = undefined;
        this.starting = undefined;
        this.activeSubscriptions.clear();
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
    this.connection.on('leaderboardInvalidated', (event: LeaderboardChanged) => this.invalidationsSubject.next(event));
    this.connection.on('leaderboardAccessRevoked', (event: LeaderboardChanged) => this.accessRevokedSubject.next(event));
    this.connection.onreconnecting(() => this.activeSubscriptions.clear());
    this.connection.onreconnected(() => { void this.serialize(() => this.resubscribe()); });
    this.starting = this.connection.start().then(() => this.resubscribe()).finally(() => this.starting = undefined);
    return this.starting;
  }

  private async resubscribe(): Promise<void> {
    if (this.connection?.state !== 'Connected') return;
    for (const subscription of [...this.subscriptions.values()]) await this.subscribeActive(this.key(subscription.alias, subscription.scope, subscription.seasonId));
    this.connectionsSubject.next();
  }

  private async subscribeActive(key: string): Promise<void> {
    const subscription = this.subscriptions.get(key);
    if (!subscription || this.activeSubscriptions.has(key) || this.connection?.state !== 'Connected') return;
    try {
      await this.connection.invoke('Subscribe', subscription.alias, subscription.scope, subscription.seasonId);
      this.activeSubscriptions.add(key);
    } catch {
      // The HTTP request remains authoritative for the initial access decision.
      this.subscriptions.delete(key);
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
