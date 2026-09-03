using ProyectoNice.ViewModels;

namespace ProyectoNice.Views
{
    public sealed partial class ProyectoNiceView
    {
        public ProyectoNiceView(ProyectoNiceViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
        }
    }
}