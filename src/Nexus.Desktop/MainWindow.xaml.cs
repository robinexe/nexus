using System.Windows;
using Nexus.Desktop.ViewModels;

namespace Nexus.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
