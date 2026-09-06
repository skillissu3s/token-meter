using System.Text.Json;
using TokenMeter.Core;

namespace TokenMeter.Verify;

/// <summary>
/// End-to-end check of everything that can be exercised without a window: each collector against
/// fixture data it has never seen, the aggregation windows, settings round-tripping and migration,
/// calibration, and behaviour on corrupt input.
///
///   dotnet run --project tools/Verify
/// </summary>
static class Program
{
    static int _pass, _fail;

    static int Main()
    {
        var fixtures = Path.Combine(Path.GetTempPath(), "tokenmeter-verify-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            BuildFixtures(fixtures);
            PointCollectorsAt(fixtures);

            var settings = new Settings();
            var service = new UsageService();
            service.RefreshAsync(settings).GetAwaiter().GetResult();
            var providers = service.Latest.Providers.ToDictionary(p => p.Id);

            CheckCollectors(providers);
            CheckClaude(providers["claude"]);
            CheckCodex(providers["codex"]);
            CheckCodexFreePlan(fixtures);
            CheckWindows();
            CheckCalibration();
            CheckSettingsPatch();
            CheckSettings();
            CheckCorruptInput(fixtures, settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine("\nharness blew up: " + ex);
            _fail++;
        }
        finally
        {
            try { Directory.Delete(fixtures, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- checks

    static void CheckCollectors(Dictionary<string, ProviderSnapshot> p)
    {
        Section("Collectors");
        Check("four providers reported", p.Count == 4, p.Count.ToString());
        Check("claude reads its transcript", p["claude"].State == ProviderState.Ok, p["claude"].State.ToString());
        Check("codex reads its rollout", p["codex"].State == ProviderState.Ok, p["codex"].State.ToString());
        Check("absent opencode db reports NotInstalled",
            p["opencode"].State == ProviderState.NotInstalled, p["opencode"].State.ToString());
        Check("absent antigravity reports NotInstalled",
            p["antigravity"].State == ProviderState.NotInstalled, p["antigravity"].State.ToString());
    }

    const int ClaudeTurns = 6;
    const long PerTurn = 120 + 2400 + 180000 + 9000;

    static void CheckClaude(ProviderSnapshot c)
    {
        Section("Claude parsing");
        Check("replayed duplicate turn is collapsed",
            c.TokensTotal == ClaudeTurns * PerTurn, $"expected {ClaudeTurns * PerTurn}, got {c.TokensTotal}");
        Check("project name decoded from dashed folder",
            c.Table.Count > 0 && c.Table[0].Name == "demo-app",
            c.Table.Count > 0 ? c.Table[0].Name : "no rows");
        Check("two gauges", c.Gauges.Count == 2, c.Gauges.Count.ToString());
        Check("5-hour window has both ends",
            c.Gauges[0].WindowStartUtc is not null && c.Gauges[0].ResetsAtUtc is not null);
        Check("rolling weekly has no window start", c.Gauges[1].WindowStartUtc is null);
        Check("weekly refuses calibration on short history", !c.Gauges[1].Calibratable);
        Check("gauges are flagged as estimates",
            !c.Gauges[0].Authoritative && !c.Gauges[1].Authoritative);
        Check("weighted usage is non-zero", c.Gauges[0].Raw > 0, c.Gauges[0].Raw.ToString("0.00"));
        Check("thinking tokens parsed", c.Stats.Any(s => s.Sub is not null && s.Sub.Contains("thinking")));
    }

    static void CheckCodex(ProviderSnapshot x)
    {
        Section("Codex rate limits");
        Check("primary window is authoritative", x.Gauges[0].Authoritative);
        Check("primary takes the newest reading",
            Math.Abs(x.Gauges[0].Percent - 60) < 0.01, x.Gauges[0].Percent.ToString("0.##"));
        Check("secondary window is authoritative", x.Gauges[1].Authoritative);
        Check("window start derived from reset time", x.Gauges[0].WindowStartUtc is not null);
        // Four turns of 50k input of which 40k was cached, plus 3k output.
        Check("cached input separated from fresh",
            x.TokensTotal == 4 * (10_000 + 3_000 + 40_000), x.TokensTotal.ToString());
    }

    /// <summary>
    /// The shape a free plan actually returns: one 30-day primary window, a null secondary, and an
    /// absolute resets_at rather than a countdown. Getting this wrong produced two gauges both
    /// labelled "weekly window" and no reset time at all.
    /// </summary>
    static void CheckCodexFreePlan(string root)
    {
        Section("Codex, free plan shape");

        var alt = Path.Combine(root, "codex-free");
        var sessions = Path.Combine(alt, "sessions");
        Directory.CreateDirectory(sessions);

        var now = DateTimeOffset.UtcNow;
        var resetsAt = now.AddDays(11).ToUnixTimeSeconds();
        var lines = new List<string>
        {
            Json(new
            {
                timestamp = now.AddMinutes(-30).ToString("o"),
                type = "session_meta",
                payload = new { type = "session_meta", model = "gpt-5.6-terra", cwd = @"C:\code\demo" },
            }),
            Json(new
            {
                timestamp = now.AddMinutes(-20).ToString("o"),
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = 8_000, cached_input_tokens = 6_000,
                            output_tokens = 900, reasoning_output_tokens = 100,
                        },
                    },
                    rate_limits = new
                    {
                        limit_id = "codex",
                        primary = new { used_percent = 11.0, window_minutes = 43200, resets_at = resetsAt },
                        secondary = (object?)null,
                        plan_type = "free",
                    },
                },
            }),
        };
        File.WriteAllLines(Path.Combine(sessions, "rollout-free.jsonl"), lines);

        var previous = Environment.GetEnvironmentVariable("TOKENMETER_CODEX_DIR");
        Environment.SetEnvironmentVariable("TOKENMETER_CODEX_DIR", alt);
        try
        {
            var service = new UsageService();
            service.RefreshAsync(new Settings()).GetAwaiter().GetResult();
            var codex = service.Latest.Providers.First(p => p.Id == "codex");

            Check("collector reads the free-plan rollout", codex.State == ProviderState.Ok, codex.State.ToString());
            Check("no duplicate window labels",
                codex.Gauges.Select(g => g.Label).Distinct().Count() == codex.Gauges.Count,
                string.Join(" | ", codex.Gauges.Select(g => g.Label)));
            Check("30 days is not called weekly",
                codex.Gauges.Any(g => g.Label == "30-day window"),
                string.Join(" | ", codex.Gauges.Select(g => g.Label)));
            Check("absolute resets_at is honoured",
                codex.Gauges[0].ResetsAtUtc is not null &&
                Math.Abs((codex.Gauges[0].ResetsAtUtc!.Value - now.AddDays(11).UtcDateTime).TotalMinutes) < 2,
                codex.Gauges[0].ResetsAtUtc?.ToString("o") ?? "null");
            Check("window start derived from an absolute reset",
                codex.Gauges[0].WindowStartUtc is not null);
            Check("null secondary does not become a gauge",
                codex.Gauges.Count(g => g.Authoritative) == 1,
                codex.Gauges.Count(g => g.Authoritative).ToString());
            Check("a local 5-hour estimate fills the missing session view",
                codex.Gauges.Any(g => !g.Authoritative && g.Label == "5-hour window"));
            Check("plan type surfaces as the account label", codex.Account == "free", codex.Account ?? "null");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOKENMETER_CODEX_DIR", previous);
        }
    }

    static void CheckWindows()
    {
        Section("Window anchoring");
        var now = DateTime.UtcNow;
        var records = new List<UsageRecord>
        {
            Rec(now.AddHours(-30)),   // an old block, long expired
            Rec(now.AddHours(-2)),    // opens the current block
            Rec(now.AddMinutes(-10)),
        };

        var (start, end) = Aggregate.CurrentBlock(records, TimeSpan.FromHours(5));
        Check("current block is open now", start <= now && end > now, $"{start:o} .. {end:o}");
        Check("block is exactly five hours", Math.Abs((end - start).TotalHours - 5) < 0.001);
        Check("block excludes the expired one", start > now.AddHours(-6), start.ToString("o"));
        Check("block includes recent usage",
            records.Count(r => r.TsUtc >= start) == 2,
            records.Count(r => r.TsUtc >= start).ToString());

        var empty = Aggregate.CurrentBlock([], TimeSpan.FromHours(5));
        Check("no records still yields a sane window", empty.End > empty.Start);

        static UsageRecord Rec(DateTime ts) => new()
        {
            TsUtc = ts, Provider = "claude", Model = "claude-opus-5", Input = 100, Output = 100,
        };
    }

    static void CheckCalibration()
    {
        Section("Calibration");
        var g = new Gauge { Raw = 90, Calibratable = true };

        Check("solves the budget an observation implies",
            Calibration.Solve(g, 75) == 120, Calibration.Solve(g, 75)?.ToString() ?? "null");
        Check("a full window solves to the usage itself", Calibration.Solve(g, 100) == 90);
        Check("rejects zero", Calibration.Solve(g, 0) is null);
        Check("rejects above 100", Calibration.Solve(g, 101) is null);
        Check("rejects negative", Calibration.Solve(g, -5) is null);
        Check("refuses a gauge marked uncalibratable",
            Calibration.Solve(new Gauge { Raw = 90, Calibratable = false }, 50) is null);
        Check("refuses when nothing has been measured",
            Calibration.Solve(new Gauge { Raw = 0, Calibratable = true }, 50) is null);

        // The point of the feature: after calibrating, the gauge reads what was observed.
        var solved = Calibration.Solve(g, 84)!.Value;
        Check("calibrated budget reproduces the observation",
            Math.Abs(Fmt.Pct(g.Raw, solved) - 84) < 0.05, Fmt.Pct(g.Raw, solved).ToString("0.###"));
    }

    /// <summary>
    /// The payload below is the one the settings page actually posts, captured from the running
    /// page. This is the seam between the UI and the host, so it is checked against the real shape
    /// rather than something hand-invented.
    /// </summary>
    static void CheckSettingsPatch()
    {
        Section("Settings payload from the page");

        var claude = new ProviderSnapshot { Id = "claude", Name = "Claude Code" };
        claude.Gauges.Add(new Gauge { Label = "5-hour window", Raw = 90, Calibratable = true });
        claude.Gauges.Add(new Gauge { Label = "Weekly", Raw = 90, Calibratable = false });

        var posted = """
            {
              "claudePlan": "max5",
              "claudeFiveHourBudget": 88,
              "claudeWeeklyBudget": 700,
              "codexFiveHourBudget": 25,
              "codexWeeklyBudget": 200,
              "openCodeWeeklyBudget": 50,
              "antigravityWeeklyBudget": 50,
              "refreshSeconds": 60,
              "countCacheReads": true,
              "calibrateFiveHour": 84,
              "calibrateWeekly": 0
            }
            """;

        var s = new Settings();
        SettingsPatch.Apply(s, JsonDocument.Parse(posted).RootElement, claude);

        Check("calibration overrides the budget box",
            Math.Abs(s.ClaudeFiveHourBudget - 107.14) < 0.01, s.ClaudeFiveHourBudget.ToString());
        Check("calibrated gauge would now read what was observed",
            Math.Abs(Fmt.Pct(90, s.ClaudeFiveHourBudget) - 84) < 0.05,
            Fmt.Pct(90, s.ClaudeFiveHourBudget).ToString("0.##"));
        Check("blank weekly calibration leaves the budget alone",
            Math.Abs(s.ClaudeWeeklyBudget - 700) < 0.01, s.ClaudeWeeklyBudget.ToString());
        Check("other fields carried through",
            s.OpenCodeWeeklyBudget == 50 && s.RefreshSeconds == 60 && s.CountCacheReads);

        // Switching plan should load that plan's starting numbers.
        var s2 = new Settings { ClaudePlan = "max5" };
        SettingsPatch.Apply(s2,
            JsonDocument.Parse("""{"claudePlan":"pro","claudeFiveHourBudget":88,"claudeWeeklyBudget":700}""").RootElement,
            null);
        Check("changing plan loads that plan's numbers",
            Math.Abs(s2.ClaudeFiveHourBudget - Settings.ClaudePlans["pro"].FiveHour) < 0.01,
            s2.ClaudeFiveHourBudget.ToString());

        // Without a plan change, the typed budget wins.
        var s3 = new Settings { ClaudePlan = "max5" };
        SettingsPatch.Apply(s3,
            JsonDocument.Parse("""{"claudePlan":"max5","claudeFiveHourBudget":123.45}""").RootElement, null);
        Check("editing a budget without changing plan is kept",
            Math.Abs(s3.ClaudeFiveHourBudget - 123.45) < 0.01, s3.ClaudeFiveHourBudget.ToString());

        var s4 = new Settings();
        SettingsPatch.Apply(s4, JsonDocument.Parse("""{"refreshSeconds":1}""").RootElement, null);
        Check("refresh interval is clamped to a sane floor", s4.RefreshSeconds == 15, s4.RefreshSeconds.ToString());

        var s5 = new Settings();
        var before = s5.ClaudeFiveHourBudget;
        SettingsPatch.Apply(s5, JsonDocument.Parse("\"not an object\"").RootElement, null);
        Check("a malformed payload changes nothing", Math.Abs(s5.ClaudeFiveHourBudget - before) < 0.001);
    }

    static void CheckSettings()
    {
        Section("Settings");
        var file = Path.Combine(Settings.Dir, "settings.json");
        Directory.CreateDirectory(Settings.Dir);
        var backup = File.Exists(file) ? File.ReadAllText(file) : null;

        try
        {
            // A file written by the older build, when budgets were token counts.
            File.WriteAllText(file, """
                {
                  "ClaudePlan": "max5",
                  "ClaudeFiveHourBudget": 88000000,
                  "ClaudeWeeklyBudget": 880000000,
                  "CodexFiveHourBudget": 12000000,
                  "CodexWeeklyBudget": 120000000,
                  "RefreshSeconds": 60,
                  "CountCacheReads": true
                }
                """);

            var migrated = Settings.Load();
            Check("stale token budget is migrated", migrated.ClaudeFiveHourBudget < 100_000,
                migrated.ClaudeFiveHourBudget.ToString());
            Check("stale weekly budget is migrated", migrated.ClaudeWeeklyBudget < 100_000,
                migrated.ClaudeWeeklyBudget.ToString());
            Check("stale codex budgets migrated", migrated.CodexFiveHourBudget < 100_000);
            Check("unrelated settings survive migration", migrated.CountCacheReads && migrated.RefreshSeconds == 60);

            migrated.ClaudeFiveHourBudget = 103.5;
            migrated.Save();
            var again = Settings.Load();
            Check("settings round-trip through disk",
                Math.Abs(again.ClaudeFiveHourBudget - 103.5) < 0.001, again.ClaudeFiveHourBudget.ToString());

            File.WriteAllText(file, "{ this is not json");
            Check("corrupt settings fall back to defaults rather than throwing",
                Settings.Load().ClaudeFiveHourBudget > 0);
        }
        finally
        {
            if (backup is not null) File.WriteAllText(file, backup);
            else File.Delete(file);
        }
    }

    static void CheckCorruptInput(string fixtures, Settings settings)
    {
        Section("Corrupt input");
        var broken = Path.Combine(fixtures, "claude", "projects", "C--broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "x.jsonl"),
            "not json at all\n{\"usage\":truncated\n\n{\"type\":\"assistant\"}\n");

        var service = new UsageService();
        service.RefreshAsync(settings).GetAwaiter().GetResult();
        var claude = service.Latest.Providers.First(p => p.Id == "claude");

        Check("garbage lines do not break the collector", claude.State == ProviderState.Ok, claude.State.ToString());
        Check("valid records still counted alongside garbage",
            claude.TokensTotal == ClaudeTurns * PerTurn, claude.TokensTotal.ToString());

        var json = service.LatestJson;
        Check("snapshot serialises for the UI", json.Length > 200 && json.StartsWith('{'));
        Check("snapshot is valid json", IsJson(json));

        static bool IsJson(string s)
        {
            try { using var _ = JsonDocument.Parse(s); return true; }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- fixtures

    static void BuildFixtures(string root)
    {
        var now = DateTimeOffset.UtcNow;

        // Codex: a rollout carrying token_count events and a rate_limits block.
        var sessions = Path.Combine(root, "codex", "sessions", "2026", "09", "06");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(root, "codex", "auth.json"), """{"tokens":{"account_id":"acc"}}""");

        var lines = new List<string>
        {
            Json(new
            {
                timestamp = now.AddHours(-2).ToString("o"),
                type = "session_meta",
                payload = new { type = "session_meta", model = "gpt-5.6-terra", cwd = @"C:\code\demo" },
            }),
        };
        for (var i = 1; i <= 4; i++)
        {
            var pct = 15 * i;
            lines.Add(Json(new
            {
                timestamp = now.AddHours(-2).AddMinutes(i * 10).ToString("o"),
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = 50_000,
                            cached_input_tokens = 40_000,
                            output_tokens = 3_000,
                            reasoning_output_tokens = 900,
                        },
                    },
                    rate_limits = new
                    {
                        primary = new { used_percent = (double)pct, window_minutes = 300, resets_in_seconds = 7200 },
                        secondary = new { used_percent = pct / 3.0, window_minutes = 10080, resets_in_seconds = 300000 },
                    },
                },
            }));
        }
        File.WriteAllLines(Path.Combine(sessions, "rollout-test.jsonl"), lines);

        // Claude: a transcript whose folder name encodes a project path containing a dash. The
        // decoder resolves the ambiguity against the real filesystem, so the path it points at has
        // to exist — created inside the fixture root, never out on the user's drive.
        var realProject = Path.Combine(root, "code", "demo-app");
        Directory.CreateDirectory(realProject);

        var encoded = realProject.Replace(":", "-").Replace(Path.DirectorySeparatorChar, '-');
        var project = Path.Combine(root, "claude", "projects", encoded);
        Directory.CreateDirectory(project);
        var turns = new List<string>();
        for (var i = 1; i <= ClaudeTurns; i++)
        {
            turns.Add(Json(new
            {
                type = "assistant",
                timestamp = now.AddHours(-3).AddMinutes(i * 20).ToString("o"),
                sessionId = "s1",
                message = new
                {
                    model = "claude-opus-5",
                    usage = new
                    {
                        input_tokens = 120,
                        output_tokens = 2_400,
                        cache_read_input_tokens = 180_000,
                        cache_creation_input_tokens = 9_000,
                        output_tokens_details = new { thinking_tokens = 400 },
                    },
                },
            }));
        }
        turns.Add(turns[0]);  // a resumed session replays its first turn
        File.WriteAllLines(Path.Combine(project, "session.jsonl"), turns);
    }

    /// <summary>Fixtures are serialised rather than hand-written so they are always valid JSON.</summary>
    static string Json(object value) => JsonSerializer.Serialize(value);

    static void PointCollectorsAt(string root)
    {
        Environment.SetEnvironmentVariable("TOKENMETER_CLAUDE_DIR", Path.Combine(root, "claude"));
        Environment.SetEnvironmentVariable("TOKENMETER_CODEX_DIR", Path.Combine(root, "codex"));
        Environment.SetEnvironmentVariable("TOKENMETER_OPENCODE_DB", Path.Combine(root, "absent.db"));
        Environment.SetEnvironmentVariable("TOKENMETER_ANTIGRAVITY_DIR", Path.Combine(root, "absent"));
    }

    // ---------------------------------------------------------------- output

    static void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(title);
        Console.ResetColor();
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.ForegroundColor = ok ? ConsoleColor.DarkGreen : ConsoleColor.Red;
        Console.Write(ok ? "  PASS  " : "  FAIL  ");
        Console.ResetColor();
        Console.WriteLine(name + (ok || detail.Length == 0 ? "" : "   -> " + detail));
    }
}
