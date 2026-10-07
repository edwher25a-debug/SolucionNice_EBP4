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
    public class ACEROS_PILOTESCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_ACEROS_PILOTES viewModel = new vm_ACEROS_PILOTES(Document, UiDocument.Selection);
            v_ACEROS_PILOTES view = new v_ACEROS_PILOTES();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_ACEROS_PILOTES = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}