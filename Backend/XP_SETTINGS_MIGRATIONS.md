# XP-Einstellungen über Updates erhalten

## Behobener Fehler

Die erste Ankündigungs-Konfiguration besaß kein `schema_version` und speicherte
`enabled`, `channel_id` und `templates` direkt im Dokument. Das spätere Modell
initialisierte die Version beim Deserialisieren trotzdem mit `2`. Dadurch wurden
Bestandsdokumente ohne Versionsfeld als aktuell behandelt: Die neuen Profile
erschienen leer und deaktiviert, obwohl die alten Einstellungen noch vorhanden
waren. Ein Speichern dieser leeren Ansicht konnte anschließend die alten Felder
entfernen. Die bisherige Umstellung im Controller war zudem nicht persistent und
erreichte den Bot ohne Dashboard-Aufruf nicht zuverlässig.

Das erklärt verlorene Ankündigungsprofile. Es belegt keine Löschung der separat
gespeicherten Rangrollen. Deren ursprüngliche Feldnamen sind weiterhin gültig.
Für einen konkreten Produktionsfall müssen die gespeicherten Dokumente und
gegebenenfalls vorhandene Datenbanksicherungen untersucht werden.

## Automatische Migration

`XpSettingsMigrationService.StartAsync` wird vor sämtlichen Hintergrunddiensten,
Discord-Runtimes und dem HTTP-Start abgewartet. Administratoren müssen die
Einstellungen weder öffnen noch erneut speichern.

- XP-Konfigurationen erhalten die dokumentweite Version `1`. Alte Voice-Regeln
  werden in die vorhandene Eligibility-Struktur übertragen. Rangrollen samt
  Beschreibungen, Kanal, XP-Raten, Ausschlüsse und Multiplikatoren bleiben erhalten.
- Ankündigungen ohne Version, mit Version `1` oder mit fälschlicher Version `2`
  ohne Profile werden anhand ihrer gespeicherten Struktur adaptiert.
- Aktivierung, Kanal, Sprache, Ping-Regel, manuelle Änderungen, Varianten,
  Gewichtungen, Prioritäten und Vorlagenbedingungen werden übernommen. Ein
  explizit deaktiviertes Profil bleibt deaktiviert; Seasons werden nicht aktiviert.
- Existierende neue Profile werden nicht durch alte Felder überschrieben. Das
  gilt auch für bewusst leere oder deaktivierte Profile.
- Ist nur der frühere `level_up_channel_id` konfiguriert und noch kein separates
  Ankündigungsdokument vorhanden, wird der Kanal als Lifetime-Ankündigung mit
  deutscher Standardvorlage übernommen. Bestehende Profile haben immer Vorrang.
- API, Integritätsprüfung und Zustellungsworker verwenden denselben persistenten
  Ladepfad. Eine neu auftauchende Legacy-Konfiguration benötigt keinen Neustart.

Unbekannte zukünftige Versionen oder nicht sicher interpretierbare Profile
stoppen die Migration und damit den Start. Sie werden nicht mit Standardwerten
überschrieben. Der Start kann nach Beheben des Daten-/Verbindungsfehlers sicher
wiederholt werden.

## Schutz vor Verlust und parallelen Änderungen

Vor jeder Migration und jedem ersetzenden Speichervorgang wird das vollständige
Originaldokument in `xp_settings_backups` abgelegt. Der Schlüssel enthält den
Collectionnamen, die Dokument-ID und einen SHA-256-Hash. Derselbe Stand wird auch
bei parallelen Starts oder nach einem Abbruch nur einmal gesichert. Scheitert die
Sicherung, wird das Original nicht verändert.

Der Schreibvergleich prüft das komplette Originaldokument, einschließlich
fehlender Felder. Gleichzeitige Änderungen führen zum erneuten Lesen bei einer
Migration oder zu HTTP 409 beim Speichern. Veraltete Browserstände überschreiben
keine neueren Konfigurationen. Fehlende Pflichtfelder werden bereits beim
Einlesen eines API-Auftrags abgelehnt, statt leere Standardwerte zu speichern.
Unbekannte Felder bleiben auch in verschachtelten Profilen und identifizierbaren
Listeneinträgen erhalten. Explizite Änderungen und das Entfernen von Rollen oder
Nachrichten bleiben möglich.

Die Originalfelder werden bei der Migration nicht entfernt. Das erleichtert die
Untersuchung, ist aber kein Freibrief für ein Downgrade auf alte Schreibpfade.
Die Sicherungssammlung gehört in das reguläre Datenbankbackup; sie hat keine TTL.

## Produktionsdiagnose und Wiederherstellung

`scripts/audit-xp-settings.mongosh.js` prüft mit einem lesenden Datenbankzugang
gezielt einen Server. `RANKOON_AUDIT_GUILD_ID` muss eine exakte Discord-ID als
Zeichenfolge enthalten. Das Skript gibt gespeicherte Einstellungen, Quellformat,
Rangrollen und verfügbare Sicherungsstände aus und führt keine Schreibzugriffe aus.

Noch vorhandene Legacy-Felder werden beim nächsten Start automatisch übernommen.
Bereits vor diesem Fix gelöschte/überschriebene Inhalte sind dadurch nicht
rekonstruierbar. Dafür ist ein älteres Datenbankbackup oder ein vorhandener
Sicherungsstand nötig. Wiederherstellungen müssen den betroffenen Server und
Dokumentstand gezielt berücksichtigen; nicht die komplette Produktionsdatenbank
zurücksetzen und nicht neuere bewusste Admin-Änderungen pauschal überschreiben.

Ein wiederhergestelltes Profil allein öffnet bereits mit
`CompletedWithoutAnnouncement` abgeschlossene Level-Ereignisse nicht erneut.
Diese Fälle müssen anhand der Zustellhistorie geprüft werden, bevor sie erneut
eingeplant werden; vorhandene Discord-Zustellnachweise dürfen nicht dupliziert
werden. Normale fehlende Ereignisse und fehlgeschlagene Zustellungen verarbeitet
weiterhin die XP-Integritätsprüfung.

## Regressionstests

`Backend.Tests/XpSettingsMigrationTests.cs` prüft echte historische BSON-Formate,
Profil- und Rollenübernahme, aktuelle deaktivierte Profile, zukünftige Versionen,
vollständige API-Aufträge und das Erhalten unbekannter Daten. MongoDB-Tests prüfen
parallele Starts/Speichervorgänge auch vor der Indexerstellung, Wiederanlauf nach
einer Sicherung und den Abbruch ohne Datenänderung bei einem Sicherungsfehler.
Die bestehende Backend-CI führt diese Tests mit MongoDB aus.
