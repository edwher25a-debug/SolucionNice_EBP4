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
    public class TrabajoFinalCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            TrabajoFinal viewModel = new TrabajoFinal(Document, UiDocument.Selection);
            v_TrabajoFinal view = new v_TrabajoFinal();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_TrabajoFinal = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}