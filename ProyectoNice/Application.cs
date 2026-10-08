using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using ProyectoNice.Commands;

namespace ProyectoNice
{
    /// <summary>
    ///     Application entry point
    /// </summary>
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CreateRibbon(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ProyectoNice", $"No se pudo crear la pestaña ProyectoNice.\n\n{ex}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        //Cinta solo con la API de Revit: no depende de la version de Nice3point que cargue otro complemento
        private static void CreateRibbon(UIControlledApplication application)
        {
            const string pestana = "ProyectoNice";
            try
            {
                application.CreateRibbonTab(pestana);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                //La pestaña ya existe
            }

            RibbonPanel panel = application.CreateRibbonPanel(pestana, "MisBotones");

            var boton = new PushButtonData(nameof(PrincipalCmd), "ProyectoNice",
                Assembly.GetExecutingAssembly().Location, typeof(PrincipalCmd).FullName)
            {
                ToolTip = "Abre la ventana con todas las herramientas",
                LargeImage = new BitmapImage(new Uri("pack://application:,,,/ProyectoNice;component/Resources/Icons/RibbonIcon32.png")),
                Image = new BitmapImage(new Uri("pack://application:,,,/ProyectoNice;component/Resources/Icons/RibbonIcon16.png"))
            };

            panel.AddItem(boton);
        }
    }
}
