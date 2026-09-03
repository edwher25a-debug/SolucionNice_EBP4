using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using ProyectoNice.ViewModels;
using ProyectoNice.Views;

namespace ProyectoNice.Commands
{
    /// <summary>
    ///     External command entry point
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class TrabajoHomeCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_TrabajoHome viewModel = new vm_TrabajoHome(Document, UiDocument.Selection);
            v_TrabajoHome view = new v_TrabajoHome();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_TrabajoHome = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}