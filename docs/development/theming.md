# Rankoon Theming Guide

Dieses Dokument ist die verbindliche Quelle fuer alle visuellen Frontend-Entscheidungen. Es gilt fuer Menschen und AI bei jeder neuen oder geaenderten UI.

Quellpfade in diesem Dokument sind relativ zu `Frontend/`. Die zentralen Tokens
liegen in [`Frontend/src/styles/_tokens.scss`](../../Frontend/src/styles/_tokens.scss).

## Markencharakter

Rankoon ist ein praezises Discord-Control-Deck: dunkel, technisch und ruhig. Die Oberflaeche vermittelt Kontrolle statt Dekoration. Tiefe Navy-Flaechen, feine blaue Lichtkanten und ein gezieltes Signalrot greifen den Rankoon-Charakter auf.

- Dark-first: Kein weisses App-Canvas und keine generischen Pastell- oder Violettverlaeufe.
- Signalrot ist ein Akzent, keine Flaechenfarbe. Es markiert primaere Handlungen, aktive Navigation und kritische Ereignisse.
- Blau kennzeichnet interaktive oder technische Informationen, Gruen ausschliesslich positive Zustaende.
- Polygone, Glow und Markenillustrationen bleiben subtil und duerfen Lesbarkeit oder Informationshierarchie nicht beeintraechtigen.

## Token-Regeln

- Verwende ausschliesslich semantische CSS-Custom-Properties aus `src/styles/_tokens.scss`.
- Neue Literalwerte fuer Farben, Schatten, Radien, Abstaende oder Animationen in Komponenten-SCSS sind verboten, ausser fuer berechnete Werte oder SVG-spezifische Attribute.
- Verwende Oberflaechen hierarchisch: `--rk-canvas` fuer Seiten, `--rk-surface-1` fuer Panels, `--rk-surface-2` fuer interaktive Elemente.
- Verwende `--rk-text-*`, `--rk-border-*` und `--rk-status-*` nach Bedeutung, nicht nach optischer Aehnlichkeit.
- Discord Blurple (`--rk-discord`) ist ausschliesslich fuer die Discord-Anmeldung und Discord-bezogene Kennzeichnungen vorgesehen.

## Layout und Typografie

- UI-Schrift: `Inter`; Fallback sind System-Sans-Serif-Schriften. Die Schrift wird ueber Google Fonts geladen.
- Ueberschriften sind kompakt, klar und maximal `700` gewichtet. Keine dekorativen Display-Schriften ohne vorhandenes Brand-Asset.
- Seiten nutzen maximal `1280px` Inhaltsbreite und `--rk-space-6` Standardabstand. Auf kleinen Bildschirmen gilt `--rk-space-4`.
- Nutze das 4px-Raster der `--rk-space-*` Tokens. Komponenten duerfen keine willkuerlichen Abstaende definieren.
- Panels haben `--rk-radius-lg`, einen feinen Rand und `--rk-shadow-panel`; schwebende, helle Karten sind nicht zulaessig.

## Komponenten

- Primaere Buttons: `--rk-brand`, weisse Schrift, ausschliesslich fuer die eine wichtigste Aktion einer Ansicht.
- Sekundaere Buttons: `--rk-surface-2` mit Rand. Destruktive Aktionen verwenden `--rk-danger` oder die zugehoerige dezente Oberflaeche.
- Eingaben verwenden dunkle Oberflaechen, einen sichtbaren Rand und den einheitlichen Fokus-Ring.
- Statusanzeigen kombinieren Text mit Farbe oder Icon. Farbe allein ist nie die einzige Bedeutungstraegerin.
- Icons nutzen eine einheitliche Outline-Sprache, `currentColor` und sind dekorativ nur mit `aria-hidden="true"`; informative Icons erhalten eine Textalternative.
- Empty States reservieren einen `mascot-slot`. Bis ein freigestelltes Rankoon-Asset existiert, wird dort keine generierte Rastergrafik eingesetzt.

### Gemeinsame Bausteine

