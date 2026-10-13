using System.Collections.ObjectModel;
using System.Windows.Input;
using VRAirBridge.Core;
using VRAirBridge.Core.Hotspot;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>One label / value row (value column starts at 42 %).</summary>
public sealed class KvItem : ObservableObject
{
    private string _label = "";
    private string _value = "—";
    private string? _tip;
    private Tone _tone;

    public KvItem(string id) { Id = id; }

    public string Id { get; }
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string Value { get => _value; private set => Set(ref _value, value); }
    public string? Tip { get => _tip; private set => Set(ref _tip, value); }
    public Tone Tone { get => _tone; private set => Set(ref _tone, value); }

    internal void Update(KvSpec s)
    {
        Label = s.Label;
        var v = string.IsNullOrEmpty(s.Value) ? "—" : s.Value!;
        Value = v;
        Tone = s.Tone;
        Tip = s.Tip ?? (v.Length > 28 ? v : null);
    }

    /// <summary>Name that UI Automation / screen readers use for this row.</summary>
    public override string ToString() => $"{Label}: {Value}";
}

internal readonly record struct KvSpec(string Id, string Label, string? Value, Tone Tone = Tone.Normal, string? Tip = null);

/// <summary>Observable list of rows that is updated in place (no flicker, keeps tooltips open).</summary>
public sealed class KvList : ObservableCollection<KvItem>
{
    internal void Apply(IReadOnlyList<KvSpec> specs)
    {
        bool same = Count == specs.Count;
        for (int i = 0; same && i < specs.Count; i++) same = this[i].Id == specs[i].Id;
        if (!same)
        {
            Clear();
            foreach (var s in specs)
            {
                var k = new KvItem(s.Id);
                k.Update(s);
                Add(k);
            }
            return;
        }
        for (int i = 0; i < specs.Count; i++) this[i].Update(specs[i]);
    }
}

/// <summary>Metric tile on the hotspot card (channel / width / protocol / streams).</summary>
public sealed class MetricVm : ObservableObject
{
    private string _label = "";
    private string _value = "—";
    private Tone _tone = Tone.Dim;

    public MetricVm(string icon) { Icon = icon; }

    public string Icon { get; }
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public Tone Tone { get => _tone; set => Set(ref _tone, value); }
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>A choice in a combo box / segmented control whose label follows the UI language.</summary>
public sealed class Choice : ObservableObject
{
    private readonly Func<string> _text;

    internal Choice(string value, Func<string> text)
    {
        Value = value;
        _text = text;
    }

    public string Value { get; }
    public string Text => _text();
    internal void Refresh() => OnPropertyChanged(nameof(Text));
    public override string ToString() => Text;
}

/// <summary>Item of the experimental "pin channel" dropdown.</summary>
public sealed class PinItemVm
{
    internal PinItemVm(string text, string ssid, PinCandidate? candidate)
    {
        Text = text;
        Ssid = ssid;
        Candidate = candidate;
    }

    public string Text { get; }
    public string Ssid { get; }
    public PinCandidate? Candidate { get; }
    public bool Usable => Candidate == null || Candidate.Usable;
    public override string ToString() => Text;
}

public sealed class DeviceRowVm
{
    public string Name { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Ip { get; init; } = "";
    public string State { get; init; } = "";
    public string LastSeen { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Online { get; init; }
    public Tone Tone { get; init; }
    public Tone DotTone => Online ? (Tone == Tone.Warn ? Tone.Warn : Tone.Good) : Tone.Dim;

    public string CopyText => string.Join("\t", new[] { Name, Mac, Ip, State, LastSeen, Note }.Where(s => s.Length > 0));
    public override string ToString() => string.Join("  ", new[] { Name, Ip, State }.Where(s => s.Length > 0));
}

public sealed class LogLineVm
{
    internal LogLineVm(DateTime time, string text, Tone tone)
    {
        Time = time;
        Text = text;
        Tone = tone;
    }

    public DateTime Time { get; }
    public string Text { get; }
    public Tone Tone { get; }
    public override string ToString() => Text;
}

/// <summary>Status-bar segment: dot + label + value.</summary>
public sealed class StatusSegVm : ObservableObject
{
    private string _label = "";
    private string _value = "";
    private string? _tip;
    private Tone _tone = Tone.Dim;

    public StatusSegVm(string icon) { Icon = icon; }

    public string Icon { get; }
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string? Tip { get => _tip; set => Set(ref _tip, value); }
    public Tone Tone { get => _tone; set => Set(ref _tone, value); }
    public override string ToString() => $"{Label} {Value}";
}

/// <summary>A collapsible sidebar section (expanded state + search visibility).</summary>
public sealed class SectionVm : ObservableObject
{
    private bool _expanded = true;
    private bool _visible = true;
    private bool? _savedExpanded;

    internal SectionVm(string id, IReadOnlyList<string> keywordKeys, Func<IEnumerable<string>>? extra = null)
    {
        Id = id;
        KeywordKeys = keywordKeys;
        Extra = extra;
    }

    public string Id { get; }
    internal IReadOnlyList<string> KeywordKeys { get; }
    internal Func<IEnumerable<string>>? Extra { get; }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }

    internal bool Matches(string query)
    {
        if (query.Length == 0) return true;
        foreach (var k in KeywordKeys)
        {
            if (Loc.T(k).Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
            if (Loc.Lookup(Loc.English, k).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (Extra != null)
            foreach (var s in Extra())
                if (s.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        return false;
    }

    internal void ApplySearch(string query)
    {
        if (query.Length == 0)
        {
            IsVisible = true;
            if (_savedExpanded is bool b) IsExpanded = b;
            _savedExpanded = null;
            return;
        }
        _savedExpanded ??= IsExpanded;
        bool m = Matches(query);
        IsVisible = m;
        if (m) IsExpanded = true;
    }
}

public enum StepState { Todo, Current, Done }

public sealed class StepVm : ObservableObject
{
    private string _title = "";
    private StepState _state;

    public StepVm(int number) { Number = number; }

    public int Number { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public StepState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsCurrent)); } }
    }
    public bool IsDone => _state == StepState.Done;
    public bool IsCurrent => _state == StepState.Current;
    public override string ToString() => $"{Number}. {Title}";
}

/// <summary>Start-page tile (driver / settings / connect) with a live status line.</summary>
public sealed class TileVm : ObservableObject
{
    private string _title = "", _body = "", _status = "", _buttonText = "";
    private string? _extra;
    private Tone _statusTone = Tone.Dim;
    private bool _isCurrent, _showButton;
    private ICommand? _command;
    private string _buttonIcon = "I.play";

