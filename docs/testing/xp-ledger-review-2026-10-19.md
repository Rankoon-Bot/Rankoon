# XP-Ledger-Prüfung vom 19. Oktober 2026

Historischer Prüfbericht: Ergebnisse, offene Nachweise und Umgebungsprobleme
beschreiben den damaligen Lauf, nicht den aktuellen CI- oder Deployment-Status.
Aktuelle Abläufe stehen in der [Entwicklungsanleitung](../development/README.md).

## Ergebnis und Aussagegrenze

Der XP-Pfad wurde von den Gateway-Eingängen über Cooldowns, Ledger und komprimierte
Voice-Aktivität bis zu Lifetime-/Season-Projektionen und Level-Ereignissen geprüft.
Die unten beschriebenen Fehler wurden im Arbeitsverzeichnis behoben.

Der letzte vollständig ausgeführte Release-Testlauf bestand mit **567 Tests,
0 Fehlern und 0 übersprungenen Tests**. Die Datenbanktests liefen gegen eine
isolierte MongoDB 8.0.12; das SDK war .NET 9.0.318.

Die inzwischen ergänzte regelmäßige Kontrollinstanz ist in
[XP-Integritätsprüfung](../backend/xp-integrity.md) dokumentiert. Der 567er-Lauf enthält auch
die zuvor blockierten Tests aller teilweise geschriebenen Projektionszustände.
Zwei danach ergänzte Integritätsfälle wurden erfolgreich kompiliert; Windows
Code Integrity blockiert derzeit aber das Laden der neuen Testassembly mit
`0x800711C7` (Ereignisse 3033/3077). Ein VSTest-Aufruf ohne entdeckte Tests ist kein
erfolgreicher Testnachweis, auch wenn sein Prozess Exitcode 0 liefert.

Es wurden keine Live-Daten gelesen, XP nachgetragen oder Deployments ausgelöst.
Die tatsächliche Ursache beim gemeldeten Discord-Nutzer ist daher noch nicht
abschließend bewiesen. Vorhandene Änderungen an den Discord-Kommandos wurden nicht
bearbeitet.

## Bezug zum gemeldeten Fehler

Die Meldung `Voice XP reconciliation did not complete successfully.` wurde vor
Commit `d063504c84aaa797d4cb378966bb3ea31917319f` geworfen, wenn der Abgleich
`false` zurückgab. Die darunterliegende Ausnahme wurde dabei verdeckt. Der
Screenshot zeigt den Vorfall am 3. Oktober um 15:36 Uhr; der Fix-Commit ist vom
3. Oktober um 15:43 Uhr, jeweils bei angenommener lokaler Anzeige in Berlin.

`GuildController.SaveXpConfig` gleicht die alten Einstellungen **vor** dem Speichern
ab. Ein Fehler an dieser Stelle verhindert das Speichern der neuen Einstellungen.
Er beweist allein weder einen fehlenden Ledger-Eintrag noch einen dauerhaften
Worker-Ausfall. Der genannte Commit verbessert Erstinitialisierung,
Ursachenerhalt und erneute Aktivierung. Ob er produktiv läuft, muss anhand der
tatsächlich gestarteten Image-/Commit-Version geprüft werden.

## Behobene Fehler

