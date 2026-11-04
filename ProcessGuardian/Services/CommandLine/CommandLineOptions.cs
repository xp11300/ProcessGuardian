namespace ProcessGuardian.Services.CommandLine;

/// <summary>
/// Parsed command line. <see cref="LaunchSelector"/> is null for a plain start. <see cref="IsAutoStart"/> is set by
/// <c>--autostart</c> (written by the "start with Windows" setting); it never launches an entry.
/// </summary>
internal sealed record CommandLineOptions(string? LaunchSelector, bool IsAutoStart = false)
{
    internal const string LaunchOption = "--launch";
    internal const string AutoStartOption = "--autostart";

    internal bool HasLaunch => LaunchSelector is not null;
}

/// <summary>Pure startup decisions derived from the command line (kept here so they can be tested without WPF).</summary>
internal static class StartupWindowPolicy
{
    /// <summary>
    /// A cold start with --launch or --autostart stays in the tray when the tray icon exists; otherwise (plain start,
    /// or no tray icon) the main window is shown. A failed configuration load is handled separately after startup.
    /// </summary>
    internal static bool ShouldStartHidden(CommandLineOptions options, bool trayAvailable) =>
        (options.HasLaunch || options.IsAutoStart) && trayAvailable;
}

/// <summary>Either valid options or a usage error message (never both).</summary>
internal sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    internal bool IsValid => Options is not null;
}

/// <summary>
/// Pure, side-effect-free parser for the only supported forms: no arguments, <c>--launch &lt;selector&gt;</c>,
/// <c>--launch=&lt;selector&gt;</c> and <c>--autostart</c> alone (option names case-insensitive; --autostart takes no
/// value, may be given once and cannot be combined with --launch). Everything else is a usage error. In the
/// two-token form a value that starts with "--" is treated as a missing value (use <c>--launch=--name</c> for such a
/// name). The selector is returned untrimmed; <see cref="ApplicationSelector"/> trims it.
/// </summary>
internal static class CommandLineParser
{
    internal const int UsageErrorExitCode = 2;

    internal const string UsageText =
        "Usage:\n" +
        "  ProcessGuardian.exe\n" +
        "  ProcessGuardian.exe --launch \"<software name or id>\"\n" +
        "  ProcessGuardian.exe --launch=\"<software name or id>\"\n" +
        "  ProcessGuardian.exe --autostart\n\n" +
        "The name must match a software name in Process Guardian exactly (case-insensitive); " +
        "an id is the entry's GUID from config.json.";

    private const int MaxArgumentEcho = 120;

    internal static CommandLineParseResult Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new CommandLineParseResult(new CommandLineOptions(null), null);
        }

        string? selector = null;
        var autoStart = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index] ?? string.Empty;
            string value;
            if (string.Equals(argument, CommandLineOptions.AutoStartOption, StringComparison.OrdinalIgnoreCase))
            {
                if (autoStart)
                {
                    return Invalid("--autostart can be given only once.");
                }

                if (selector is not null)
                {
                    return Invalid("--autostart cannot be combined with --launch.");
                }

                autoStart = true;
                continue;
            }

            if (argument.StartsWith(CommandLineOptions.AutoStartOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("--autostart does not take a value.");
            }

            if (string.Equals(argument, CommandLineOptions.LaunchOption, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count || (args[index + 1] ?? string.Empty).StartsWith("--", StringComparison.Ordinal))
                {
                    return Invalid("--launch needs a value: the software name or id of an entry.");
                }

                value = args[++index] ?? string.Empty;
            }
            else if (argument.StartsWith(CommandLineOptions.LaunchOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(CommandLineOptions.LaunchOption.Length + 1)..];
            }
            else
            {
                return Invalid(selector is null && !autoStart
                    ? $"Unknown argument: {Describe(argument)}."
                    : $"Unexpected extra argument: {Describe(argument)}.");
            }

            if (selector is not null)
            {
                return Invalid("--launch can be given only once.");
            }

            if (autoStart)
            {
                return Invalid("--autostart cannot be combined with --launch.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return Invalid("--launch needs a non-empty value: the software name or id of an entry.");
            }

            selector = value;
        }

        return new CommandLineParseResult(new CommandLineOptions(selector, autoStart), null);
    }

    internal static string Describe(string value)
    {
        var shown = value.Length > MaxArgumentEcho ? value[..MaxArgumentEcho] + "…" : value;
        return "\"" + shown + "\"";
    }

    private static CommandLineParseResult Invalid(string error) => new(null, error);
}
