using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using ProyectoNice.ViewModels;
using ProyectoNice.Views;

namespace ProyectoNice.Commands
{
    /// <summary>
    ///     Ventana unica con todas las herramientas
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PrincipalCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                TaskDialog.Show("ProyectoNice", "Abra un proyecto para usar las herramientas.");
                return Result.Cancelled;
            }

            vm_Principal viewModel = new vm_Principal(uiDocument.Document, uiDocument.Selection);

            //La ventana se cierra para trabajar en Revit y se vuelve a abrir en la misma herramienta
            try
            {
                do
                {
                    v_Principal view = new v_Principal { DataContext = viewModel };
                    viewModel.v_Principal = view;
                    view.ShowDialog();
                }
                while (viewModel.EjecutarPendiente());
            }
            catch (System.Windows.Markup.XamlParseException ex)
            {
                //Revit solo muestra "no ha podido completar el comando": se muestra la causa real
                string causas = "";
                for (Exception? e = ex; e != null; e = e.InnerException) causas += $"{e.GetType().Name}: {e.Message}\n\n";
                TaskDialog.Show("ProyectoNice", $"No se pudo abrir la ventana.\n\n{causas}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }
    }
}