| Bereich | Vorheriges Problem | Änderung und Nachweis |
| --- | --- | --- |
| Ledger-Reparatur | Hintergrundreparatur konnte einen noch unentschiedenen Cooldown umgehen. | Entscheidung erfolgt innerhalb derselben beanspruchten Projektionsstrecke wie beim Gateway. Tests für Pending, Denied, Parallelität und Wiederholung. |
| Cooldown-Abbruch | Ein Retry konnte den Zeitstempel erneut verschieben; ein späteres Ereignis konnte den einzigen Nachweis einer zuvor reservierten Buchung überschreiben. | Gleicher Grant-Key wird erkannt; vor dem Überschreiben wird die frühere Reservierung am Ledger gesichert. |
| Verzögerte Buchungen | Verarbeitung am selben späteren Zeitpunkt konnte zwei zeitlich ausreichend getrennte Ereignisse gegenseitig sperren. | Cooldown verwendet den gespeicherten Ereigniszeitpunkt. Test mit nachgeholten Buchungen. |
| Projektions-Leases | Bei erneuter Konkurrenz konnte die Information über bereits teilweise ausgeführte Buchungen gelöscht werden. Der nächste Versuch konnte erneut inkrementieren. | Wiederherstellung behält Marker und Zielstand. Frische Konkurrenz ohne Schreibvorgang bleibt ein günstiger Pending-Retry. Ledger- und Voice-Regressionstests. |
| Voice-Checkpoint-CAS | Projektionsänderungen erhöhten die Dokumentrevision nicht durchgängig. Ein älterer Checkpoint konnte Projektionszähler zurückschreiben. | Sämtliche Projektionsmutationen erhöhen die CAS-Revision. Tests für veralteten Checkpoint und neue Aktivität während der Projektion. |
| Rank-up nach Abbruch | Nach der XP-Buchung fehlte beim Retry der ursprüngliche Vorher-Stand; Levelsprünge konnten verschwinden. | Lifetime- und Season-Übergänge werden vor den Projektionsschreibvorgängen gesichert und idempotent wiederverwendet. |
| Reaktions-Storno | Eine Entfernung konnte XP abziehen, während die ursprüngliche Buchung später am Cooldown scheiterte. | Storno wartet auf die Originalentscheidung. Abgelehnte Originale werden niemals abgezogen. |
| Storno-Identität | Die allgemeine Umkehrfunktion konnte andere Ledger-Arten umkehren oder einen anderweitig verwendeten Key akzeptieren. | Nur automatische Original-Grants; Key-Konflikte werden zurückgewiesen. Unique-Index begrenzt Umkehrungen pro Original. |
| Voice-Seasons | Auch abgesagte, geplante und pausierte Seasons wurden geladen; überlappende abgesagte Ersatzpläne konnten `SingleOrDefault` abbrechen lassen. Deaktivierte Seasons wurden nicht berücksichtigt. | Voice lädt nur aktive/abschließende Seasons bei aktivierter Season-Funktion. Projektionen verändern nur aktive/abschließende Seasons. |
| Fehlerisolation | Ein fehlerhaftes Mitglied oder ein Ledger-Eintrag konnte den Rest des Batches blockieren. Fehler beim Fehlerprotokollieren konnten Reparatur-Worker beenden. | Verarbeitung isoliert Fehler pro Objekt, setzt den Rest fort und meldet Degraded. Fehleraufzeichnung ist zusätzlich abgesichert. |
| Discord-Ausfall | Ein nicht auflösbarer Runtime-Kontext konnte als gesunder Durchlauf erscheinen. Alte Voice-Caches und Sitzungen konnten Ausfallzeit als XP zählen. | Verfügbarkeit wird pro Shard geprüft; Disconnects invalidieren die Beobachtungsepoche. Nach Neustart/Reconnect beginnen beobachtete Sitzungen neu. Persistierte Aktivität bleibt projektierbar. |
| Gateway-Eingänge | Kurze Verbindungs-/Timeoutfehler vor der dauerhaften Buchung verloren das Ereignis sofort. | Drei begrenzte Versuche bei transienten Mongo-/Timeoutfehlern, mit unverändertem Ereignis-Key. Permanente Fehler werden nicht wiederholt. |
| Ausschlüsse/Raten | Reaktionen, Thread-Erstellung und Event-Interesse beachteten Rollen/Kategorien/Elternkanäle uneinheitlich; Kanal-Multiplikatoren galten fast nur für Voice. | Gemeinsame getestete Eingangsverarbeitung und Ausschlussregeln; eigene Thread-Rate vor Elternrate; Channel- und Booster-Multiplikatoren kombinierbar. |
| Fehlende Socket-Caches | Reaktionen ohne gecachten Nutzer oder mit heruntergeladenem REST-Kanal konnten verworfen werden. | REST-Guild-Kanäle werden unterstützt; fehlende Mitgliedsdaten werden geladen. Dispatcher verwendet auch den Kanalcache zur Guild-Zuordnung. |
| Nachrichtenlänge | Nachrichten unter der konfigurierten Mindestlänge erhielten trotzdem Mindest-XP. | Unterhalb der Mindestlänge keine Buchung. Tests für Grenzen, Monotonie und Deckelung. |
| Legacy-Voice | Inkrementelle Ledger-Buchung pflegte Voice-XP und Server-Voice-Zähler nicht vollständig; Umkehrungen verloren die Zeitspanne. | Einheitliche Voice-Semantik, Zeitspanne und signierte Sekunden auch für Umkehrungen. |
| Voice-Migration | Unveränderte Slices berechneten den übergebenen historischen Award neu und konnten Präzision verlieren. | Originalbetrag bleibt erhalten; nur ein beschnittener Überlappungsrest wird neu berechnet. |
| Importe | Import konnte die XP-Basis zwischen Lesen und absoluter Wiederherstellung ändern und damit einen veralteten Gesamtstand verursachen. | Import und XP-Projektion teilen dieselbe Guild-Lease; begrenztes Warten, Erneuerung pro Chunk, sichere Wiederholung. |
| XP-Historie | Cooldown-abgelehnte Einträge sahen wie positive, angewandte XP-Buchungen aus. | XP-Historie und letzte XP-Aktivität schließen abgelehnte Buchungen aus. Die Originalentscheidungen bleiben zur internen Diagnose im Ledger erhalten. |
| Eingabevalidierung | Ungültige Identitäten, Keys, negative automatische Grants und ungültige Zeitspannen konnten erst nach dem Insert auffallen. | Validierung vor dauerhaften Schreibvorgängen; Umkehrungen haben ihren eigenen Pfad. |

