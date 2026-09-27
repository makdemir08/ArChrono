using ArChrono.App.ViewModels;
using Avalonia.Controls;

namespace ArChrono.App.Views;

public partial class RepositoryView : UserControl
{
    public RepositoryView()
    {
        InitializeComponent();
        Strip.MarkClicked += (_, mark) => (DataContext as RepositoryViewModel)?.OnChronoMarkClicked(mark);
    }
}
