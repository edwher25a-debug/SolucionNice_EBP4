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
    public class CoordCompartidasCmd : ExternalCommand
    {
        public override void Execute()
        {
            //Instanciamos
            vm_CoordCompartidas viewModel = new vm_CoordCompartidas(Document);
            v_CoordCompartidas view = new v_CoordCompartidas();
            //Formar la relacion entre vm y v
            view.DataContext = viewModel;
            viewModel.v_CoordCompartidas = view;

            view.ShowDialog();//Muestro la ventana (view)
        }
    }
}