## Testsuite

Neue Tests liegen in:

- `ActivityXpIngressTests.cs`: echte Source-to-Ledger-Verträge für Nachricht,
  Thread-Nachricht, Thread-Erstellung, Reaktion und Event-Interesse; deaktivierte
  Regeln, Bots, Ausschlüsse, Keys, Cooldowns, Nullraten und Multiplikatoren.
- `ActivityXpPolicyTests.cs`: Grenzen, Ausschlüsse und Vererbung von Kanalraten.
- `XpLedgerInvariantTests.cs`: Validierung, Scope-/Umkehrungssemantik, exakte
  Levelgrenzen bis Level 10.000 und mehrstufige Übergänge.
- `XpRecoveryTests.cs`: Batch-Isolation, Cancellation, begrenzte Eingangs-Retries,
  Voice-Mindestdauer, Unterbrechungen, Settings-Revisionen und Migration.
- `XpLedgerPersistenceTests.cs`: Unique-Indizes und echte MongoDB-CAS-Aufrufe;
  gleichzeitige gleiche/verschiedene Ereignisse, Cooldowns, Stornos, Imports,
  Lifetime/Season, neue Aktivität während Projektion, eingefrorene Seasons,
  überlappende abgesagte Seasons und verlorene Lease-Eigentümerschaft.
- Ergänzte bestehende Tests in `VoiceXpWatchdogTests.cs` und `XpProjectionTests.cs`.

Die zwei zuletzt hinzugefügten, noch nicht ausgeführten Tests sind:

- `Members_with_default_xp_settings_are_not_missed_by_the_sweep`
- `Falsely_applied_voice_days_are_returned_to_the_projection_pipeline_without_double_awards`

Die Tests der Zustände nach 0, 1, 2 und 3 abgeschlossenen Schreibvorgängen sind
mittlerweile erfolgreich ausgeführt. Die beiden offenen Integritätsfälle betreffen
die Serverauswahl und bereits gespeicherte, unvollständig projizierte Voice-XP.

