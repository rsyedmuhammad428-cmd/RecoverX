using System.Windows;
using RecoverX.Desktop.ViewModels;

namespace RecoverX.Desktop
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
