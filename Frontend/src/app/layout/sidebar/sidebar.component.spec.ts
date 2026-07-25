import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BuildInfoService } from '../../services/build-info.service';
import { CustomBotIdentityAccessService } from '../../services/custom-bot-identity-access.service';
import { AppStore, Guild } from '../../store/app.store';
import { AuthStore } from '../../store/auth.store';
import { testI18n } from '../../testing/i18n-testing';
import { SidebarComponent } from './sidebar.component';

describe('SidebarComponent', () => {
  it('shows guild analytics and a separate operator section without guild errors', () => {
    TestBed.configureTestingModule({ imports: [SidebarComponent, testI18n], providers: [provideRouter([]), { provide: CustomBotIdentityAccessService, useValue: { load: () => {}, clear: () => {}, visible: signal(false) } }, { provide: BuildInfoService, useValue: { buildVersion: signal('test') } }] });
    const app = TestBed.inject(AppStore); const auth = TestBed.inject(AuthStore);
    const guild: Guild = { id: '1', name: 'Guild', icon: null, owner: false, permissions: '', features: [], botInstalled: true, inviteUrl: '' };
    app.setSelectedGuild(guild); app.setGuildCapabilities({ guildId: '1', isOwner: false, canAccessSettings: false, moduleIds: ['reporting'], leaderboardAlias: 'guild' });
    auth.setAuthData({ id: 'u', discordId: 'u', username: 'operator', displayName: 'Operator', avatar: '', isBotOperator: true }, 'token');
    const fixture = TestBed.createComponent(SidebarComponent); fixture.detectChanges();
    const routes = fixture.componentInstance.menuItems().flatMap(item => [item.route, ...(item.children ?? []).map(child => child.route)]);
    expect(routes).toContain('/analytics/overview'); expect(routes).toContain('/analytics/audit'); expect(routes).toContain('/bot-management/incidents'); expect(routes).not.toContain('/logs/errors');
    app.clearState(); auth.clearAuth();
  });
});
