namespace TidalSqlApi;

using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>
/// Holds authenticated web sessions. Because <see cref="TidalSqlLib.TidalSql"/> is transient (a fresh
/// session per HTTP request), the login established when credentials are verified would otherwise be
/// lost. This singleton maps an opaque session token (delivered to the browser as an HttpOnly cookie)
/// to the authenticated login, so each request can resume that login without re-sending the password.
/// </summary>
public sealed class AuthTokenStore
{
    public const string CookieName = "pbsession";

    private readonly ConcurrentDictionary<string, AuthSession> sessions = new();

    public string Issue(string login, bool isSysadmin)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        this.sessions[token] = new AuthSession(login, isSysadmin);
        return token;
    }

    public bool TryGet(string? token, out AuthSession session)
    {
        session = default!;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return this.sessions.TryGetValue(token, out session!);
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            this.sessions.TryRemove(token, out _);
        }
    }
}

/// <summary>An authenticated web session: the login and whether it is a sysadmin.</summary>
public sealed record AuthSession(string Login, bool IsSysadmin);
