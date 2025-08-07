# Rankoon Authentication System

This document describes the Discord OAuth2 authentication system implemented in Rankoon.

## Architecture Overview

The authentication system follows this flow:

1. **Client Request**: User clicks login button in frontend
2. **Login URL**: Frontend requests Discord OAuth URL from `/api/auth/login`
3. **Discord OAuth**: User is redirected to Discord authorization page
4. **Callback**: Discord redirects back to `/api/auth/callback` with authorization code
5. **Token Exchange**: Backend exchanges code for Discord access token
6. **User Data**: Backend fetches user info from Discord API
7. **JWT Generation**: Backend generates custom JWT tokens
8. **Frontend Redirect**: User is redirected to frontend with our JWT tokens

## API Endpoints

### GET /api/auth/login
Get Discord OAuth authorization URL.

**Query Parameters:**
- `returnUrl` (optional): URL to redirect to after successful authentication

**Response:**
```json
{
  "loginUrl": "https://discord.com/api/oauth2/authorize?client_id=..."
}
```

### GET /api/auth/callback
Discord OAuth callback endpoint (called by Discord, not frontend).

**Query Parameters:**
- `code`: Authorization code from Discord
- `state`: State parameter for CSRF protection

**Response:** Redirects to frontend with tokens in query parameters

### POST /api/auth/refresh
Refresh expired JWT tokens using refresh token.

**Request Body:**
```json
{
  "refreshToken": "your_refresh_token_here"
}
```

**Response:**
```json
{
  "accessToken": "new_jwt_token",
  "refreshToken": "new_refresh_token",
  "expiresAt": "2024-01-01T12:00:00Z",
  "user": {
    "id": "user_id",
    "discordId": "discord_snowflake",
    "username": "username",
    "displayName": "Display Name",
    "email": "user@example.com",
    "avatar": "avatar_hash",
    "verified": true
  }
}
```

### POST /api/auth/logout
Revoke refresh token (logout).

**Request Body:**
```json
{
  "refreshToken": "refresh_token_to_revoke"
}
```

**Response:**
```json
{
  "message": "Logged out successfully"
}
```

### GET /api/auth/me
Get current user information (requires JWT authentication).

**Headers:**
```
Authorization: Bearer your_jwt_token_here
```

**Response:**
```json
{
  "id": "user_id",
  "discordId": "discord_snowflake",
  "username": "username",
  "displayName": "Display Name",
  "email": "user@example.com",
  "avatar": "avatar_hash",
  "verified": true
}
```

## Configuration

### Environment Variables (.env file)

```env
# Discord OAuth Configuration
DISCORD_CLIENT_ID=your_discord_client_id_here
DISCORD_CLIENT_SECRET=your_discord_client_secret_here

# JWT Configuration
JWT_SECRET_KEY=your_super_secret_jwt_key_here_make_it_long_and_complex

# MongoDB Configuration
MONGODB_CONNECTION_STRING=mongodb://localhost:27017
MONGODB_DATABASE_NAME=rankoon

# Frontend Configuration
FRONTEND_BASE_URL=http://localhost:3000
```

### Discord Application Setup

1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Create a new application
3. Go to OAuth2 settings
4. Add redirect URI: `http://localhost:5020/api/auth/callback`
5. Copy Client ID and Client Secret to your .env file

### MongoDB Collections

The system creates two collections:

1. **discord_users**: Stores user information and Discord tokens
2. **refresh_tokens**: Stores JWT refresh tokens with metadata

## Security Features

- **CSRF Protection**: State parameter in OAuth flow
- **Token Expiration**: Access tokens expire after 1 hour, refresh tokens after 30 days
- **Token Revocation**: Refresh tokens can be revoked on logout
- **IP Tracking**: Refresh token usage is tracked by IP address
- **Secure Storage**: Discord tokens stored securely in MongoDB

## Frontend Integration

### Login Flow

```javascript
// 1. Get login URL
const response = await fetch('/api/auth/login?returnUrl=/dashboard');
const { loginUrl } = await response.json();

// 2. Redirect to Discord
window.location.href = loginUrl;

// 3. Handle callback (Discord redirects to frontend callback page)
// URL will be: /auth/callback?token=...&refresh_token=...&expires_at=...

// 4. Extract tokens from URL and store them
const urlParams = new URLSearchParams(window.location.search);
const accessToken = urlParams.get('token');
const refreshToken = urlParams.get('refresh_token');
const expiresAt = urlParams.get('expires_at');

localStorage.setItem('accessToken', accessToken);
localStorage.setItem('refreshToken', refreshToken);
localStorage.setItem('tokenExpiresAt', expiresAt);
```

### Making Authenticated Requests

```javascript
const token = localStorage.getItem('accessToken');

const response = await fetch('/api/auth/me', {
  headers: {
    'Authorization': `Bearer ${token}`
  }
});

if (response.status === 401) {
  // Token expired, try refresh
  await refreshToken();
}
```

### Token Refresh

```javascript
async function refreshToken() {
  const refreshToken = localStorage.getItem('refreshToken');
  
  const response = await fetch('/api/auth/refresh', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({ refreshToken })
  });

  if (response.ok) {
    const data = await response.json();
    localStorage.setItem('accessToken', data.accessToken);
    localStorage.setItem('refreshToken', data.refreshToken);
    localStorage.setItem('tokenExpiresAt', data.expiresAt);
    return true;
  } else {
    // Refresh failed, redirect to login
    localStorage.clear();
    window.location.href = '/login';
    return false;
  }
}
```

## Token Lifetimes

- **Discord Access Token**: Managed by Discord, typically expires after a few hours
- **Discord Refresh Token**: Long-lived, used to refresh Discord access token
- **Our JWT Access Token**: 1 hour (configurable)
- **Our JWT Refresh Token**: 30 days (configurable)

The system maintains the same lifetime as Discord tokens for the user session, while providing our own shorter-lived JWT tokens for API access.
