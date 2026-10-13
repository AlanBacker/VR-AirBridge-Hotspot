using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui.ViewModels;

public enum DialogKind { Info, Question, Warning, Error, Success }

public sealed class DialogButtonVm
{
    internal DialogButtonVm(string text, string result, bool primary = false, bool danger = false)
    {
        Text = text;
        Result = result;
        IsPrimary = primary;
        IsDanger = danger;
    }

    public string Text { get; }
    public string Result { get; }
    public bool IsPrimary { get; }
    public bool IsDanger { get; }
    public ICommand? Command { get; internal set; }
}

/// <summary>
/// One in-app dialog (replaces MessageBox / TaskDialog / the old result window): title, message, optional
/// read-only detail lines with a copy button, footer, footnote and buttons. The result is the button's id.
/// </summary>
public sealed class DialogVm : ObservableObject
{
    public const string Ok = "ok";
    public const string Yes = "yes";
    public const string No = "no";
    public const string Close = "close";

    private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _copyText;

    internal DialogVm(string? title, string? message, DialogKind kind, IEnumerable<DialogButtonVm> buttons, string cancelResult)
    {
        Title = string.IsNullOrWhiteSpace(title) ? Loc.T("app.name") : title!;
        Message = message ?? "";
        Kind = kind;
        CancelResult = cancelResult;
        Buttons = new ObservableCollection<DialogButtonVm>(buttons);
        foreach (var b in Buttons)
        {
            var r = b.Result;
            b.Command = new RelayCommand(() => Host?.Close(this, r));
        }
        _copyText = Loc.T("dialog.copy");
        CopyCommand = new RelayCommand(DoCopy);
    }

    internal DialogHostVm? Host { get; set; }

    public string Title { get; }
    public string Message { get; }
    public bool HasMessage => Message.Length > 0;
    public IReadOnlyList<string>? Lines { get; private init; }
    public string LinesText => Lines == null ? "" : string.Join(Environment.NewLine, Lines);
    public bool HasLines => Lines is { Count: > 0 };
    public string? Footer { get; private init; }
    public string? Footnote { get; init; }
    public DialogKind Kind { get; }
    public string CancelResult { get; }
    public ObservableCollection<DialogButtonVm> Buttons { get; }
    public ICommand CopyCommand { get; }
    public string CopyText { get => _copyText; private set => Set(ref _copyText, value); }

    public string IconKey => Kind switch
    {
        DialogKind.Question => "I.circle-question-mark",
        DialogKind.Warning => "I.triangle-alert",
        DialogKind.Error => "I.circle-x",
        DialogKind.Success => "I.circle-check",
        _ => "I.info",
    };

    public Tone IconTone => Kind switch
    {
        DialogKind.Warning => Tone.Warn,
        DialogKind.Error => Tone.Bad,
        DialogKind.Success => Tone.Good,
        _ => Tone.Accent,
    };

    internal Task<string> Result => _tcs.Task;
    internal void Complete(string result) => _tcs.TrySetResult(result);

    private void DoCopy()
    {
        var text = string.Join(Environment.NewLine, new[] { Title, Message, LinesText, Footer ?? "" }.Where(s => s.Length > 0));
        Host?.Copy?.Invoke(text);
        CopyText = Loc.T("dialog.copied");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) => { t.Stop(); CopyText = Loc.T("dialog.copy"); };
        t.Start();
    }

    // ------------------------------------------------------------------ factories

    internal static DialogVm Notice(string text, DialogKind kind = DialogKind.Info, string? title = null) =>
        new(title, text, kind, new[] { new DialogButtonVm(Loc.T("dialog.ok"), Ok, primary: true) }, Ok);

    internal static DialogVm Confirm(string text, DialogKind kind = DialogKind.Question, string? title = null) =>
        new(title, text, kind, new[]
        {
            new DialogButtonVm(Loc.T("dialog.yes"), Yes, primary: true),
            new DialogButtonVm(Loc.T("dialog.no"), No),
        }, No);

    /// <summary>Result of a multi-step operation: scrollable, selectable lines + copy.</summary>
    internal static DialogVm Report(string title, IEnumerable<string> lines, string? footer = null, DialogKind kind = DialogKind.Info) =>
        new(title, null, kind, new[] { new DialogButtonVm(Loc.T("dialog.close"), Close, primary: true) }, Close)
        {
            Lines = lines.ToList(),
            Footer = string.IsNullOrWhiteSpace(footer) ? null : footer,
        };

    internal static DialogVm Custom(string? title, string text, DialogKind kind, string cancelResult, params DialogButtonVm[] buttons) =>
        new(title, text, kind, buttons, cancelResult);
}

