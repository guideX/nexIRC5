using System.Collections.ObjectModel;
using nexIRC.Core.Session;

namespace nexIRC.Application;

public sealed record WhoisAdditionalField(int Numeric, string Text)
{
    public string Label => Numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DisplayText => $"[{Label}] {Text}";
}

public sealed class WhoisResult : ObservableObject
{
    private static readonly HashSet<int> KnownWhoisNumerics =
        new HashSet<int> { 301, 307, 310, 311, 312, 313, 317, 318, 319, 330, 335, 338, 378, 379, 671 };

    private string _nickname;
    private RichResultState _state;
    private string? _username;
    private string? _hostname;
    private string? _realName;
    private string? _server;
    private string? _serverDescription;
    private string? _account;
    private string? _operatorStatus;
    private string? _awayMessage;
    private string? _idleText;
    private string? _signOnText;
    private bool _isSecure;
    private string? _errorText;

    public WhoisResult(string requestedNickname)
    {
        _nickname = requestedNickname;
    }

    public static bool IsKnownWhoisNumeric(int numeric) => KnownWhoisNumerics.Contains(numeric);

    public static bool IsPotentialAdditionalNumeric(int numeric) => numeric is >= 300 and <= 399 && !IsKnownWhoisNumeric(numeric);

    public string Nickname
    {
        get => _nickname;
        private set => SetProperty(ref _nickname, value);
    }

