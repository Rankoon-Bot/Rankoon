import { TranslocoTestingModule } from '@jsverse/transloco';

export const testI18n = TranslocoTestingModule.forRoot({
  langs: {
    en: {
      app: { title: 'Rankoon Control Deck' },
      common: { retry: 'Try again', dismissNotification: 'Dismiss notification', notifications: 'Notifications' },
      errors: { generic: 'Something went wrong.', save: 'Could not save.', dashboardLoad: 'Could not load dashboard.', voiceHubsLoad: 'Could not load voice hubs.', voiceHubDelete: 'Could not delete voice hub.' },
      apiErrors: { request: { validationFailed: 'Please check your input.' }, xp: { settings: { messagePoints: '{{field}} has invalid XP values.' } } },
      voiceHubs: { createPlaceholder: 'Create voice channel', nameTemplateSuffix: "'s channel", loading: 'Loading voice hubs...' },
      domain: { reports: { names: { xp: { granted: 'XP awarded' } }, actions: { voice: 'Voice activity' }, outcomes: { succeeded: 'Succeeded', failed: 'Failed', rejected: 'Rejected' }, severities: { info: 'Info', warning: 'Warning', error: 'Error', critical: 'Critical' }, errorSources: { voice: { watchdog: 'Voice watchdog' } } }, watchdog: { healthy: 'Healthy', degraded: 'Degraded', stopped: 'Stopped' } },
      modules: {
        xp: { name: 'XP & Levels', description: 'Manage XP' },
        reporting: { name: 'Reports', description: 'View reports' }
      },
      rolePermissions: { saved: 'Role permissions saved.' }
    },
    de: {
      app: { title: 'Rankoon Kontrollzentrum' },
      common: { retry: 'Erneut versuchen', dismissNotification: 'Benachrichtigung schließen', notifications: 'Benachrichtigungen' },
      errors: { generic: 'Etwas ist schiefgelaufen.', save: 'Speichern fehlgeschlagen.', dashboardLoad: 'Dashboard konnte nicht geladen werden.', voiceHubsLoad: 'VC-Hubs konnten nicht geladen werden.', voiceHubDelete: 'VC-Hub konnte nicht gelöscht werden.' },
      apiErrors: { request: { validationFailed: 'Bitte prüfe deine Eingaben.' }, xp: { settings: { messagePoints: '{{field}} enthält ungültige XP-Werte.' } } },
      voiceHubs: { createPlaceholder: 'VC erstellen', nameTemplateSuffix: 's Kanal', loading: 'VC-Hubs werden geladen...' },
      domain: { reports: { names: { xp: { granted: 'XP vergeben' } }, actions: { voice: 'Voice-Aktivität' }, outcomes: { succeeded: 'Erfolgreich', failed: 'Fehlgeschlagen', rejected: 'Abgelehnt' }, severities: { info: 'Info', warning: 'Warnung', error: 'Fehler', critical: 'Kritisch' }, errorSources: { voice: { watchdog: 'Voice-Watchdog' } } }, watchdog: { healthy: 'Aktiv', degraded: 'Beeinträchtigt', stopped: 'Deaktiviert' } },
      modules: {
        xp: { name: 'XP & Level', description: 'XP verwalten' },
        reporting: { name: 'Berichte', description: 'Berichte ansehen' }
      },
      rolePermissions: { saved: 'Rollenberechtigungen gespeichert.' }
    }
  },
  translocoConfig: { availableLangs: ['en', 'de'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true
});