- Aktionsbuttons und als Button dargestellte Links verwenden `rk-button`. Die
  Darstellung liegt zentral in `src/styles/_controls.scss`; Seiten definieren
  nur ihre Anordnung. Standardhoehe: `--rk-control-height` (44px), gemeinsame
  Schriftgroesse und Innenabstaende. Gleiche Hoehe bedeutet keine feste Breite:
  Beschriftungen duerfen auf schmalen Bildschirmen umbrechen.
- `rk-button--primary` kennzeichnet die wichtigste Aktion, `rk-button--secondary`
  eine Nebenaktion und `rk-button--danger` eine destruktive Aktion. Filter,
  Zeitraumwahl und andere ausgewaehlte Werte verwenden Informationsblau.
  `rk-button--discord` bleibt Discord-OAuth-Aktionen vorbehalten.
- `rk-button--icon` ist ein quadratisches 44px-Ziel. Verwende `rk-icon` aus
  `src/app/shared/ui/icon/` fuer Aktualisieren, Schliessen, Entfernen, Suche und
  Sortierpfeile. Das Icon ist dekorativ; der umgebende Button benoetigt sichtbaren
  Text oder eine uebersetzte `aria-label`. Keine Unicode-Ersatzzeichen fuer Icons.
- Datenladezustaende verwenden `rk-loading-skeleton` aus
  `src/app/shared/ui/loading-skeleton/`. Die Layouts `form`, `cards`, `table`,
  `chart` und `dashboard` reservieren dem Inhalt entsprechenden Platz. Es gibt
  eine vorgelesene Ladeinformation; die Platzhalter selbst sind dekorativ.
  Bei Hintergrundaktualisierungen von Datenansichten bleiben vorhandene Daten sichtbar. Spinner kennzeichnen
  kurze laufende Aktionen wie Speichern oder Anmelden.
- Seitenkoepfe verwenden `rk-page-header`, optional
  `rk-page-header__eyebrow` und `rk-page-header__description`. Geometrie und
  mobile Anordnung liegen in `src/styles/_page-layout.scss`. Der App-Shell
  gehoert der aeussere Seitenabstand; Seiten fuegen keinen zweiten Gutter hinzu.
- Panels verwenden `--rk-panel-padding`, das auf Mobilgeraeten automatisch
  kleiner wird. Arbeitsbereiche bleiben innerhalb von `--rk-page-max`.

### Textmenge

Eine kurze Einleitung je Seite reicht. Wiederhole weder Ueberschriften noch
offensichtliche Funktionen eines Feldes als Hilfetext. Erklaerungen zu Metriken
koennen in einer mit Tastatur bedienbaren `details`-Referenz stehen. Sichtbar
bleiben Validierung, Berechtigungen, Entwurfsstatus, Fehlermeldungen und die
Folgen kritischer Aktionen. Technische Codes gehoeren in Details, sofern sie
fuer eine Entscheidung nicht benoetigt werden.

## Interaktion und Accessibility

- Alle interaktiven Elemente besitzen sichtbare `:hover`, `:active`, `:focus-visible` und `:disabled` Zustaende.
- Der Fokus-Ring verwendet `--rk-focus-ring` und wird nie entfernt.
- Text- und Kontrollkontraste muessen mindestens WCAG AA erfuellen.
- Touch-Ziele sind mindestens `44px` hoch oder breit, sofern die Kontrolle nicht Teil einer dichten Datentabelle ist.
- Animationen dauern `120ms` bis `220ms`, bewegen Elemente nur geringfuegig und respektieren `prefers-reduced-motion`.
- Responsive Anpassungen erfolgen mobile-first. Navigation, Formulare und Tabellen muessen bei `768px` ohne horizontalen Viewport-Scroll funktionieren.

## Umsetzung und Review

Vor einem Merge pruefen:

- Keine neue harte Farb-, Schatten-, Radius-, Spacing- oder Transition-Literalwerte ausserhalb der Token-Datei.
- Keine generischen Violett-Blau-Verlaeufe oder weissen Standardkarten.
- Jede neue Kontrolle funktioniert mit Tastatur und sichtbarem Fokus.
- Desktop und Mobile sind fuer geaenderte Ansichten geprueft.
- Neue Bilder sind echte, versionierte Markenassets; Konzeptbilder oder generierte Bilder werden nicht ungeprueft als Produkt-UI verwendet.
