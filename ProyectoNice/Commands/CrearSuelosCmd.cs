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
    public class CrearSuelosCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_CrearSuelos viewModel = new vm_CrearSuelos(Document, UiDocument.Selection);
            v_CrearSuelos view = new v_CrearSuelos();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_CrearSuelos = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}