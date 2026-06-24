using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;
using SiteDevProxy = Stackroot.Core.Sites.Models.SiteDevProxy;

namespace Stackroot.App.ViewModels;

public sealed class DevProxyDirectiveRowViewModel : ViewModelBase
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public DevProxyDirectiveRowViewModel(string key, string value, Action<DevProxyDirectiveRowViewModel>? onRemove = null)
    {
        _key = key;
        _value = value;
        RemoveCommand = new RelayCommand(_ => onRemove?.Invoke(this), _ => onRemove is not null);
    }

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public RelayCommand RemoveCommand { get; }
}

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
    private readonly NginxHttpSettings _httpSettings;
    private string _name = string.Empty;
    private SiteDevProxyLocationKind _locationKind = SiteDevProxyLocationKind.Prefix;
    private string _locationPath = "/";
    private bool _enabled = true;
    private bool _websocket;
    private bool _isExpanded;

    public DevProxyRowViewModel(
        SiteDevProxy? source,
        Action<DevProxyRowViewModel> onRemove,
        Action onEnabledChanged,
        NginxHttpSettings httpSettings,
        bool expand = false,
        SiteDevProxyLocationKind? defaultKind = null)
    {
        _onRemove = onRemove;
        _onEnabledChanged = onEnabledChanged;
        _httpSettings = httpSettings ?? new NginxHttpSettings();
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

        _enabled = source?.Enabled ?? (source is null);
        _isExpanded = expand;
        Directives = new ObservableCollection<DevProxyDirectiveRowViewModel>();
        LoadDirectives(source);
        RemoveCommand = new RelayCommand(_ => _onRemove(this));
        AddDirectiveCommand = new RelayCommand(_ => Directives.Add(new DevProxyDirectiveRowViewModel(string.Empty, string.Empty, RemoveDirective)));
        ResetDirectivesCommand = new RelayCommand(_ => ResetDirectives());
    }

    public string Id { get; }

    public ObservableCollection<DevProxyDirectiveRowViewModel> Directives { get; }

    public RelayCommand AddDirectiveCommand { get; }

    public RelayCommand ResetDirectivesCommand { get; }

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
        set
        {
            if (SetProperty(ref _websocket, value))
            {
                RebuildDirectiveRowsFromOverrides(CollectDirectiveOverrides());
            }
        }
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

    public SiteDevProxy ToModel()
    {
        var entries = Directives
            .Select(row => new KeyValuePair<string, string>(row.Key.Trim(), row.Value.Trim()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToList();

        var proxy = new SiteDevProxy
        {
            Id = Id,
            Name = Name.Trim(),
            LocationKind = LocationKind,
            LocationPath = LocationPath.Trim(),
            TargetUrl = GetDirectiveValue("proxy_pass"),
            Enabled = Enabled,
            Websocket = Websocket
        };

        proxy.DirectiveOverrides = SiteDevProxyDirectives.ComputeOverrides(proxy, entries, _httpSettings);
        return proxy;
    }

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

        foreach (var row in Directives)
        {
            var error = SiteDevProxyDirectives.ValidateEntry(row.Key, row.Value);
            if (error is not null)
            {
                return error;
            }
        }

        var proxy = ToModel();
        return SiteDevProxyDirectives.ValidateProxy(proxy, _httpSettings);
    }

    private void RemoveDirective(DevProxyDirectiveRowViewModel row) => Directives.Remove(row);

    private void LoadDirectives(SiteDevProxy? source)
    {
        RebuildDirectiveRowsFromOverrides(source?.DirectiveOverrides, source?.TargetUrl);
    }

    private void ResetDirectives()
    {
        var proxyPass = GetDirectiveValue("proxy_pass");
        RebuildDirectiveRowsFromOverrides(null, proxyPass);
    }

    private Dictionary<string, string> CollectDirectiveOverrides()
        => SiteDevProxyDirectives.ComputeOverrides(
            BuildProxyShell(),
            Directives.Select(row => new KeyValuePair<string, string>(row.Key, row.Value)).ToList(),
            _httpSettings)
           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private SiteDevProxy BuildProxyShell() =>
        new()
        {
            TargetUrl = GetDirectiveValue("proxy_pass"),
            Websocket = Websocket
        };

    private string GetDirectiveValue(string key) =>
        Directives.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase))?.Value.Trim()
        ?? string.Empty;

    private void RebuildDirectiveRowsFromOverrides(Dictionary<string, string>? overrides, string? targetUrl = null)
    {
        var proxy = new SiteDevProxy
        {
            TargetUrl = targetUrl ?? GetDirectiveValue("proxy_pass"),
            Websocket = Websocket,
            DirectiveOverrides = overrides
        };

        Directives.Clear();
        foreach (var entry in SiteDevProxyDirectives.BuildMerged(proxy, _httpSettings))
        {
            Directives.Add(new DevProxyDirectiveRowViewModel(entry.Key, entry.Value, RemoveDirective));
        }
    }
}
