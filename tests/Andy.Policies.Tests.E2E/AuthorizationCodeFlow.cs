// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Andy.Policies.Tests.E2E;

/// <summary>
/// Programmatic OAuth 2.0 Authorization Code + PKCE flow for end-to-end tests
/// that need a JWT representing a real user (not a service principal). Targets
/// andy-auth (#109).
///
/// The flow:
/// <list type="number">
///   <item>POST <c>/Account/TestLogin</c> with email/password — andy-auth's
///   Development-only login endpoint that bypasses anti-forgery so tests can
///   establish the <c>.AspNetCore.Identity.Application</c> session cookie
///   without scraping CSRF tokens out of an HTML form.</item>
///   <item>Generate PKCE pair (S256 challenge) — even though andy-auth treats
///   PKCE as optional, using it is the modern OAuth default and what real
///   andy-policies-web clients do.</item>
///   <item>GET <c>/connect/authorize</c> with the cookie → 302 to the
///   client's <c>redirect_uri</c> with <c>?code=...</c> in the query.</item>
///   <item>POST <c>/connect/token</c> exchanging the code + verifier for a
///   bearer access_token.</item>
/// </list>
///
/// The redirect_uri does not need to be reachable — andy-auth just emits a
/// 302 Location header. We intercept rather than follow.
/// </summary>
public sealed class AuthorizationCodeFlow : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _authBaseUrl;
    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly string _scope;

    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);

    public AuthorizationCodeFlow(string authBaseUrl, string clientId, string redirectUri, string scope)
    {
        _authBaseUrl = authBaseUrl.TrimEnd('/');
        _clientId = clientId;
        _redirectUri = redirectUri;
        _scope = scope;

        // Manual cookie management — andy-auth's session cookie ships with the
        // Secure flag (correct in production), which the built-in CookieContainer
        // would refuse to send over HTTP. Our compose stack exposes andy-auth
        // on plain HTTP for test simplicity, so we capture Set-Cookie ourselves
        // and ignore the Secure flag. Scoped to this fixture only.
        _http = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
        });
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var raw in values)
        {
            // "name=value; path=/; secure; ..." — we only need name=value for
            // replay; flags (path/secure/httponly/samesite) are irrelevant when
            // we control the request manually.
            var semi = raw.IndexOf(';');
            var nameValue = semi < 0 ? raw : raw[..semi];
            var eq = nameValue.IndexOf('=');
            if (eq <= 0) continue;
            var name = nameValue[..eq].Trim();
            var value = nameValue[(eq + 1)..].Trim();
            _cookies[name] = value;
        }
    }

    private void AttachCookies(HttpRequestMessage request)
    {
        if (_cookies.Count == 0) return;
        var header = string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        request.Headers.Add("Cookie", header);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Run the full flow and return the access_token.</summary>
    public async Task<string> AcquireUserAccessTokenAsync(string email, string password)
    {
        await TestLoginAsync(email, password);

        var (verifier, challenge) = GeneratePkcePair();
        var state = Guid.NewGuid().ToString("N");

        var code = await GetAuthorizationCodeAsync(challenge, state);
        return await ExchangeCodeForTokenAsync(code, verifier);
    }

    private async Task TestLoginAsync(string email, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_authBaseUrl}/Account/TestLogin")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("email", email),
                new KeyValuePair<string, string>("password", password),
            }),
        };

        var res = await _http.SendAsync(req);
        CaptureCookies(res);
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"andy-auth /Account/TestLogin returned {(int)res.StatusCode} {res.ReasonPhrase}: {body}");
        }
    }

    private async Task<string> GetAuthorizationCodeAsync(string codeChallenge, string state)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _clientId,
            ["redirect_uri"] = _redirectUri,
            ["scope"] = _scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        var query = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var req = new HttpRequestMessage(HttpMethod.Get, $"{_authBaseUrl}/connect/authorize?{query}");
        AttachCookies(req);
        var res = await _http.SendAsync(req);
        CaptureCookies(res);

        if (res.StatusCode != HttpStatusCode.Redirect && res.StatusCode != HttpStatusCode.Found)
        {
            var body = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Expected 302 from /connect/authorize, got {(int)res.StatusCode} {res.ReasonPhrase}. " +
                $"Body: {Truncate(body, 500)}");
        }

        var location = res.Headers.Location
            ?? throw new InvalidOperationException("/connect/authorize 302 had no Location header.");

        var code = ParseQueryParam(location.Query, "code")
            ?? throw new InvalidOperationException(
                $"/connect/authorize redirect did not include ?code=. Location: {location}");

        return code;
    }

    private static string? ParseQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return null;
    }

    private async Task<string> ExchangeCodeForTokenAsync(string code, string codeVerifier)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("code_verifier", codeVerifier),
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("redirect_uri", _redirectUri),
        });

        var res = await _http.PostAsync($"{_authBaseUrl}/connect/token", form);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"/connect/token returned {(int)res.StatusCode} {res.ReasonPhrase}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("/connect/token response did not include access_token.");
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        // RFC 7636 §4.1: 43-128 char URL-safe verifier.
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var verifier = Base64UrlEncode(bytes);

        // S256: challenge = BASE64URL(SHA256(verifier)).
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(hash);

        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
