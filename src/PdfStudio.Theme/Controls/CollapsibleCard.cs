using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace PdfStudio.Theme.Controls;

/// <summary>
/// A rounded collapsible card panel used in inspector sidebars and settings panels.
/// </summary>
public class CollapsibleCard : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsibleCard, bool>(nameof(IsExpanded), defaultValue: true);

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }
}
