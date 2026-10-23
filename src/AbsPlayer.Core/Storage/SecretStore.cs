using System.Runtime.InteropServices;
using Windows.Security.Credentials;

namespace AbsPlayer.Core.Storage;

/// <summary>Where sign-in tokens live. Never the settings file.</summary>
public interface ISecretStore
{
    string? Get(string key);
    /// <summary>Stores the value, or deletes it when null.</summary>
    void Set(string key, string? value);
}

/// <summary>Windows Credential Locker (shows up in Credential Manager under the resource name, "ABS Player" from before the rename, kept so sign-ins carry over).</summary>
public sealed class CredentialLockerStore(string resource = "ABS Player") : ISecretStore
{
    private readonly PasswordVault _vault = new();

    public string? Get(string key)
    {
        try
        {
            var cred = _vault.Retrieve(resource, key);
            cred.RetrievePassword();
            return cred.Password;
        }
        catch (COMException) { return null; } // Not found.
    }

    public void Set(string key, string? value)
    {
        try { _vault.Remove(_vault.Retrieve(resource, key)); }
        catch (COMException) { }
        if (value is not null) _vault.Add(new PasswordCredential(resource, key, value));
    }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key)
    {
        lock (_values) return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        lock (_values)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }
}
