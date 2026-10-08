using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using ProyectoNice.ViewModels;
using ProyectoNice.Views;

namespace ProyectoNice.Commands
{
    /// <summary>
    ///     Ventana unica con todas las herramientas
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class PrincipalCmd : ExternalCommand
    {
        public override void Execute()
        {
            vm_Principal viewModel = new vm_Principal(Document, UiDocument.Selection);

            //La ventana se cierra para trabajar en Revit y se vuelve a abrir en la misma herramienta
            do
            {
                v_Principal view = new v_Principal { DataContext = viewModel };
                viewModel.v_Principal = view;
                view.ShowDialog();
            }
            while (viewModel.EjecutarPendiente());
        }
    }
}
