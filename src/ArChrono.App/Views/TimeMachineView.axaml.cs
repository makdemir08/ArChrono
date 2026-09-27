using ArChrono.App.ViewModels;
using Avalonia.Controls;

namespace ArChrono.App.Views;

public partial class TimeMachineView : UserControl
{
    public TimeMachineView()
    {
        InitializeComponent();
        DayStrip.MarkClicked += (_, mark) => (DataContext as TimeMachineViewModel)?.OnMarkClicked(mark);
    }
}
