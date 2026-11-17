using PdfStudio.Core.Documents;

namespace PdfStudio.App.ViewModels;

public sealed class OutlineItemViewModel : ViewModelBase
{
    public OutlineItemViewModel(PdfOutlineNode node)
    {
        Title = node.Title;
        TargetPageIndex = node.TargetPageIndex;
        Children = node.Children.Select(c => new OutlineItemViewModel(c)).ToList();
    }

    public string Title { get; }
    public int? TargetPageIndex { get; }
    public IReadOnlyList<OutlineItemViewModel> Children { get; }
}