### Lokal ausführen

Eine separate MongoDB bereitstellen, z. B. auf Port 27028. Die Tests verwenden
zufällige Datenbanknamen und löschen nur ihre eigenen Testdatenbanken wieder.

```powershell
$env:RANKOON_SEASON_TEST_MONGO = 'mongodb://127.0.0.1:27028'
dotnet test Backend/Backend.Tests/Backend.Tests.csproj --configuration Release `
  --logger 'trx;LogFileName=backend.trx' --results-directory artifacts/xp-tests
```

Ohne diese Umgebungsvariable laufen die Unit-Tests, die optionalen MongoDB-Tests
werden übersprungen. `.github/workflows/ci.yml` stellt jetzt MongoDB explizit
bereit und setzt die Variable, damit die Datenbankregressionen in CI mitlaufen.
Dieser geänderte CI-Workflow wurde hier nicht auf GitHub ausgeführt.

Für einen gezielten Lauf:

```powershell
dotnet test Backend/Backend.Tests/Backend.Tests.csproj --configuration Release `
  --filter 'FullyQualifiedName~Xp|FullyQualifiedName~Voice|FullyQualifiedName~Level|FullyQualifiedName~Season'
```

Ein Cobertura-Bericht kann zusätzlich mit
`--collect 'Code Coverage;Format=Cobertura'` erzeugt werden. Coverage ist kein
Nachweis eines echten Discord-End-to-End-Laufs. Socket-Adapter und laufende
Gateway-Orchestrierung benötigen zusätzlich den folgenden kontrollierten Test.

## Prüfung des betroffenen Live-Falls

1. Tatsächlich gestartete Bot-Version mit dem Fix `d063504` und diesem Arbeitsstand
   abgleichen. Das Datum eines Logeintrags beweist keine aktuelle Deployment-Version.
2. Guild- und User-ID bestimmen. Die XP-Audit-Seite zeigt Lifetime/Season sowie
   vorhandene XP-Aktivität; der Watchdog-Endpunkt zeigt Betriebszustand und
   berechtigte/ausgeschlossene Nutzer.
3. Mit `Backend/scripts/audit-xp.mongosh.js` eine **rein lesende** Diagnose durchführen:

   ```powershell
   $env:RANKOON_AUDIT_GUILD_ID = 'DISCORD_GUILD_ID'
   $env:RANKOON_AUDIT_USER_ID = 'DISCORD_USER_ID'
   mongosh $env:MONGODB_URI --quiet --file Backend/scripts/audit-xp.mongosh.js
   ```

   `MONGODB_URI` muss bereits die gewünschte Datenbank auswählen. IDs bleiben exakte
   Dezimalstrings. Das Skript schreibt und repariert nichts.
4. Settings prüfen: XP/Voice aktiv, Punkte pro Minute positiv, Mindestteilnehmer,
   Mindestdauer, AFK-/Mute-Regeln, ausgeschlossene Rollen/Kanäle/Kategorien,
   Kanalrate. Ein fehlendes gespeichertes XP-Settings-Dokument startet weiterhin
   keine implizite Voice-Vergabe.
5. `xp_ledger` und `voice_activity_days` getrennt beurteilen. Moderne Voice-XP
   liegen in komprimierten Tagesdaten. Während der Migration dürfen Legacy-Ledger
   und kopierte Voice-Daten nicht doppelt addiert werden.
6. Pending-Einträge, abgelaufene Leases und projizierte Voice-Revisionen prüfen.
   `cooldown_denied=true` bedeutet null vergebene XP, trotz gespeichertem
   angefragtem Betrag. `Pending` ist kein bestätigter Endstand.
7. `level_transition_events` prüfen: keine Schwelle überschritten, fehlendes
   Übergangsereignis, Rollenfehler und nicht versendete Ankündigung sind
   unterschiedliche Ursachen. XP-Bestand und Ankündigung getrennt prüfen.
