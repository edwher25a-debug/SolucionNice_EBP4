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
    public class TuberiasporCADCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_TuberiasporCAD viewModel = new vm_TuberiasporCAD(Document, UiDocument.Selection);
            v_TuberiasporCAD view = new v_TuberiasporCAD();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_TuberiasporCAD = view;

            view.ShowDialog();//Muestro la ventana (view)

        }
    }
}