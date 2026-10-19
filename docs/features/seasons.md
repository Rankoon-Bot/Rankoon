# Seasons bedienen und prüfen

Seasons ergänzen die Lifetime-Rangliste. Lifetime-XP werden durch Season-Aktionen nicht zurückgesetzt.

1. Seasons aktivieren. Bei der ersten Einrichtung werden ein monatlicher Zeitplan, die lokale Zeitzone und morgen um Mitternacht vorgeschlagen.
2. Zeitraum und XP-Regeln wählen. Die Vorschau zeigt zusätzliche Seasons; vorhandene Seasons behalten ihre gespeicherten Regeln.
3. Die Anzahl prüfen (standardmäßig **eine**) und **Speichern und Seasons anlegen** wählen. Im expliziten Modus speichert **Änderungen speichern** nur Einstellungen.

Geplante Seasons starten innerhalb ihres Zeitraums automatisch. Der Hintergrunddienst prüft jede Minute; Änderungen erscheinen in der geöffneten Ansicht spätestens bei der nächsten automatischen Aktualisierung (alle 30 Sekunden). Start ist inklusive, Ende exklusiv. Die Oberfläche zeigt Zeiten in der gewählten Zeitzone. Ungültige lokale Uhrzeiten bei der Zeitumstellung werden in der Eingabe abgelehnt.

Automatische Vorausplanung ist ausdrücklich optional. Ihr Limit zählt laufende und zukünftige Seasons zusammen (höchstens 24). Eine laufende Season kann nicht parallel zu einer anderen gestartet werden. Abbrechen schaltet die Vorausplanung aus; andere bereits geplante Seasons bleiben bestehen. Abgebrochene Zeiträume werden nicht stillschweigend erneut angelegt. Das direkte Löschen eines Entwurfs entfernt ihn dagegen vollständig aus dem Plan; eine spätere ausdrückliche Anlage kann den freigewordenen Zeitraum wieder verwenden.

**Wiederherstellen** plant zukünftige Seasons erneut ein oder setzt eine Season innerhalb ihres ursprünglichen Zeitraums mit ihren vorhandenen XP fort. Voraussetzung: kein überlappender belegter Zeitraum und kein endgültiger Abschluss. Die Vorausplanung bleibt ausgeschaltet. Ein endgültiger Abschluss lässt sich nicht rückgängig machen. Geplante Seasons werden direkt gelöscht, ohne vorherigen Abbruch. Erhaltene Verweise und XP-Daten verhindern das Löschen eines Entwurfs.

Geplante Seasons lassen sich über **Name und Zeitraum bearbeiten** direkt im Dialog anpassen. Der Dialog bietet für nachfolgende Entwürfe **Termine beibehalten**, **Termine verschieben** und **ebenfalls löschen**. Eine Vorschau zeigt alle betroffenen Einträge vorher und nachher. Jede Änderung an der Eingabe erfordert eine neue Vorschau; veraltete Vorschauen werden auch im Backend abgewiesen. Löschungen nummerieren automatisch benannte Entwürfe neu und entfernen den veralteten Datumszeiger. Jede Planänderung pausiert die automatische Vorausplanung. Für eine Einzelanlage kann der Zeitplan **Manuell** gewählt werden. Änderungen an laufenden oder abgeschlossenen Zeiträumen werden abgelehnt. Der öffentliche Verlauf begrenzt die öffentliche Rangliste, nicht die Verwaltungsliste.

## Sommerpause und andere Unterbrechungen

**Aussetzen** reserviert den Zeitraum einer geplanten Season als benannte Pause (z. B. „Sommerpause“). Dabei gibt es keine Season-XP und die Pause verbraucht keine Season-Nummer; Lifetime-XP laufen wie üblich weiter. Der Hintergrunddienst startet dort keine Season und die Vorschau belegt die Pause nicht mit einem neuen Entwurf. Nachfolgende Termine bleiben erhalten. **Wiederherstellen** wandelt die Pause vor ihrem Ende wieder in eine geplante Season um. Pausen lassen sich auch direkt löschen.

## Ausfallsicherheit

Planänderungen speichern vor der ersten Mutation einen vollständigen, serverbezogenen Auftrag mit Löschungen und den endgültigen Änderungen. Wiederholte Bestätigungen verwenden denselben Auftrag und erstellen keine gelöschten Einträge neu. Unterbrochene Änderungen werden vor weiteren Schreibzugriffen, Verwaltungslesezugriffen und im Hintergrund fertiggestellt.

Alle Änderungen und der Hintergrunddienst teilen eine erneuerte MongoDB-Sperre pro Server. Geht die Sperre verloren, wird die geschützte Arbeit abgebrochen. Der Anlageauftrag speichert Kandidaten und Regel-Snapshot vor der Anlage. Derselbe Auftrag kann sicher wiederholt werden; abgeschlossene Aufträge erstellen gelöschte Seasons nicht erneut. Unterbrochene Aufträge werden vor weiteren Änderungen und vom Hintergrunddienst fortgesetzt. Fehler eines Servers blockieren die anderen Server nicht.

## Regressionstests

- Frontend: `npm test -- --watch=false --browsers=ChromeHeadless --include='src/app/pages/season-config/**/*.spec.ts'` im Verzeichnis `Frontend`.
- Production-Build: `npm run build` im Verzeichnis `Frontend`.
- Backend: `dotnet test Backend/Backend.Tests/Backend.Tests.csproj`.
- MongoDB-Regressionstests: zusätzlich `RANKOON_SEASON_TEST_MONGO` auf eine isolierte Test-MongoDB setzen. Jeder Test verwendet eine eigene Datenbank und entfernt ausschließlich diese danach. Ohne diese Variable werden nur diese Integrationstests übersprungen.

Die projektweiten Bedien- und Entwicklungsgrundsätze stehen in den
[Beitragsrichtlinien](../development/contributing.md).