    public TileVm(string icon) { Icon = icon; }

    public string Icon { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Body { get => _body; set => Set(ref _body, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string? Extra { get => _extra; set => Set(ref _extra, value); }
    public Tone StatusTone { get => _statusTone; set => Set(ref _statusTone, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool ShowButton { get => _showButton; set => Set(ref _showButton, value); }
    public string ButtonText { get => _buttonText; set => Set(ref _buttonText, value); }
    public string ButtonIcon { get => _buttonIcon; set => Set(ref _buttonIcon, value); }
    public ICommand? Command { get => _command; set => Set(ref _command, value); }

    // Optional secret line (the hotspot password on the settings tile): masked until the eye button reveals it.
    // The view gives this line a fixed automation name, so the secret never reaches UIA.
    private string? _secret;
    private bool _secretRevealed;
    private const string Mask = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public string? Secret
    {
        get => _secret;
        set { if (Set(ref _secret, value)) { OnPropertyChanged(nameof(HasSecret)); OnPropertyChanged(nameof(SecretText)); } }
    }
    public bool HasSecret => !string.IsNullOrEmpty(_secret);
    public bool SecretRevealed
    {
        get => _secretRevealed;
        set
        {
            if (!Set(ref _secretRevealed, value)) return;
            OnPropertyChanged(nameof(SecretText));
            OnPropertyChanged(nameof(SecretToggleTip));
            OnPropertyChanged(nameof(SecretToggleIcon));
        }
    }
    public string SecretText => Loc.T("ui.start.passwordLine", _secretRevealed ? _secret ?? "" : Mask);
    public string SecretLabel => Loc.T("ui.hs.password");
    public string SecretToggleTip => Loc.T(_secretRevealed ? "ui.hs.hide" : "ui.hs.show");
    public string SecretToggleIcon => _secretRevealed ? "I.eye-off" : "I.eye";
    public ICommand ToggleSecretCommand => _toggleSecret ??= new RelayCommand(() => SecretRevealed = !SecretRevealed);
    private ICommand? _toggleSecret;

    /// <summary>Re-reads the localized secret texts (language change).</summary>
    public void RefreshSecretTexts()
    {
        OnPropertyChanged(nameof(SecretText));
        OnPropertyChanged(nameof(SecretLabel));
        OnPropertyChanged(nameof(SecretToggleTip));
    }

    public override string ToString() => Title; // never the secret: keep it out of UIA names
}

public sealed class LanguageChoice : ObservableObject
{
    internal LanguageChoice(string code) { Code = code; }

    /// <summary>Setting value: "auto", "en", "zh-CN", "ja", "ko".</summary>
    public string Code { get; }

    /// <summary>Each language in its own script; "auto" follows the current UI language.</summary>
    public string Text => Code == Loc.Auto ? Loc.T("ui.opt.languageAuto") : Loc.NativeName(Code);

    internal void Refresh() => OnPropertyChanged(nameof(Text));
    public override string ToString() => Text;
}
