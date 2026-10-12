using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsPilot.Engine;

public enum TokenScope { ro, act, drive }

/// <summary>
/// Three-scope bearer tokens (design §13). Generated on first run, printed
/// once to the startup log, stored %LOCALAPPDATA%\WindowsPilot\tokens.json
/// (chmod-equivalent: current-user-only dir). ro is enough for GET/tree/
/// shots; act unlocks mutations (still needs ARMED); drive unlocks /ws/drive.
/// A stronger token implies the weaker ones (act ⊃ ro, drive ⊃ ro).
/// </summary>
public sealed class TokenStore(PilotSettings cfg, ILogger<TokenStore> log)
{
    Dictionary<string, TokenScope> _tokens = new();   // token -> scope

    public void LoadOrGenerate()
    {
        try
        {
            if (File.Exists(cfg.TokensFile))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cfg.TokensFile));
                if (d is not null)
                {
                    _tokens = d.ToDictionary(kv => kv.Key, kv => Enum.Parse<TokenScope>(kv.Value));
                    return;
                }
            }
        }
        catch { }

        _tokens = new Dictionary<string, TokenScope>
        {
            [New()] = TokenScope.ro,
            [New()] = TokenScope.act,
            [New()] = TokenScope.drive,
        };
        try
        {
            Directory.CreateDirectory(cfg.DataDir);
            File.WriteAllText(cfg.TokensFile,
                JsonSerializer.Serialize(_tokens.ToDictionary(kv => kv.Key, kv => kv.Value.ToString())));
        }
        catch { }

        log.LogWarning("WindowsPilot tokens (one-time print, also at {File}):", cfg.TokensFile);
        foreach (var (t, scope) in _tokens) log.LogWarning("  scope={Scope}: {Token}", scope, t);
    }

    static string New()
    {
        var b = RandomNumberGenerator.GetBytes(24);
        return "pilot_" + Convert.ToHexString(b).ToLowerInvariant();
    }

    public TokenScope? Validate(string? bearer)
    {
        if (string.IsNullOrEmpty(bearer)) return null;
        return _tokens.TryGetValue(bearer, out var s) ? s : null;
    }

    public bool Has(string? bearer, TokenScope need) => Validate(bearer) is { } s && Implies(s, need);

    static bool Implies(TokenScope have, TokenScope need) => need switch
    {
        TokenScope.ro => true,
        TokenScope.act => have != TokenScope.ro,
        TokenScope.drive => have == TokenScope.drive || have == TokenScope.act,
        _ => false,
    };

    public string Rotate(TokenScope scope)
    {
        var t = New();
        _tokens = _tokens.Where(kv => kv.Value != scope).ToDictionary();
        _tokens[t] = scope;
        try { File.WriteAllText(cfg.TokensFile,
                JsonSerializer.Serialize(_tokens.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()))); }
        catch { }
        return t;
    }
}
