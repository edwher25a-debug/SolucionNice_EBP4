using ProyectoNice.Commands;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_ExportarFichas : vm_Herramienta
    {
        //Datos de entrada
        public Document doc;

        //Botones
        public RelayCommand AceptarBT { get; set; }

        //Constructor
        public vm_ExportarFichas(Document doc)
        {
            this.doc = doc;

            AceptarBT = new RelayCommand(() => Ejecutar(() => ExportarFichasCmd.Exportar(doc)));
        }
    }
}
