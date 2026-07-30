import { TranslocoTestingModule } from '@jsverse/transloco';
import { SUPPORTED_LOCALES } from '../i18n/locale.service';

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
        leaderboard: { name: 'Leaderboard', description: 'View leaderboard' },
        'voice-hubs': { name: 'Voice hubs', description: 'Manage voice hubs' },
        analytics: { name: 'Analytics', description: 'View analytics' },
        'self-roles': { name: 'Self-roles', description: 'Manage self-roles' },
        'xp-audit': { name: 'XP audit', description: 'View XP history' },
        'xp-adjustments': { name: 'XP adjustments', description: 'Adjust XP' },
        'xp-announcements': { name: 'Announcements', description: 'Manage announcements' },
        diagnostics: { name: 'Diagnostics', description: 'View diagnostics' }
      },
      rolePermissions: { saved: 'Role permissions saved.' },
      leaderboard: { voiceTimeLabel: 'Voice time: {{duration}}', voiceMetadata: '{{duration}} voice' },
      duration: { compact: { month: '{{count}} mo', week: '{{count}} wk', day: '{{count}} d', hour: '{{count}} hr', minute: '{{count}} min', second: '{{count}} sec' }, full: { monthOne: '{{count}} month', monthOther: '{{count}} months', weekOne: '{{count}} week', weekOther: '{{count}} weeks', dayOne: '{{count}} day', dayOther: '{{count}} days', hourOne: '{{count}} hour', hourOther: '{{count}} hours', minuteOne: '{{count}} minute', minuteOther: '{{count}} minutes', secondOne: '{{count}} second', secondOther: '{{count}} seconds' } }
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
        leaderboard: { name: 'Rangliste', description: 'Rangliste ansehen' },
        'voice-hubs': { name: 'VC-Hubs', description: 'VC-Hubs verwalten' },
        analytics: { name: 'Analytics', description: 'Analytics ansehen' },
        'self-roles': { name: 'Selbstrollen', description: 'Selbstrollen verwalten' },
        'xp-audit': { name: 'XP-Audit', description: 'XP-Verlauf ansehen' },
        'xp-adjustments': { name: 'XP-Anpassungen', description: 'XP anpassen' },
        'xp-announcements': { name: 'Ankündigungen', description: 'Ankündigungen verwalten' },
        diagnostics: { name: 'Diagnose', description: 'Diagnose ansehen' }
      },
      rolePermissions: { saved: 'Rollenberechtigungen gespeichert.' },
      leaderboard: { voiceTimeLabel: 'Voice-Zeit: {{duration}}', voiceMetadata: '{{duration}} Voice' },
      duration: { compact: { month: '{{count}} Mon.', week: '{{count}} Wo.', day: '{{count}} T.', hour: '{{count}} Std.', minute: '{{count}} Min.', second: '{{count}} Sek.' }, full: { monthOne: '{{count}} Monat', monthOther: '{{count}} Monate', weekOne: '{{count}} Woche', weekOther: '{{count}} Wochen', dayOne: '{{count}} Tag', dayOther: '{{count}} Tage', hourOne: '{{count}} Stunde', hourOther: '{{count}} Stunden', minuteOne: '{{count}} Minute', minuteOther: '{{count}} Minuten', secondOne: '{{count}} Sekunde', secondOther: '{{count}} Sekunden' } }
    },
    es: { app: { title: 'Panel de control de Rankoon' } },
    pt: { app: { title: 'Plataforma de controlo Rankoon' } },
    fr: { app: { title: 'Centre de contrôle Rankoon' } },
    it: { app: { title: 'Pannello di controllo Rankoon' } },
  },
  translocoConfig: { availableLangs: [...SUPPORTED_LOCALES], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true
});
