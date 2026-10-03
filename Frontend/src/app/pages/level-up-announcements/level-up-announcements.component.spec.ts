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
       lifetime: { enabled: true, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
       season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    });

    expect(component.valid()).toBeFalse();
    component.save();
    TestBed.inject(HttpTestingController).expectNone(request => request.method === 'PUT');

    component.settings.update(settings => settings ? { ...settings, lifetime: { ...settings.lifetime, channelId: '42' } } : settings);
    expect(component.valid()).toBeTrue();
  });

  it('blocks saving when an optional level condition is below one', () => {
    const component = TestBed.createComponent(LevelUpAnnouncementsComponent).componentInstance;
    component.settings.set({
      schemaVersion: 2,
      lifetime: { enabled: true, channelId: '42', notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [{ id: 'group-1', name: 'Standard', enabled: true, weight: 1, conditions: { minimumLevel: 0, maximumLevel: null, everyNthLevel: null, exactLevels: [], sources: [] }, messages: [] }] }, rewards: { groups: [] } },
      season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    });

    expect(component.valid()).toBeFalse();
    component.save();
    TestBed.inject(HttpTestingController).expectNone(request => request.method === 'PUT');
  });

  it('accepts valid numeric condition values restored as strings', () => {
    const component = TestBed.createComponent(LevelUpAnnouncementsComponent).componentInstance;
    component.settings.set({
      schemaVersion: 2,
      lifetime: { enabled: true, channelId: '42', notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [{ id: 'group-1', name: 'Standard', enabled: true, weight: 1, conditions: { minimumLevel: '1' as unknown as number, maximumLevel: '20' as unknown as number, everyNthLevel: '1' as unknown as number, exactLevels: [], sources: [] }, messages: [] }] }, rewards: { groups: [] } },
      season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    });

    expect(component.valid()).toBeTrue();
  });

  it('updates the selected channel and sends the complete settings on save', () => {
    const fixture = TestBed.createComponent(LevelUpAnnouncementsComponent);
    const component = fixture.componentInstance;
    const settings = {
      schemaVersion: 2,
       lifetime: { enabled: true, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
       season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    };
    component.settings.set(settings);
    component.editorStates.set({});

    component.setChannel('42');
    expect(component.settings()?.lifetime.channelId).toBe('42');
    expect(component.dirty()).toBeTrue();
    component.save();

    const request = TestBed.inject(HttpTestingController).expectOne(request => request.method === 'PUT');
    expect(request.request.body.lifetime.channelId).toBe('42');
    request.flush({ ...settings, lifetime: { ...settings.lifetime, channelId: '42' }, revision: 2 });
  });

  it('connects the sticky Save Changes button to the save request', () => {
    const fixture = TestBed.createComponent(LevelUpAnnouncementsComponent);
    const component = fixture.componentInstance;
    const settings = {
      schemaVersion: 2,
       lifetime: { enabled: true, channelId: '42', notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
       season: { enabled: false, channelId: null, notifyUser: true, useDefaultFallback: true, fallbackLocale: 'en', announceManualAdjustments: false, avoidRecentMessagesPerUser: 0, avoidRecentMessagesPerGuild: 10, levelUp: { groups: [] }, rewards: { groups: [] } },
      revision: 1,
    };
    component.settings.set(settings);
    component.editorStates.set({});
    component.dirty.set(true);
    fixture.detectChanges();
    const loadRequest = TestBed.inject(HttpTestingController).expectOne(request => request.method === 'GET');
    loadRequest.flush({ settings, migrated: false, channelStatus: { lifetime: { exists: true, canSend: true }, season: { exists: false, canSend: false } } });
    component.settings.set(settings);
    component.editorStates.set({});
    component.dirty.set(true);
    component.loading.set(false);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.rk-button--primary') as HTMLButtonElement).click();

    const request = TestBed.inject(HttpTestingController).expectOne(request => request.method === 'PUT');
    expect(request.request.body.lifetime.channelId).toBe('42');
    request.flush({ ...settings, revision: 2 });
  });
});
