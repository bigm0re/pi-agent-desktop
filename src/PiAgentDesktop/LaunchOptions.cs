namespace PiAgentDesktop;

/// <summary>Parsed command line switches.</summary>
internal sealed class LaunchOptions
{
    public bool ForceShow { get; private set; }

    public bool ForceHidden { get; private set; }

    public bool NoTray { get; private set; }

    public bool SmokeTest { get; private set; }

    public bool EchoLog { get; private set; }

    public int? Port { get; private set; }

    public string? NodePath { get; private set; }

    public string? PiWebDir { get; private set; }

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var (name, inlineValue) = SplitInline(arg);

            switch (name.ToLowerInvariant())
            {
                case "--show":
                case "--open":
                case "-s":
                    options.ForceShow = true;
                    break;
                case "--hidden":
                case "--minimized":
                    options.ForceHidden = true;
                    break;
                case "--no-tray":
                    options.NoTray = true;
                    break;
                case "--smoke-test":
                    options.SmokeTest = true;
                    options.EchoLog = true;
                    break;
                case "--echo-log":
                    options.EchoLog = true;
                    break;
                case "--port":
                case "-p":
                    if (int.TryParse(ReadValue(args, ref i, inlineValue), out var port) && port is > 0 and <= 65535)
                    {
                        options.Port = port;
                    }

                    break;
                case "--node":
                    options.NodePath = ReadValue(args, ref i, inlineValue);
                    break;
                case "--pi-web-dir":
                    options.PiWebDir = ReadValue(args, ref i, inlineValue);
                    break;
            }
        }

        return options;
    }

    private static (string Name, string? Value) SplitInline(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator < 0
            ? (arg, null)
            : (arg[..separator], arg[(separator + 1)..]);
    }

    private static string? ReadValue(string[] args, ref int index, string? inlineValue)
    {
        if (!string.IsNullOrEmpty(inlineValue))
        {
            return inlineValue;
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
        {
            index++;
            return args[index];
        }

        return null;
    }
}
