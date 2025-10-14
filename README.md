<section id="tokenrefresher-overview">
  <h1>TokenRefresher</h1>

  ![Status](https://img.shields.io/badge/status-stable-blue)
  ![Build](https://img.shields.io/badge/build-passing-brightgreen)
  ![License](https://img.shields.io/badge/license-MIT-lightgrey)
  
  <h2>Overview</h2>
  <p>
    <strong>TokenRefresher</strong> is a universal, dependency-free C# helper that keeps your access tokens valid --
    automatically refreshing them <em>before</em> they expire. It works with <strong>JWT</strong>, <strong>OAuth2</strong>,
    <strong>OpenID Connect</strong>, or any <strong>custom API token</strong> provider.
  </p>

  <p>
    The refresher caches your last token, adds smart proactive refreshing with jitter (to avoid stampedes),
    and guarantees <strong>single-flight refresh</strong> behavior -- meaning if multiple threads or requests ask for
    a token at the same time, only one actual refresh occurs, and everyone else waits for it to complete.
  </p>

  <h2>Developer Note</h2>
  <p>
    This project is part of a broader cleanup of my personal playground -- where I’m 
    organizing standalone mini-projects that demonstrate core programming concepts, 
    clean design, and practical problem-solving in small, focused doses.
  </p>

  <h2>Key Features</h2>
  <ul>
    <li><strong>Universal:</strong> works with any token type (JWT, OAuth2, custom)</li>
    <li><strong>Proactive Refresh:</strong> refreshes before expiry with configurable skew and jitter</li>
    <li><strong>Single-Flight:</strong> concurrent callers share one refresh task -- no race conditions</li>
    <li><strong>Thread-Safe:</strong> safely used across multiple async threads and requests</li>
    <li><strong>Lightweight:</strong> single-file, zero dependencies, pure C#</li>
  </ul>

  <h2>How It Works</h2>
  <p>
    You provide a delegate (<code>FetchTokenAsync</code>) that knows how to fetch a fresh token 
    from your API, auth service, or identity provider. The refresher wraps that logic with caching, 
    expiration tracking, and smart timing.
  </p>

  <p>
    When you call <code>GetValidTokenAsync()</code>, it returns a valid token from cache if possible, 
    or automatically refreshes if the current token is expired or near expiry.
  </p>

  <h2>Example Usage</h2>
  <pre>
  using Demo.Auth;

  // Example: JWT token from your API
  static async Task&lt;AccessToken&gt; FetchJwtAsync(CancellationToken ct)
  {
      using var http = new HttpClient();
      var res = await http.PostAsync("https://auth.example.com/token", null, ct);
      var json = await res.Content.ReadFromJsonAsync&lt;JsonElement&gt;(cancellationToken: ct);
      string token = json.GetProperty("access_token").GetString()!;
      int expiresIn = json.GetProperty("expires_in").GetInt32();
      var expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
      return new AccessToken(token, expiry);
  }

  // Create refresher
  var refresher = new TokenRefresher(
      fetchToken: FetchJwtAsync,
      refreshSkew: TimeSpan.FromSeconds(60),   // refresh 60s before expiry
      minimumRefreshInterval: TimeSpan.FromSeconds(5),
      jitterRatio: 0.2                          // +/- 20% random jitter
  );

  // Always get a valid token safely
  var token = await refresher.GetValidTokenAsync();
  http.DefaultRequestHeaders.Authorization = new("Bearer", token.Value);

  Console.WriteLine("Token valid until " + token.ExpiresAtUtc);
  </pre>

  <h2>Sample Console Output</h2>
  <pre>
  [INFO] Fetched new token: e3a8c1c0... valid until 2025-10-14T12:30Z
  [INFO] Reusing cached token (expires in 240s)
  [INFO] Near expiry (within skew), refreshing proactively...
  [INFO] Refreshed token: f5b92e31... valid until 2025-10-14T12:35Z
  </pre>

  <h2>Works with JWT too!</h2>
  <p>
    For JWTs, you can even extract the <code>exp</code> claim directly from the token payload:
  </p>

  <pre>
  static DateTimeOffset GetJwtExpiry(string jwt)
  {
      var parts = jwt.Split('.');
      if (parts.Length &lt; 2) throw new ArgumentException("Invalid JWT");
      var json = System.Text.Json.JsonDocument.Parse(
          System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=')))
      );
      var exp = json.RootElement.GetProperty("exp").GetInt64();
      return DateTimeOffset.FromUnixTimeSeconds(exp);
  }

  static async Task&lt;AccessToken&gt; FetchJwtAsync(CancellationToken ct)
  {
      var res = await http.PostAsync("https://auth.example.com/login", null, ct);
      var token = await res.Content.ReadAsStringAsync(ct);
      var expiry = GetJwtExpiry(token);
      return new AccessToken(token, expiry);
  }
  </pre>

  <h2>Works with Any Token Provider (Universal Example)</h2>
  <p>
    You can plug in any token source -- API key rotation, OAuth2 client credentials, 
    Firebase custom tokens, or even internal service-to-service tokens. 
    Just return the string and expiry time.
  </p>

  <pre>
  static async Task&lt;AccessToken&gt; FetchFromMyService(CancellationToken ct)
  {
      // Call your custom token endpoint
      var res = await http.PostAsync("https://internal.myservice.com/api/token", null, ct);
      var json = await res.Content.ReadFromJsonAsync&lt;JsonElement&gt;(cancellationToken: ct);

      string token = json.GetProperty("token").GetString()!;
      var expiry = DateTimeOffset.UtcNow.AddMinutes(10); // or read from response

      return new AccessToken(token, expiry);
  }

  var refresher = new TokenRefresher(FetchFromMyService);
  var token = await refresher.GetValidTokenAsync();

  client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Value}");
  </pre>

  <p>
    The refresher doesn’t care where your token comes from -- it simply ensures 
    you always get a fresh one before it expires.
  </p>

  <h2>How It Prevents Common Auth Bugs</h2>
  <ul>
    <li><strong>401 before expiry:</strong> handled by refreshing early (skew).</li>
    <li><strong>Concurrent refresh storms:</strong> only one refresh runs; others await it.</li>
    <li><strong>Clock skew issues:</strong> configurable early refresh window.</li>
    <li><strong>Unreliable auth servers:</strong> avoids repeated token hammering via cooldown.</li>
  </ul>

  <h2>Interface Summary</h2>
  <pre>
  public readonly struct AccessToken
  {
      public string Value { get; }
      public DateTimeOffset ExpiresAtUtc { get; }
  }

  public delegate Task&lt;AccessToken&gt; FetchTokenAsync(CancellationToken ct);

  public sealed class TokenRefresher
  {
      public TokenRefresher(FetchTokenAsync fetchToken,
                            TimeSpan? refreshSkew = null,
                            TimeSpan? minimumRefreshInterval = null,
                            double jitterRatio = 0.15);
      public Task&lt;AccessToken&gt; GetValidTokenAsync(CancellationToken ct = default);
  }
  </pre>

  <h2>Why TokenRefresher?</h2>
  <p>
    Many developers run into "401 Unauthorized" or "Token Expired" errors because their apps 
    don’t refresh tokens early enough -- or they trigger multiple simultaneous refreshes when 
    multiple requests hit an API at once. <strong>TokenRefresher</strong> solves both problems elegantly:
  </p>

  <pre>
  // Without TokenRefresher:
  if (DateTime.UtcNow &gt;= token.ExpiresAtUtc)
      token = await RefreshTokenAsync();

  // With TokenRefresher:
  var token = await refresher.GetValidTokenAsync();
  </pre>

  <p>
    You never have to track expiry manually, never worry about race conditions, 
    and never spam your auth endpoint again.
  </p>

  <section id="tech-stack">
    <h2>Tech Stack</h2>
    <pre>☑ C# (.NET 8 or newer)</pre>
    <pre>☑ Async/Await & Task coordination</pre>
    <pre>☑ No external dependencies</pre>
  </section>

  <h2>Build Status</h2>
  <p>
    This is a single-file demonstration repository and does not include a build pipeline.  
    Future updates may introduce automated tests and a CI workflow via GitHub Actions.
  </p>

  <h2>License</h2>
  <p>
    Licensed under the <a href="LICENSE">MIT License</a>.<br>
  </p>
</section>
