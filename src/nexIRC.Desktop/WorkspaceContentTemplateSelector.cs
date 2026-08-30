using System.Windows;
using System.Windows.Controls;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed class WorkspaceContentTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (container is not FrameworkElement element)
        {
            return base.SelectTemplate(item, container);
        }

        var key = item switch
        {
            ServerStatusView => "StatusContentTemplate",
            ChannelView => "ChannelContentTemplate",
            QueryView => "QueryContentTemplate",
            _ => null
        };
        return key is null ? base.SelectTemplate(item, container) : element.FindResource(key) as DataTemplate;
    }
}
