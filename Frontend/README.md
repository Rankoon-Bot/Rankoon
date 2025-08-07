# Rankoon Discord Bot Dashboard

Ein modernes Dashboard zur Verwaltung des Rankoon Discord Bots, entwickelt mit Angular 20 und Angular Signals.

## 🚀 Features

- **Discord OAuth2 Authentifizierung** - Sichere Anmeldung über Discord mit Backend-Token-System
- **Modernes Dashboard Design** - Intuitive Benutzeroberfläche mit responsivem Design
- **Angular Signals Store** - Moderne State Management Lösung
- **Modular aufgebaut** - Erweiterbar für verschiedene Bot-Module:
  - Server Konfiguration
  - Moderation (Automod, Warns, Bans)
  - Economy System
  - Fun & Games
  - Logs & Analytics

## � Authentifizierung

Das Dashboard nutzt ein modernes Auth-System:

1. **Frontend leitet zur Discord OAuth weiter** (Discord Callback geht ans Backend)
2. **Backend verarbeitet Discord Auth** und generiert eigenes JWT Token
3. **Backend redirected zur Frontend Callback-Seite** mit dem Token als Query-Parameter
4. **Frontend speichert und validiert das Backend-Token**

### Auth-Flow im Detail:

```
1. User klickt "Login" → Discord OAuth (redirect_uri = backend/auth/discord/callback)
2. Discord → Backend Callback mit authorization code
3. Backend → Tauscht code gegen Discord tokens, generiert eigenes JWT
4. Backend → Redirected zu frontend/auth/callback?token=jwt_token
5. Frontend → Validiert Token beim Backend und speichert es
```

## �🛠️ Installation

### 1. Repository klonen
```bash
git clone <repository-url>
cd Rankoon_Frontend
```

### 2. Dependencies installieren
```bash
npm install
```

### 3. Discord Application einrichten

1. Gehe zur [Discord Developer Portal](https://discord.com/developers/applications)
2. Erstelle eine neue Application oder nutze eine existierende
3. Notiere dir die `Client ID` (die `Client Secret` gehört nur ins Backend!)
4. **Wichtig**: Füge `http://localhost:3000/auth/discord/callback` (Backend URL) zu den OAuth2 Redirect URIs hinzu

### 4. Umgebungsvariablen konfigurieren

Kopiere `.env.example` zu `.env` und konfiguriere:

```env
# Discord OAuth Configuration
DISCORD_CLIENT_ID=deine_discord_client_id
# WICHTIG: Das Client Secret gehört NUR ins Backend! Niemals ins Frontend!

# Backend API Configuration
API_BASE_URL=http://localhost:3000/api
BACKEND_CALLBACK_URL=http://localhost:3000/auth/discord/callback
FRONTEND_CALLBACK_URL=http://localhost:4200/auth/callback
```

> ⚠️ **SICHERHEITSHINWEIS**: Das Discord Client Secret darf niemals im Frontend-Code stehen! Es gehört ausschließlich ins Backend und sollte als Umgebungsvariable gespeichert werden.

Alternativ bearbeite direkt die Environment-Dateien:
- `src/environments/environment.ts` (Development)
- `src/environments/environment.prod.ts` (Production)

### 5. Development Server starten

```bash
npm start
```

Die Anwendung ist dann unter `http://localhost:4200/` erreichbar.

## 🏗️ Architektur

### Store Management mit Angular Signals

Das Dashboard nutzt Angular Signals für das State Management:

- **AuthStore** - Verwaltung der Benutzerauthentifizierung
- **AppStore** - Verwaltung der Discord-Server und Bot-Konfigurationen

### Komponenten-Struktur

```
src/app/
├── layout/          # Layout-Komponenten (Header, Sidebar, Main-Layout)
├── pages/           # Seiten-Komponenten (Login, Dashboard, etc.)
├── services/        # Services (Auth, Discord API)
├── store/           # Signal-basierte Stores
├── guards/          # Route Guards
└── environments/    # Umgebungskonfigurationen
```

### Routing

Das Dashboard nutzt lazy loading für optimale Performance. Alle Routen sind durch Guards geschützt:

- **AuthGuard** - Schützt authentifizierte Bereiche
- **GuestGuard** - Verhindert Zugriff auf Login-Seite für authentifizierte Benutzer

## 🎨 Design System

Das Dashboard verwendet das Dark-First Rankoon Control Deck Design.

- **Verbindliche Regeln**: [`THEMING.md`](THEMING.md)
- **Tokens**: `src/styles/_tokens.scss`
- **Typography**: Inter mit System-Fallback
- **Responsive Design**: Mobile-first, mit Tastatur- und Kontrastanforderungen
- **Icons**: SVG-basierte Outline-Icons mit `currentColor`

Alle neuen oder geaenderten UI-Bausteine muessen die Token und Regeln aus `THEMING.md` verwenden. Die Anweisungen in [`AGENTS.md`](AGENTS.md) gelten ebenfalls fuer AI-gestuetzte Entwicklung.

## 🔧 Entwicklung

### Code Scaffolding

Neue Komponenten erstellen:
```bash
ng generate component component-name
```

Services erstellen:
```bash
ng generate service service-name
```

### Build

Production Build erstellen:
```bash
npm run build
```

### Tests

Unit Tests ausführen:
```bash
npm test
```

## 📝 Geplante Features

- [ ] Server-spezifische Konfigurationen
- [ ] Moderation Dashboard
- [ ] Economy System Verwaltung
- [ ] Custom Commands Editor
- [ ] Analytics & Reporting
- [ ] Real-time Updates via WebSockets
- [ ] Multi-Language Support

## 🤝 Contributing

1. Fork das Repository
2. Erstelle einen Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Committe deine Änderungen (`git commit -m 'Add some AmazingFeature'`)
4. Push zum Branch (`git push origin feature/AmazingFeature`)
5. Öffne einen Pull Request

## 📄 Lizenz

Dieses Projekt steht unter der [MIT Lizenz](LICENSE).

## 📞 Support

Bei Fragen oder Problemen erstelle gerne ein Issue im Repository oder kontaktiere das Entwicklerteam.

---

**Entwickelt mit ❤️ für die Rankoon Community**
