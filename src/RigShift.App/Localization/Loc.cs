using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace RigShift.App.Localization;

/// <summary>
/// String lookup for XAML (<c>{loc:Tr Key}</c>) and code. Switching the language raises a change for every
/// indexer binding and <see cref="PropertyChanged"/>, so open windows and view models update without a restart.
/// </summary>
/// <remarks>
/// The chosen language is kept here instead of relying on <see cref="CultureInfo.CurrentUICulture"/>: a change made
/// inside an async method is undone for the caller when the await returns (culture flows with the execution context).
/// Only the text language follows the setting; numbers, dates and times keep the Windows regional format
/// (analysis finding J-02: choosing "Deutsch" must not turn de-AT formats into neutral de ones).
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;

    private readonly ResourceManager _resources = new("RigShift.App.Resources.Strings", typeof(Loc).Assembly);
    private readonly Lock _gate = new();
    private readonly List<Listener> _listeners = [];

    private Loc()
    {
    }

    /// <summary>
    /// Raised on the thread each listener subscribed on. The language is app-wide, but a list, a control or a view
    /// model belongs to the thread that made it; one reached from another thread throws.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            if (value is not null)
            {
                // The base context has no thread of its own: posting to it only runs the handler on the pool, next to the
                // same object's other handlers (two catalogs rebuilding one list at once crashed the test run).
                SynchronizationContext? context = SynchronizationContext.Current;
                if (context?.GetType() == typeof(SynchronizationContext))
                {
                    context = null;
                }

                lock (_gate)
                {
                    _listeners.Add(new Listener(value, context, Environment.CurrentManagedThreadId));
                }
            }
        }

        remove
        {
            lock (_gate)
            {
                int index = _listeners.FindLastIndex(l => l.Handler == value);
                if (index >= 0)
                {
                    _listeners.RemoveAt(index);
                }
            }
        }
    }

    public static Loc Instance { get; } = new();

    /// <summary>Format culture for numbers, dates and times: the Windows regional format, independent of the language.</summary>
    public CultureInfo Culture { get; } = CultureInfo.CurrentCulture;

    /// <summary>Language of the texts.</summary>
    public CultureInfo UICulture { get; private set; } = SystemUICulture;

    public string this[string key] => _resources.GetString(key, UICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(Instance.Culture, Instance[key], args);

    /// <param name="language"><c>null</c> follows Windows, otherwise a culture name such as <c>de</c>.</param>
    public void SetLanguage(string? language)
    {
        CultureInfo wanted = string.IsNullOrWhiteSpace(language) ? SystemUICulture : CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = wanted;

        // Every settings load sets the language; only a real change is worth rebuilding every list and page for.
        if (Equals(wanted, UICulture))
        {
            return;
        }

        UICulture = wanted;
        Listener[] listeners;
        lock (_gate)
        {
            listeners = [.. _listeners];
        }

        var args = new PropertyChangedEventArgs("Item[]");
        foreach (Listener listener in listeners)
        {
            if (listener.Context is null || listener.Thread == Environment.CurrentManagedThreadId)
            {
                listener.Handler(this, args);
            }
            else
            {
                listener.Context.Post(_ => listener.Handler(this, args), null);
            }
        }
    }

    private sealed record Listener(PropertyChangedEventHandler Handler, SynchronizationContext? Context, int Thread);
}