8. Originalfehler zur Guild um den 3. Oktober suchen, insbesondere
   `voice.watchdog`, `voice.watchdog.member` und `xp.voice-activity-projection`.
   Der globale Wrapper aus dem Screenshot reicht für eine Ursachenzuordnung nicht.

### Kontrollierter Discord-Test nach Deployment

- Zwei menschliche Nutzer in einem erlaubten Voice-Kanal; mit Standardregeln
  mindestens 60 Sekunden zusammen bleiben. Die erste Minute muss inklusive der
  Qualifikationszeit 10 XP ergeben, danach 5 XP je vollständigem 30-Sekunden-Takt,
  bei unveränderten Standardraten und ohne Multiplikator.
- Einer verlässt den Kanal, wechselt in AFK oder erfüllt eine konfigurierte
  Ausschlussbedingung: Berechtigung und XP-Zuwachs müssen entsprechend wechseln.
- Mindestteilnehmer wieder herstellen; bei aktiviertem Reset muss die Mindestdauer
  erneut erreicht werden. Kein Award für die unberechtigte Lücke.
- Eine erlaubte Nachricht, eine zu kurze Nachricht und zwei Nachrichten innerhalb
  desselben Cooldowns senden. Nur berechtigte Aktivitäten zählen.
- Reaktion hinzufügen/entfernen; ausgeschlossenen Thread/Elternkanal prüfen.
- Bot geordnet neu starten und eine Discord-Unterbrechung testen: persistierte
  Aktivität bleibt erhalten, unbewiesene Ausfallzeit wird nicht nachträglich
  erfunden, neue beobachtete Aktivität startet wieder.
- Vor einer Levelgrenze XP-Stand, danach Übergangsereignis, Rolle und Ankündigung
  jeweils separat kontrollieren.

Bei 10 Voice-XP pro Minute erfordert Level 0 → 1 insgesamt 100 XP, also zehn
berechtigte Minuten. Level 50 → 51 benötigt bereits 15.100 weitere XP, also
25 Stunden und zehn Minuten. Häufige Anwesenheit oder eine alte Rank-up-Nachricht
allein beweist daher keinen XP-Ausfall.

## Verbleibende Systemgrenzen

- Gateway-Ereignisse, die Discord während einer Bot-Ausfallzeit nicht zustellt,
  können nicht verlässlich rekonstruiert werden. Kurze Retries ersetzen keine
  dauerhafte Discord-Event-Historie.
- Nach Verlust der Beobachtungskontinuität wird die Sitzung neu begonnen; der
  ungesicherte Rest seit dem letzten Checkpoint und die Ausfallzeit werden nicht
  geraten. Bereits gespeicherte Activity-Slices werden weiterhin repariert.
- Recovery ist eine mehrstufige, eventual konsistente Projektion. Ein kurzer
  Zwischenstand kann sichtbar sein. Leases und absolute Rekonstruktion schützen
  Wiederholungen; ein echter Last-/Failover-Test mit Produktionsvolumen und
  Netzwerkunterbrechungen bleibt zusätzlich erforderlich.
- Settings-Invalidierung und Discord-Autorität sind teilweise prozesslokal.
  Mehrere aktive Bot-/API-Instanzen benötigen verteilte Invalidation und eindeutig
  geklärte Gateway-Autorität; diese Arbeit führt kein Clusterprotokoll ein.
- Eine Discord-Nachricht und ihre Datenbankbestätigung sind keine gemeinsame
  Transaktion. Ein Absturz nach Versand, aber vor Bestätigung kann weiterhin eine
  doppelte Ankündigung verursachen. Das ist vom XP-Saldo getrennt.
- Historisch vollständig verlorene Ereignisse oder bereits falsch als Applied
  abgeschlossene Bestände werden nicht automatisch rückwirkend korrigiert.
  Dafür ist zunächst die lesende Diagnose mit konkreter Guild/User-ID erforderlich.
