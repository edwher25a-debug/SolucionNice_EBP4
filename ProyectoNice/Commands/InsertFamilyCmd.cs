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
    public class InsertFamilyCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_InsertFamily viewModel = new vm_InsertFamily(Document, UiDocument.Selection);
            v_InsertFamily view = new v_InsertFamily();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_InsertFamily = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}