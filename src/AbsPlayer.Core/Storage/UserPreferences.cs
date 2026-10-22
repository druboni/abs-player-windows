namespace AbsPlayer.Core.Storage;

/// <summary>Who is signed in, to which server. Tokens go to the secret store; the rest to settings.</summary>
public sealed class UserPreferences(SettingsStore settings, ISecretStore secrets)
{
    private static class Keys
    {
        public const string ServerUrl = "server_url";
        public const string Token = "auth_token";
        public const string RefreshToken = "refresh_token";
        public const string UserId = "user_id";
        public const string Username = "username";
        public const string DeviceId = "device_id";
    }

    private string? _cachedToken;

    /// <summary>Raised when sign-in state changes (sign in, sign out, token refreshed).</summary>
    public event EventHandler? Changed;

    public SettingsStore Settings => settings;

    public string? ServerUrl
    {
        get => settings.GetString(Keys.ServerUrl);
        set { settings.Set(Keys.ServerUrl, value); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public string? Token
    {
        get => _cachedToken ??= secrets.Get(Keys.Token);
        set
        {
            _cachedToken = value;
            secrets.Set(Keys.Token, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Present only on servers that issue short-lived access tokens (Audiobookshelf 2.26+).</summary>
    public string? RefreshToken
    {
        get => secrets.Get(Keys.RefreshToken);
        set => secrets.Set(Keys.RefreshToken, value);
    }

    public string? UserId
    {
        get => settings.GetString(Keys.UserId);
        set => settings.Set(Keys.UserId, value);
    }

    public string? Username
    {
        get => settings.GetString(Keys.Username);
        set => settings.Set(Keys.Username, value);
    }

    /// <summary>Stable per-install id so the server can tell this device's sessions apart.</summary>
    public string DeviceId
    {
        get
        {
            if (settings.GetString(Keys.DeviceId) is { } id) return id;
            id = Guid.NewGuid().ToString().ToUpperInvariant();
            settings.Set(Keys.DeviceId, id);
            return id;
        }
    }

    public bool IsLoggedIn => Token is not null && ServerUrl is not null;

    public void SaveCredentials(string serverUrl, string token, string? refreshToken, string userId, string username)
    {
        settings.Set(Keys.ServerUrl, serverUrl);
        RefreshToken = refreshToken;
        UserId = userId;
        Username = username;
        Token = token;
    }

    /// <summary>Keeps the server address so signing back in only needs a username and password.</summary>
    public void ClearCredentials()
    {
        RefreshToken = null;
        settings.Remove(Keys.UserId);
        settings.Remove(Keys.Username);
        Token = null;
    }
}
