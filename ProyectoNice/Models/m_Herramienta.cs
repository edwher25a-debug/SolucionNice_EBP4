using ProyectoNice.ViewModels;

namespace ProyectoNice.Models
{
    public sealed class m_Herramienta
    {
        public string Grupo { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }
        public Func<vm_Herramienta> Crear { get; set; }
    }
}
