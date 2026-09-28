using System.Windows;
using System.Windows.Automation;

namespace TaskbarLyrics;

/// <summary>
/// Находит левую границу области уведомлений (трея) на основной панели задач, в физических пикселях.
/// Windows 10: системное окно TrayNotifyWnd. Windows 11: трей нарисован на XAML, поэтому ищем его
/// элементы через UI Automation (то же, чем пользуются экранные дикторы) — классы "SystemTray.*".
/// Вызывать из фонового потока: обход дерева занимает десятки миллисекунд.
/// </summary>
public static class TrayLocator
{
    /// <summary>Левая граница трея и ширина одной кнопки трея (0 — неизвестна), физ. пиксели.</summary>
    public static (int Left, int Button)? FindTray()
    {
        var taskbar = Native.TaskbarHandle();
        if (taskbar == IntPtr.Zero) return null;

        int? win32 = Native.GetTrayNotifyLeft(taskbar);
        if (Native.WindowsBuild < 22000 && win32 is int w10) return (w10, 0);

        try
        {
            Native.RECT? bar = Native.GetTaskbarRect();
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            using (cache.Activate())
            {
                var root = AutomationElement.FromHandle(taskbar);
                var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                var found = new List<Rect>();
                foreach (AutomationElement e in all)
                {
                    var cls = e.Cached.ClassName ?? "";
                    if (!cls.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
                    var r = e.Cached.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                    // Только то, что лежит на самой панели: всплывающие подсказки и окна трея не считаются
                    if (bar is { } b && (r.Top < b.Top - 1 || r.Bottom > b.Bottom + 1 || r.Left < b.Left || r.Right > b.Right + 1)) continue;
                    found.Add(r);
                }
                if (found.Count > 0)
                {
                    // Самая левая кнопка трея (обычно стрелка «скрытые значки»); среди совпадающих по краю
                    // берём самую узкую — это сама кнопка, а не общий контейнер
                    double min = found.Min(r => r.Left);
                    var button = found.Where(r => r.Left - min < 1.5 && r.Width >= 8).OrderBy(r => r.Width).FirstOrDefault();
                    return ((int)Math.Round(min), button.Width <= 0 ? 0 : (int)Math.Round(button.Width));
                }
            }
        }
        catch (Exception ex) { App.Log("Tray: " + ex.Message); }

        return win32 is int l ? (l, 0) : null;
    }
}