/// <summary>Queue of in-app dialogs shown one at a time over the main window.</summary>
public sealed class DialogHostVm : ObservableObject
{
    private readonly Queue<DialogVm> _queue = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DialogVm? _current;
    private bool _isOpen, _closing;

    /// <summary>Called before a dialog appears (shows the window if it is hidden in the tray).</summary>
    internal Action? BeforeShow { get; set; }

    internal Action<string>? Copy { get; set; }

    public DialogVm? Current { get => _current; private set => Set(ref _current, value); }
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    internal Task<string> ShowAsync(DialogVm d)
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(() => ShowAsync(d));
        d.Host = this;
        if (_current == null && !_closing) Present(d);
        else _queue.Enqueue(d);
        return d.Result;
    }

    private void Present(DialogVm d)
    {
        try { BeforeShow?.Invoke(); } catch { }
        Current = d;
        IsOpen = true;
    }

    internal void Close(DialogVm d, string result)
    {
        if (!ReferenceEquals(d, _current) || _closing) return;
        _closing = true;
        IsOpen = false;
        d.Complete(result);
        void Next()
        {
            _closing = false;
            if (_queue.Count > 0) Present(_queue.Dequeue());
            else Current = null;
        }
        if (!Motion.Enabled) { Next(); return; }
        var t = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) => { t.Stop(); Next(); };
        t.Start();
    }

    /// <summary>Closes the current dialog with its cancel result (Esc / click outside).</summary>
    internal void Cancel()
    {
        if (_current != null) Close(_current, _current.CancelResult);
    }

    /// <summary>Accepts the current dialog with its primary button (Enter).</summary>
    internal void Accept()
    {
        var d = _current;
        if (d == null) return;
        var p = d.Buttons.FirstOrDefault(b => b.IsPrimary) ?? d.Buttons.FirstOrDefault();
        Close(d, p?.Result ?? d.CancelResult);
    }

    /// <summary>
    /// Yes / no question that blocks the calling thread (Core operations ask from worker threads).
    /// On the UI thread a nested dispatcher frame keeps the window responsive.
    /// </summary>
    internal bool ConfirmBlocking(string text)
    {
        if (_dispatcher.CheckAccess())
        {
            var t = ShowAsync(DialogVm.Confirm(text));
            if (!t.IsCompleted)
            {
                var frame = new DispatcherFrame();
                t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            return t.Result == DialogVm.Yes;
        }
        var task = _dispatcher.Invoke(() => ShowAsync(DialogVm.Confirm(text)));
        return task.GetAwaiter().GetResult() == DialogVm.Yes;
    }
}

/// <summary>Toast notification (bottom-right): icon, text, dismiss button.</summary>
public sealed class ToastVm : ObservableObject
{
    private bool _leaving;

    internal ToastVm(string text, Tone tone, bool persistent)
    {
        Text = text;
        Tone = tone;
        Persistent = persistent;
    }

    public string Text { get; }
    public Tone Tone { get; }
    public bool Persistent { get; }
    public bool IsLeaving { get => _leaving; internal set => Set(ref _leaving, value); }
    public ICommand? DismissCommand { get; internal set; }
    internal DispatcherTimer? Timer { get; set; }

    public string IconKey => Tone switch
    {
        Tone.Good => "I.circle-check",
        Tone.Warn => "I.triangle-alert",
        Tone.Bad => "I.circle-x",
        _ => "I.info",
    };
}

public sealed class ToastHostVm
{
    private const int MaxToasts = 5;

    public ObservableCollection<ToastVm> Items { get; } = new();

    internal void Show(string text, Tone tone = Tone.Normal, bool persistent = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = new ToastVm(text.Trim(), tone, persistent);
        t.DismissCommand = new RelayCommand(() => Leave(t));
        while (Items.Count(i => !i.IsLeaving) >= MaxToasts)
        {
            var old = Items.First(i => !i.IsLeaving);
            old.Timer?.Stop();
            Items.Remove(old);
        }
        Items.Add(t);
        if (persistent) return;
        t.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tone == Tone.Bad ? 7 : 4.5) };
        t.Timer.Tick += (_, _) => Leave(t);
        t.Timer.Start();
    }

    private void Leave(ToastVm t)
    {
        t.Timer?.Stop();
        if (t.IsLeaving) return;
        if (!Motion.Enabled) { Items.Remove(t); return; }
        t.IsLeaving = true;
        var r = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
        r.Tick += (_, _) => { r.Stop(); Items.Remove(t); };
        r.Start();
    }
}
