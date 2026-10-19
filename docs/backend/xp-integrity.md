# Regelmäßige XP- und Rangkontrolle

Rankoon hat zwei ergänzende Kontrollläufe:

| Kontrolle | Rhythmus | Aufgabe |
| --- | --- | --- |
| `level-role-repair` | Zehn Minuten nach dem vorherigen Lauf | Aktuelle Lifetime- und aktive Season-Rollen mit Discord abgleichen; fehlende Rollen erneut vergeben. |
| `xp-integrity` | Beim Start, anschließend 60 Minuten nach dem vorherigen Lauf | Dauerhafte XP-Quellen gegen Nutzerstände prüfen, nachholbare Projektionen korrigieren und fehlende Level-Zustellungen nachholen. |

Die normale XP-Vergabe und die schnelle Ledger-/Voice-Reparatur laufen unabhängig
davon weiter. Eine Ankündigung muss nicht bis zum nächsten Kontrolllauf warten.

## Was die stündliche Kontrolle tut

1. Aktuelle echte Mitglieder in begrenzten Seiten durchlaufen, auch ohne eigenes
   gespeichertes XP-Settings-Dokument. Ehemalige Mitglieder und Entwicklungsnutzer
   werden übersprungen.
2. Dieselbe Datenbank-Lease wie die XP-Projektion und der Import verwenden. Ein
   laufender oder unterbrochener Projektionsvorgang wird zunächst zurückgestellt.
3. Angewandte, nicht abgelehnte Ledger-Einträge und die maßgeblichen projizierten
   Voice-Daten summieren. Legacy-Voice-Kopien werden abhängig vom Migrationszustand
   ausgeschlossen, damit keine XP doppelt zählen. Pending-Voice-Zuwachs wird nicht
   vorzeitig ein zweites Mal gutgeschrieben.
4. Fehlende Earned-/Voice-XP und falsche abgeleitete Gesamtstände korrigieren,
   Lifetime und aktive Seasons getrennt. Importbasis und Season-Startbasis bleiben
   erhalten. Geschlossene Seasons bleiben eingefroren.
5. Fälschlich als Applied markierte Voice-Tage mit noch nicht projizierten Beträgen
   per CAS an die normale Voice-Projektion zurückgeben.
6. Bei unvollständiger oder widersprüchlicher Historie keine vorhandenen XP
   automatisch abziehen: als Betriebsfehler melden und den Fall zur Prüfung lassen.
7. Bereits vorhandene Level-Ereignisse prüfen. Fehlt ein berechtigter Aufstieg,
   genau ein zusammengefasstes Nachholereignis bis zum aktuellen Level anlegen.
   Stabile Ereignisschlüssel und der vorhandene Unique-Index verhindern erneute
   Erzeugung bei Neustarts und parallelen Läufen.
8. Endgültig fehlgeschlagene Zustellungen frühestens einmal pro Prüfintervall neu
   einplanen. Die bisherigen Versuche bleiben erhalten. Der normale Worker vergibt
   Rollen und stellt die Ankündigung anschließend zu.

Bereits versendete, laufende oder bewusst ohne Ankündigung abgeschlossene
Ereignisse werden nicht dupliziert. Eine gespeicherte Discord-Nachrichten-ID
verhindert erneutes Einplanen. Alte fehlgeschlagene Meldungen werden abgeschlossen,
wenn ein späterer mindestens gleich hoher Aufstieg bereits bestätigt zugestellt ist.
Aufstiege oberhalb des inzwischen tatsächlich erreichten Levels werden nicht erneut
angekündigt.

Importe und technische Migrationen erzeugen keine historischen Ankündigungen.
Manuelle XP-Korrekturen werden nur entsprechend der bestehenden Einstellung
`AnnounceManualAdjustments` berücksichtigt. Aktivierte Ankündigungsprofile,
Kanäle, Vorlagenbedingungen und Ping-Einstellungen bleiben maßgeblich.
Nachholereignisse verwenden die Quelle `xp_reconciliation`.

## Begrenzung und faire Wiederaufnahme

Pro Server und Prüflauf werden höchstens 20 neue oder erneut eingeplante
Zustellungen erzeugt. Mehrere fehlende Level eines Nutzers werden zusammengefasst.
Die geprüften XP-Stände werden auch nach Ausschöpfen dieses Zustelllimits weiter
kontrolliert.