    public RichResultState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }
    }

    public string StateText => State switch
    {
        RichResultState.Loading => "WHOIS in progress…",
        RichResultState.Completed => "WHOIS complete",
        RichResultState.Failed => string.IsNullOrWhiteSpace(ErrorText) ? "WHOIS failed" : $"WHOIS failed: {ErrorText}",
        _ => "WHOIS not started"
    };

    public bool IsLoading => State == RichResultState.Loading;

    public bool IsCompleted => State == RichResultState.Completed;

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string? Username
    {
        get => _username;
        private set => SetProperty(ref _username, value);
    }

    public string? Hostname
    {
        get => _hostname;
        private set => SetProperty(ref _hostname, value);
    }

    public string? RealName
    {
        get => _realName;
        private set => SetProperty(ref _realName, value);
    }

    public string? Server
    {
        get => _server;
        private set => SetProperty(ref _server, value);
    }

    public string? ServerDescription
    {
        get => _serverDescription;
        private set => SetProperty(ref _serverDescription, value);
    }

    public string? ServerText => Server is null
        ? ServerDescription
        : string.IsNullOrWhiteSpace(ServerDescription) ? Server : $"{Server} · {ServerDescription}";

    public string? Account
    {
        get => _account;
        private set => SetProperty(ref _account, value);
    }

    public string? OperatorStatus
    {
        get => _operatorStatus;
        private set => SetProperty(ref _operatorStatus, value);
    }

    public string? AwayMessage
    {
        get => _awayMessage;
        private set => SetProperty(ref _awayMessage, value);
    }

    public string? IdleText
    {
        get => _idleText;
        private set => SetProperty(ref _idleText, value);
    }

    public string? SignOnText
    {
        get => _signOnText;
        private set => SetProperty(ref _signOnText, value);
    }

    public bool IsSecure
    {
        get => _isSecure;
        private set => SetProperty(ref _isSecure, value);
    }

    public ObservableCollection<string> Channels { get; } = [];

    public ObservableCollection<WhoisAdditionalField> AdditionalFields { get; } = [];

    public string? Hostmask => string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Hostname)
        ? null
        : $"{Nickname}!{Username}@{Hostname}";

    public string FormattedText
    {
        get
        {
            var lines = new List<string> { $"WHOIS {Nickname}" };
            AddLine(lines, "Username", Username);
            AddLine(lines, "Hostname", Hostname);
            AddLine(lines, "Real name", RealName);
            AddLine(lines, "Server", ServerText);
            AddLine(lines, "Account", Account);
            AddLine(lines, "Operator", OperatorStatus);
            AddLine(lines, "Idle", IdleText);
            AddLine(lines, "Sign-on", SignOnText);
            if (IsSecure) lines.Add("Secure/TLS: yes");
            if (Channels.Count > 0) lines.Add($"Channels: {string.Join(' ', Channels)}");
            if (!string.IsNullOrWhiteSpace(AwayMessage)) AddLine(lines, "Away", AwayMessage);
            lines.AddRange(AdditionalFields.Select(additional => additional.DisplayText));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public void Begin()
    {
        Username = null;
        Hostname = null;
        RealName = null;
        Server = null;
        ServerDescription = null;
        Account = null;
        OperatorStatus = null;
        AwayMessage = null;
        IdleText = null;
        SignOnText = null;
        IsSecure = false;
        ErrorText = null;
        Channels.Clear();
        AdditionalFields.Clear();
        State = RichResultState.Loading;
    }

    public void Apply(IrcWhoisEvent item)
    {
        if (State == RichResultState.Idle)
        {
            Begin();
        }

        if (!string.IsNullOrWhiteSpace(item.Nickname))
        {
            Nickname = item.Nickname;
        }

        var parameters = item.Parameters;
        var text = item.Text ?? string.Empty;
        switch (item.Numeric)
        {
            case 311 when parameters.Count > 3:
                Username = Parameter(parameters, 2);
                Hostname = Parameter(parameters, 3);
                RealName = text;
                break;
            case 312 when parameters.Count > 2:
                Server = Parameter(parameters, 2);
                ServerDescription = text;
                OnPropertyChanged(nameof(ServerText));
                break;
            case 313:
                OperatorStatus = text;
                break;
            case 317:
                IdleText = FormatDuration(Parameter(parameters, 2));
                SignOnText = FormatSignOn(Parameter(parameters, 3));
                break;
            case 319:
                foreach (var channel in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Channels.Contains(channel, StringComparer.OrdinalIgnoreCase))
                    {
                        Channels.Add(channel);
                    }
                }
                break;
            case 330 when parameters.Count > 2:
                Account = Parameter(parameters, 2);
                break;
            case 301:
                AwayMessage = text;
                break;
            case 307:
                Account ??= "registered nickname";
                break;
            case 338:
                if (parameters.Count > 3)
                {
                    Hostname = Parameter(parameters, 3);
                }
                else if (parameters.Count > 2 && !string.IsNullOrWhiteSpace(Parameter(parameters, 2)))
                {
                    Hostname ??= Parameter(parameters, 2);
                }

                OnPropertyChanged(nameof(Hostname));
                IsSecure = true;
                break;
            case 671:
                IsSecure = true;
                break;
            case 318:
                State = RichResultState.Completed;
                return;
            default:
                AddAdditional(item.Numeric, text.Length == 0 ? string.Join(' ', parameters) : text);
                break;
        }

        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(Hostmask));
        OnPropertyChanged(nameof(FormattedText));
    }

    public void ApplyAdditional(int numeric, string text)
    {
        if (State == RichResultState.Idle)
        {
            Begin();
        }

        AddAdditional(numeric, text);
        OnPropertyChanged(nameof(FormattedText));
    }

    public void Fail(string message)
    {
        ErrorText = string.IsNullOrWhiteSpace(message) ? "The WHOIS request did not complete." : message;
        State = RichResultState.Failed;
    }

    private void AddAdditional(int numeric, string text)
    {
        if (AdditionalFields.Count >= 64 || AdditionalFields.Any(field => field.Numeric == numeric && field.Text == text))
        {
            return;
        }

        AdditionalFields.Add(new WhoisAdditionalField(numeric, text));
        OnPropertyChanged(nameof(FormattedText));
    }

    private static void AddLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }

    private static string? Parameter(IReadOnlyList<string> parameters, int index) => index < parameters.Count ? parameters[index] : null;

    private static string? FormatDuration(string? seconds)
    {
        if (!long.TryParse(seconds, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return seconds;
        }

        try
        {
            return TimeSpan.FromSeconds(Math.Max(0, value)).ToString();
        }
        catch (ArgumentOutOfRangeException)
        {
            return seconds;
        }
    }

    private static string? FormatSignOn(string? seconds)
    {
        if (!long.TryParse(seconds, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return seconds;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return seconds;
        }
    }
}

public sealed class WhoisView : WorkspaceView
{
    internal WhoisView(Guid networkId, Guid id, string nickname)
        : base(networkId, id, WorkspaceViewKind.Whois, $"WHOIS {nickname}")
    {
        RequestedNickname = nickname;
        Result = new WhoisResult(nickname);
        Result.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsCompleted));
        };
    }

    public string RequestedNickname { get; }

    public WhoisResult Result { get; }

    public string StateText => Result.StateText;

    public bool IsLoading => Result.IsLoading;

    public bool IsCompleted => Result.IsCompleted;

    public string? ErrorText => Result.ErrorText;

    internal void BeginRequest() => Result.Begin();

    internal void Apply(IrcWhoisEvent item) => Result.Apply(item);

    internal void ApplyAdditional(int numeric, string text) => Result.ApplyAdditional(numeric, text);

    internal void Fail(string message) => Result.Fail(message);
}
