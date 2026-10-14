namespace ProyectoNice.Models
{
    //Plano de la lista de exportacion
    public sealed class m_PlanoExportar : ObservableObject
    {
        public ViewSheet Plano { get; set; }
        public string Numero { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Revision { get; set; } = "";

        private bool seleccionado;
        public bool Seleccionado
        {
            get => seleccionado;
            set => SetProperty(ref seleccionado, value);
        }
    }
}
