using Stackroot.App.Commands;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;

namespace Stackroot.App.ViewModels;

public sealed class DevProxyLocationKindOption
{
    public required SiteDevProxyLocationKind Kind { get; init; }
    public required string Label { get; init; }
    public required string PatternHint { get; init; }
}

public sealed class DevProxyRowViewModel : ViewModelBase
{
    public static IReadOnlyList<DevProxyLocationKindOption> LocationKindOptions { get; } =
    [
        new()
        {
            Kind = SiteDevProxyLocationKind.Prefix,
            Label = "Path prefix",
            PatternHint = "/vite/"
        },
        new()
        {
            Kind = SiteDevProxyLocationKind.Exact,
            Label = "Exact path",
            PatternHint = "/api/health"
        },
        new()
        {
            Kind = SiteDevProxyLocationKind.Regex,
            Label = "Regex",
            PatternHint = "^/notifications/v1/[0-9a-f]{32}/connect$"
        },
        new()
        {
            Kind = SiteDevProxyLocationKind.RegexIgnoreCase,
            Label = "Regex (ignore case)",
            PatternHint = "^/api/.+"
        }
    ];

    private readonly Action<DevProxyRowViewModel> _onRemove;
    private readonly Action _onEnabledChanged;
    private string _name = string.Empty;
    private SiteDevProxyLocationKind _locationKind = SiteDevProxyLocationKind.Prefix;
    private string _locationPath = "/";
    private string _targetUrl = string.Empty;
    private bool _enabled = true;
    private bool _websocket;
    private bool _isExpanded;

    public DevProxyRowViewModel(
        SiteDevProxy? source,
        Action<DevProxyRowViewModel> onRemove,
        Action onEnabledChanged,
        bool expand = false,
        SiteDevProxyLocationKind? defaultKind = null)
    {
        _onRemove = onRemove;
        _onEnabledChanged = onEnabledChanged;
        Id = source?.Id ?? Guid.NewGuid().ToString("N");
        _name = source?.Name ?? string.Empty;
        if (source is null)
        {
            _locationKind = defaultKind ?? SiteDevProxyLocationKind.Prefix;
            _locationPath = _locationKind == SiteDevProxyLocationKind.Prefix ? "/" : string.Empty;
            _websocket = _locationKind is SiteDevProxyLocationKind.Regex or SiteDevProxyLocationKind.RegexIgnoreCase;
        }
        else
        {
            var normalized = SiteDevProxyLocation.Normalize(source.LocationKind, source.LocationPath);
            _locationKind = normalized.Kind;
            _locationPath = normalized.Pattern;
            _websocket = source.Websocket ?? normalized.Kind is SiteDevProxyLocationKind.Regex or SiteDevProxyLocationKind.RegexIgnoreCase;
        }

        _targetUrl = source?.TargetUrl ?? string.Empty;
        _enabled = source?.Enabled ?? (source is null);
        _isExpanded = expand;
        RemoveCommand = new RelayCommand(_ => _onRemove(this));
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RaisePropertyChanged(nameof(HeaderText));
            }
        }
    }

    public SiteDevProxyLocationKind LocationKind
    {
        get => _locationKind;
        set
        {
            if (SetProperty(ref _locationKind, value))
            {
                RaisePropertyChanged(nameof(SelectedLocationKindOption));
                RaisePropertyChanged(nameof(LocationPatternHint));
                RaisePropertyChanged(nameof(NginxLocationPreview));
            }
        }
    }

    public DevProxyLocationKindOption SelectedLocationKindOption
    {
        get => LocationKindOptions.First(option => option.Kind == LocationKind);
        set
        {
            if (value is null || value.Kind == LocationKind)
            {
                return;
            }

            LocationKind = value.Kind;
        }
    }

    public string LocationPath
    {
        get => _locationPath;
        set
        {
            var cleaned = SiteDevProxyLocation.ParsePatternInput(LocationKind, value);
            if (SiteDevProxyLocation.LooksLikeRegex(cleaned)
                && LocationKind == SiteDevProxyLocationKind.Prefix)
            {
                LocationKind = SiteDevProxyLocationKind.Regex;
            }

            if (SetProperty(ref _locationPath, cleaned))
            {
                RaisePropertyChanged(nameof(NginxLocationPreview));
            }
        }
    }

    public string TargetUrl
    {
        get => _targetUrl;
        set => SetProperty(ref _targetUrl, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                RaisePropertyChanged(nameof(StatusHint));
                _onEnabledChanged();
            }
        }
    }

    public bool Websocket
    {
        get => _websocket;
        set => SetProperty(ref _websocket, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string HeaderText => string.IsNullOrWhiteSpace(Name) ? "New proxy" : Name.Trim();

    public string StatusHint => Enabled ? "Active" : "Disabled";

    public string LocationPatternHint =>
        LocationKindOptions.First(option => option.Kind == LocationKind).PatternHint;

    public string NginxLocationPreview => $"location {SiteDevProxyLocation.Format(ToModel())} {{ … }}";

    public RelayCommand RemoveCommand { get; }

    public SiteDevProxy ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        LocationKind = LocationKind,
        LocationPath = LocationPath.Trim(),
        TargetUrl = TargetUrl.Trim(),
        Enabled = Enabled,
        Websocket = Websocket
    };

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Name is required.";
        }

        var locationError = SiteDevProxyLocation.Validate(LocationKind, LocationPath);
        if (locationError is not null)
        {
            return locationError;
        }

        if (string.IsNullOrWhiteSpace(TargetUrl))
        {
            return "Target URL is required.";
        }

        if (!Uri.TryCreate(TargetUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            return "Target URL must use http:// or https://.";
        }

        return null;
    }
}
