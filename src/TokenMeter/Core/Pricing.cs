namespace TokenMeter.Core;

/// <summary>
/// Per-million-token list prices, used only where a provider does not report cost itself.
/// Anything that does not match falls back to the closest family, then to zero.
/// </summary>
public static class Pricing
{
    public readonly record struct Rate(double In, double Out, double CacheRead, double CacheWrite);

    static readonly (string Match, Rate Rate)[] Table =
    [
        // Anthropic
        ("claude-opus-5",     new Rate(5.00, 25.00, 0.50, 6.25)),
        ("claude-opus-4",     new Rate(15.00, 75.00, 1.50, 18.75)),
        ("opus",              new Rate(5.00, 25.00, 0.50, 6.25)),
        ("claude-sonnet-5",   new Rate(3.00, 15.00, 0.30, 3.75)),
        ("sonnet",            new Rate(3.00, 15.00, 0.30, 3.75)),
        ("claude-haiku-4-5",  new Rate(1.00, 5.00, 0.10, 1.25)),
        ("haiku",             new Rate(0.80, 4.00, 0.08, 1.00)),
        ("fable",             new Rate(3.00, 15.00, 0.30, 3.75)),

        // OpenAI
        ("gpt-5.6",           new Rate(1.25, 10.00, 0.125, 0)),
        ("gpt-5.1",           new Rate(1.25, 10.00, 0.125, 0)),
        ("gpt-5-mini",        new Rate(0.25, 2.00, 0.025, 0)),
        ("gpt-5",             new Rate(1.25, 10.00, 0.125, 0)),
        ("o4-mini",           new Rate(1.10, 4.40, 0.275, 0)),
        ("gpt-4.1-mini",      new Rate(0.40, 1.60, 0.10, 0)),
        ("gpt-4.1",           new Rate(2.00, 8.00, 0.50, 0)),
        ("codex",             new Rate(1.25, 10.00, 0.125, 0)),

        // Google
        ("gemini-3-pro",      new Rate(2.00, 12.00, 0.20, 0)),
        ("gemini-3",          new Rate(2.00, 12.00, 0.20, 0)),
        ("gemini-2.5-pro",    new Rate(1.25, 10.00, 0.31, 0)),
        ("gemini-2.5-flash",  new Rate(0.30, 2.50, 0.075, 0)),
        ("gemini",            new Rate(1.25, 10.00, 0.31, 0)),

        // Others commonly wired into OpenCode
        ("grok",              new Rate(3.00, 15.00, 0.75, 0)),
        ("deepseek",          new Rate(0.56, 1.68, 0.07, 0)),
        ("qwen",              new Rate(0.40, 1.20, 0.04, 0)),
        ("kimi",              new Rate(0.60, 2.50, 0.15, 0)),
        ("llama",             new Rate(0.20, 0.60, 0.02, 0)),
        ("mistral",           new Rate(0.40, 2.00, 0.04, 0)),
    ];

    public static Rate For(string model)
    {
        var m = model.ToLowerInvariant();
        foreach (var (match, rate) in Table)
            if (m.Contains(match)) return rate;
        return default;
    }

    public static double Estimate(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        var r = For(model);
        return (input * r.In + output * r.Out + cacheRead * r.CacheRead + cacheWrite * r.CacheWrite) / 1_000_000d;
    }
}
