import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppStore } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { LevelUpAnnouncementsComponent } from './level-up-announcements.component';

describe('LevelUpAnnouncementsComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [LevelUpAnnouncementsComponent, testI18n],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(AppStore).setSelectedGuild({ id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' });
  });

  it('requires a target channel before saving', () => {
    const component = TestBed.createComponent(LevelUpAnnouncementsComponent).componentInstance;
    component.settings.set({
      schemaVersion: 2,
      lifetime: { enabled: true, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, levelUp: { groups: [] }, rewards: { groups: [] } },
      season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    });

    expect(component.valid()).toBeFalse();
    component.save();
    TestBed.inject(HttpTestingController).expectNone(request => request.method === 'PUT');

    component.settings.update(settings => settings ? { ...settings, lifetime: { ...settings.lifetime, channelId: '42' } } : settings);
    expect(component.valid()).toBeTrue();
  });
});