Ein dauerhafter Cursor in `xp_integrity_sweep_cursors` setzt den nächsten Lauf bei
den folgenden Nutzern fort und läuft anschließend wieder vom Anfang durch.
Dauerhaft fehlschlagende frühe Nutzer können dadurch spätere Mitglieder nicht
verdrängen. Ein großer historischer Rückstand kann entsprechend mehrere Stunden
benötigen. Der Kontrolllauf verändert keine XP-Regeln und erfindet keine fehlenden
Discord-Ereignisse.

## Konfiguration

In `Backend/appsettings.json` ist die regelmäßige Kontrolle explizit
aktiviert:

```json
"XpIntegrity": {
  "Enabled": true,
  "CatchUpAnnouncements": true,
  "IntervalMinutes": 60,
  "BatchSize": 100,
  "MemberTimeoutSeconds": 90,
  "MaxAnnouncementsPerGuildPerRun": 20
}
```

- `XpIntegrity__Enabled=false` schaltet den neuen Kontrolllauf beim nächsten
  Prozessstart aus. Die bestehende normale XP-Verarbeitung und Rollenreparatur
  werden dadurch nicht abgeschaltet.
- `XpIntegrity__CatchUpAnnouncements=false` lässt die XP-Prüfung weiterlaufen,
  stoppt aber das neue Erzeugen und erneute Einplanen von Level-Zustellungen durch
  diesen Kontrolllauf. Bereits eingeplante normale Zustellungen bleiben bestehen.
- Gültige Grenzen: Intervall 5–1440 Minuten, Seitengröße 1–500, Timeout je Mitglied
  10–300 Sekunden und Zustelllimit 1–100. Ungültige Werte verhindern den Start mit
  einer Konfigurationsfehlermeldung.

## Betrieb und verbleibende Grenzen

Der bestehende Worker-Health-Mechanismus führt `xp-integrity` mit geprüften,
korrigierten, eingeplanten, zurückgestellten und fehlerhaften Fällen. Ein
minütlicher Heartbeat verhindert falsche Stale-Meldungen während der stündlichen
Wartezeit. Einzelkorrekturen und Nachholaufträge werden mit Guild/User/Ergebnis
protokolliert; widersprüchliche Quellen erscheinen als `xp.integrity`-Betriebsfehler.
Bekannte endgültige beziehungsweise ausgeschöpfte Zustellfehler halten den Lauf
auf Degraded, bis sie behoben oder abgeschlossen sind.

Fehlende Discord-Berechtigungen, gelöschte Rollen oder ein nicht erreichbarer
Ankündigungskanal benötigen weiterhin eine Korrektur am Server. Die Kontrolle
kann diese Berechtigungen nicht selbst erteilen; sie verhindert aber, dass eine
fehlgeschlagene Zustellung nach wenigen Versuchen endgültig vergessen wird.

Discord-Versand und Datenbankspeicherung sind keine gemeinsame Transaktion.
Ein Prozessabbruch nach erfolgreichem Versand, aber vor Speicherung der
Nachrichten-ID bleibt ein mögliches Doppelzustellungsfenster. Ein bestätigter
Versand wird durch die Kontrolle niemals bewusst erneut versendet.

Der Lauf aktualisiert Nutzer-/Season-XP und Zustellaufträge; er ersetzt keine
vollständige Rekonstruktion der aggregierten Analytics-/Serverstatistiken oder
einen Last-/Failover-Test der produktiven Discord-Verbindung.

## Tests

`XpIntegrityTests.cs` prüft Aktivierung, sichere Grenzen, stabile Schlüssel und
Schutz vor unbelegten XP-Abzügen. `XpIntegrityPersistenceTests.cs` prüft mit echter
MongoDB Korrekturen, Parallelität, Wiederanlauf, Stundenabstand bei fehlgeschlagenen
Zustellungen, abgeschlossene/unterdrückte Meldungen, Paging, Budgets, faire
Wiederaufnahme, Import-/Migrationsbasis, manuelle Ankündigungen und aktive Seasons.
Auch ein ausgefallener Fehler-Recorder darf eine Zustellung nicht ohne Retry
zurücklassen.

Ergebnisse und offene Nachweise des damaligen Prüflaufs stehen im
[XP-Ledger-Prüfbericht vom 19. Oktober 2026](../testing/xp-ledger-review-2026-10-19.md).
Die aktuellen Testbefehle und Voraussetzungen stehen in der
[Entwicklungsanleitung](../development/README.md#testing-and-building).
