import { Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

@Component({
  selector: 'app-legal',
  standalone: true,
  imports: [RouterLink],
  template: `
    <article class="legal-page">
      <a class="back-link" routerLink="/">&larr; Zur Startseite</a>
      @if (isPrivacy) {
        <header><p class="overline">RANKOON / RECHTLICHES</p><h1>Datenschutzbestimmungen</h1><p class="intro">Informationen zur Verarbeitung personenbezogener Daten durch Rankoon.</p></header>
        <section><h2>1. Verantwortliche Stelle</h2><p>Bei selbst gehosteten Instanzen ist der jeweilige Betreiber der Rankoon-Instanz für die Datenverarbeitung verantwortlich. Serverbetreiber, die Rankoon einsetzen, bleiben für ihre eigene Community und die dort geltenden Regeln verantwortlich.</p></section>
        <section><h2>2. Verarbeitete Daten</h2><p>Rankoon verarbeitet Discord-Konto- und Serverdaten, soweit dies für die angebotenen Funktionen erforderlich ist. Dazu gehören Discord-Nutzer- und Server-IDs, Anzeigenamen, Avatare, Rollen- und Berechtigungsinformationen sowie Server- und Kanalnamen.</p><p>Für Leveling und Ranglisten werden Aktivitätsdaten wie Nachrichtenanzahl, Reaktionen, Thread-Aktivität, Interesse an geplanten Events, Voice-Verbindungsdauer, XP, Level und gegebenenfalls die öffentliche Sichtbarkeit eines Ranglisteneintrags gespeichert. Der Inhalt von Nachrichten wird nicht als dauerhafter Datensatz gespeichert; er wird nur verarbeitet, wenn die aktivierte XP-Regel eine Auswertung der Nachrichtenlänge erfordert.</p></section>
        <section><h2>3. Zwecke und Rechtsgrundlagen</h2><p>Die Datenverarbeitung dient dem Betrieb des Bots, der Berechnung von XP und Ranglisten, der Erstellung temporärer Voice-Kanäle, der Verwaltung von Rollenbelohnungen, der Absicherung des Dashboards und der Fehleranalyse. Die Verarbeitung erfolgt zur Bereitstellung der vom Serverbetreiber aktivierten Funktionen und auf Grundlage des berechtigten Interesses an einem funktionsfähigen Community-Service beziehungsweise einer erteilten Einwilligung, soweit diese erforderlich ist.</p></section>
        <section><h2>4. Anmeldung und Sicherheit</h2><p>Die Dashboard-Anmeldung erfolgt über Discord OAuth. Rankoon erhält die für die Anmeldung und Berechtigungsprüfung notwendigen Discord-Informationen und erstellt eigene Zugriffs- und Refresh-Tokens. Refresh-Tokens werden zur Sitzungsverwaltung gespeichert und sind standardmäßig bis zu 30 Tage gültig. Zugriffe auf das Dashboard und Änderungen an Einstellungen können in Aktivitätsberichten erfasst werden.</p></section>
        <section><h2>5. Speicherung und Löschung</h2><p>Konfigurations-, XP-, Ranglisten- und Voice-Daten werden gespeichert, solange der Bot auf dem Server genutzt wird oder sie für die jeweilige Funktion erforderlich sind. Aktivitäts-, Befehls- und Fehlerberichte werden nach 90 Tagen automatisch gelöscht. Bei Entfernung des Bots oder einem Löschersuchen entscheidet der verantwortliche Instanz- beziehungsweise Serverbetreiber über die Löschung der zugehörigen Daten.</p></section>
        <section><h2>6. Empfänger und Drittanbieter</h2><p>Discord verarbeitet Daten nach seinen eigenen Bedingungen und Datenschutzbestimmungen. Rankoon übermittelt Daten an Discord nur, soweit dies für die Discord-API und die Bot-Funktionen erforderlich ist. Die Daten werden in der vom Betreiber konfigurierten Datenbank gespeichert; eine Weitergabe zu Werbe- oder Verkaufszwecken findet nicht statt.</p></section>
        <section><h2>7. Deine Rechte</h2><p>Betroffene Personen können sich für Auskunft, Berichtigung, Löschung, Einschränkung der Verarbeitung oder Widerspruch an den Betreiber der jeweiligen Rankoon-Instanz wenden. Für Daten, die direkt durch Discord verarbeitet werden, gelten zusätzlich die Datenschutzinformationen von Discord.</p></section>
      } @else {
        <header><p class="overline">RANKOON / RECHTLICHES</p><h1>Nutzungsbedingungen</h1><p class="intro">Bedingungen für die Nutzung von Rankoon, dem Discord-Bot für Leveling und Community-Voice-Management.</p></header>
        <section><h2>1. Geltungsbereich</h2><p>Diese Bedingungen gelten für die Nutzung von Rankoon und dem zugehörigen Dashboard. Mit der Installation des Bots, der Anmeldung am Dashboard oder der Nutzung seiner Funktionen stimmst du diesen Bedingungen zu.</p></section>
        <section><h2>2. Discord-Konto und Berechtigungen</h2><p>Für die Dashboard-Nutzung ist ein Discord-Konto erforderlich. Die Anmeldung erfolgt über Discord. Administratoren und Serverbesitzer sind dafür verantwortlich, Rankoon nur auf Servern einzusetzen, für die sie die notwendigen Berechtigungen besitzen, und die vergebenen Dashboard-Zugriffe angemessen zu verwalten.</p></section>
        <section><h2>3. Zulässige Nutzung</h2><p>Rankoon darf nur im Einklang mit den Discord Terms of Service, den Discord Community Guidelines und allen anwendbaren Gesetzen genutzt werden. Es ist untersagt, den Bot zur Umgehung von Discord-Regeln, zur Belästigung, zur unbefugten Datenerhebung oder zur Störung des Dienstes einzusetzen.</p></section>
        <section><h2>4. Funktionen und Verfügbarkeit</h2><p>Rankoon bietet unter anderem XP- und Ranglistenfunktionen, temporäre Voice-Kanäle, Rollenbelohnungen sowie Berichte. Funktionen können sich ändern, eingeschränkt oder eingestellt werden. Für eine ununterbrochene Verfügbarkeit, die Richtigkeit von Statistiken oder den Erhalt von Daten wird keine Gewähr übernommen.</p></section>
        <section><h2>5. Verantwortlichkeit für Serverinhalte</h2><p>Serverbetreiber bleiben für ihre Server, Inhalte, Mitglieder und Konfigurationen verantwortlich. Rankoon ist kein Moderationsdienst und ersetzt keine eigene Administration oder Sicherung wichtiger Konfigurationsdaten.</p></section>
        <section><h2>6. Datenschutz</h2><p>Die Verarbeitung personenbezogener Daten richtet sich nach den <a routerLink="/privacy">Datenschutzbestimmungen</a>. Bei selbst gehosteten Instanzen ist deren Betreiber für die datenschutzrechtlichen Informationen und Anfragen verantwortlich.</p></section>
        <section><h2>7. Änderungen</h2><p>Diese Nutzungsbedingungen können angepasst werden, wenn dies aufgrund rechtlicher, technischer oder funktionaler Änderungen erforderlich ist. Die weitere Nutzung nach Veröffentlichung der aktualisierten Bedingungen gilt als Zustimmung.</p></section>
      }
    </article>
  `,
  styleUrl: './legal.component.scss',
})
export class LegalComponent {
  readonly isPrivacy = inject(ActivatedRoute).snapshot.data['page'] === 'privacy';
}
