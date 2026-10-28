using System.Windows;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Models;
using Timetable.Plugins;

namespace NcuCourseTable.Desktop.Services;

/// <summary>把宿主的数据读写能力暴露给插件，所有变更立即落盘并刷新窗口。</summary>
public sealed class PluginHost : IPluginHost
{
    private readonly DataStore _store;
    private readonly Action _refresh;
    private readonly Action<string> _notify;
    private readonly Dispatcher _dispatcher;

    public PluginHost(DataStore store, Action refresh, Action<string> notify)
    {
        _store = store;
        _refresh = refresh;
        _notify = notify;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public string DataDir => _store.Dir;

    public string ActiveTermcode => _store.Data.Termcode;

    public int TotalWeeks => _store.Data.TotalWeeks;

    public int CountEvents(string pluginId) =>
        _store.Data.Events.Count(e => string.Equals(e.Source, pluginId, StringComparison.OrdinalIgnoreCase));

    public void ReplaceEvents(string pluginId, IReadOnlyList<PluginEvent> events,
        string? termcode = null, string? termName = null)
    {
        Run(() =>
        {
            ApplyTerm(termcode, termName);
            _store.Data.Events.RemoveAll(e =>
                string.Equals(e.Source, pluginId, StringComparison.OrdinalIgnoreCase));

            foreach (PluginEvent e in events)
            {
                if (string.IsNullOrWhiteSpace(e.Title)) continue;
                var item = new ScheduleEvent
                {
                    Title = e.Title.Trim(),
                    Category = string.IsNullOrWhiteSpace(e.Category) ? "课程" : e.Category,
                    Teacher = e.Teacher ?? "",
                    Location = e.Location ?? "",
                    Notes = e.Notes ?? "",
                    ColorKey = e.ColorKey ?? "",
                    Source = pluginId,
                    Weekday = Math.Clamp(e.Weekday, 1, 7),
                    StartSection = Math.Max(1, e.StartSection),
                    EndSection = Math.Max(Math.Max(1, e.StartSection), e.EndSection),
                    WeeksRaw = string.IsNullOrWhiteSpace(e.WeeksRaw) ? $"1-{_store.Data.TotalWeeks}周" : e.WeeksRaw,
                    Date = string.IsNullOrWhiteSpace(e.Date) ? null : e.Date,
                    Raw = e.Raw,
                };
                _store.Data.Events.Add(item);
            }
            _store.Save();
        });
        _refresh();
    }

    public void SetTerm(string termcode, string termName, string? firstMonday = null)
    {
        Run(() =>
        {
            ApplyTerm(termcode, termName);
            if (!string.IsNullOrWhiteSpace(firstMonday))
            {
                _store.Data.TermStart = firstMonday.Trim();
            }
            _store.Save();
        });
        _refresh();
    }

    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _dispatcher.BeginInvoke(() => _notify(message));
    }

    public void RequestRefresh()
    {
        _dispatcher.BeginInvoke(_refresh);
    }

    private void ApplyTerm(string? termcode, string? termName)
    {
        if (string.IsNullOrWhiteSpace(termcode)) return;
        string code = termcode.Trim();
        bool changed = !string.Equals(_store.Data.Termcode, code, StringComparison.Ordinal);
        _store.Data.Termcode = code;
        if (!string.IsNullOrWhiteSpace(termName))
        {
            _store.Data.TermName = termName.Trim();
        }
        else if (changed || string.IsNullOrWhiteSpace(_store.Data.TermName))
        {
            _store.Data.TermName = DataStore.FormatTermName(code);
        }
        if ((changed || string.IsNullOrWhiteSpace(_store.Data.TermStart)) &&
            DataStore.GuessTermStart(code) is { } guess)
        {
            _store.Data.TermStart = guess;
        }
    }

    private void Run(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }
}
