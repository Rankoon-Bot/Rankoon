# Rankoon Authentication Flow Dokumentation

## Überblick

Das Rankoon-System implementiert eine zweistufige Token-Architektur:
1. **Discord OAuth Tokens** - für Discord API-Zugriff (gespeichert in der Datenbank)
2. **Eigene JWT Tokens** - für die Authentifizierung in der Rankoon-Anwendung

## Datenfluss

### 1. Login-Prozess

1. **Frontend** → `GET /api/auth/login?returnUrl=...`
2. **AuthController** → **AuthService.GetLoginUrl()**
3. **AuthService** → **DiscordService.GetAuthorizationUrl()**
4. **User wird zu Discord OAuth weitergeleitet**

### 2. OAuth Callback

1. **Discord** → `GET /api/auth/callback?code=...&state=...`
2. **AuthController** → **AuthService.HandleCallbackAsync()**
3. **AuthService** → **DiscordService.ExchangeCodeForTokenAsync()** (Discord Token holen)
4. **AuthService** → **DiscordService.GetUserInfoAsync()** (User-Daten von Discord)
5. **AuthService** → **DiscordService.CreateOrUpdateUserAsync()** (User + Discord Token in DB speichern)
6. **AuthService** → **JwtService.GenerateAccessToken()** (Eigenes JWT erstellen)
7. **AuthService** → Refresh Token in DB speichern
8. **User wird zum Frontend weitergeleitet mit unserem JWT Token**

### 3. API-Zugriff

1. **Frontend** sendet Requests mit unserem JWT Token im Authorization Header
2. **ASP.NET Core JWT Middleware** validiert das Token
3. **Controller** kann User-ID aus Claims extrahieren
4. **Services** können User-Daten aus DB laden

### 4. Discord API-Zugriff

Wenn du Discord API-Calls machen möchtest:
1. **Service** → User aus DB laden (enthält Discord Access Token)
2. **Service** → Discord API mit Discord Access Token aufrufen
3. Falls Discord Token abgelaufen → **DiscordService.RefreshTokenAsync()** verwenden

## Datenbank-Struktur

### DiscordUser Collection
```json
{
  "_id": "ObjectId",
  "discord_id": "Discord Snowflake ID",
  "username": "Discord Username", 
  "email": "user@example.com",
  "access_token": "Discord Access Token",
  "refresh_token": "Discord Refresh Token", 
  "token_expires_at": "DateTime",
  // ... weitere User-Daten
}
```

### RefreshToken Collection  
```json
{
  "_id": "ObjectId",
  "token": "Unser Refresh Token",
  "user_id": "ObjectId Verweis auf DiscordUser",
  "expires_at": "DateTime",
  "revoked": false
}
```

## Verwendung in Services

### User-Daten abrufen
```csharp
public class UserService
{
    private readonly RankoonDbContext _dbContext;
    
    public async Task<DiscordUser?> GetUserAsync(string userId)
    {
        return await _dbContext.DiscordUsers
            .Find(u => u.Id == userId)
            .FirstOrDefaultAsync();
    }
}
```

### Discord API aufrufen
```csharp
public class DiscordApiService  
{
    private readonly RankoonDbContext _dbContext;
    private readonly HttpClient _httpClient;
    
    public async Task<string> GetUserGuildsAsync(string userId)
    {
        var user = await _dbContext.DiscordUsers
            .Find(u => u.Id == userId)
            .FirstOrDefaultAsync();
            
        if (user?.AccessToken == null) return null;
        
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", user.AccessToken);
            
        var response = await _httpClient.GetAsync("https://discord.com/api/users/@me/guilds");
        return await response.Content.ReadAsStringAsync();
    }
}
```

## API Endpoints

### Authentication Endpoints

- `GET /api/auth/login?returnUrl=...` - Get Discord OAuth login URL
- `GET /api/auth/callback?code=...&state=...` - Handle Discord OAuth callback
- `POST /api/auth/refresh` - Refresh JWT tokens using refresh token
- `POST /api/auth/logout` - Logout user by revoking refresh token
- `GET /api/auth/me` - Get current user information (requires authentication)
- `GET /api/auth/verify` - Verify JWT token and return token info (requires authentication)
- `GET /api/auth/test` - Test endpoint to verify authentication is working

### Verify Endpoint Response

The `/api/auth/verify` endpoint returns the following format expected by the frontend:

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "user": {
    "id": "ObjectId",
    "discordId": "Discord Snowflake",
    "username": "Discord Username",
    "displayName": "Display Name",
    "email": "user@example.com",
    "avatar": "avatar_hash",
    "verified": true
  },
  "expiresAt": "2025-08-05T12:34:56.789Z"
}
```

## Sicherheitsaspekte

- **Discord Tokens** werden verschlüsselt in der Datenbank gespeichert
- **JWT Secret** muss in der .env Datei gesetzt werden
- **Refresh Tokens** haben eine begrenzte Lebensdauer
- **Discord Tokens** können über die Discord API erneuert werden

## Konfiguration

Alle Einstellungen werden über `appsettings.json` und `.env` Datei gesteuert:
- Discord OAuth Credentials in `.env`
- JWT Settings in `appsettings.json`
- MongoDB Connection in `appsettings.json` oder `.env`
