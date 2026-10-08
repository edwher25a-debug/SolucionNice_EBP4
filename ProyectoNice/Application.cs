using Nice3point.Revit.Toolkit.External;
using Nice3point.Revit.Extensions.UI;
using ProyectoNice.Commands;

namespace ProyectoNice
{
    /// <summary>
    ///     Application entry point
    /// </summary>
    [UsedImplicitly]
    public class Application : ExternalApplication
    {
        public override void OnStartup()
        {
            CreateRibbon();
        }

        private void CreateRibbon()
        {
            var panel = Application.CreatePanel("MisBotones", "ProyectoNice");

            panel.AddPushButton<PrincipalCmd>("ProyectoNice")
                .SetLargeImage("/ProyectoNice;component/Resources/Icons/RibbonIcon32.png");
        }
    }
}