namespace ProyectoNice.ViewModels
{
    //Base de las herramientas que se muestran dentro de la ventana principal
    public abstract class vm_Herramienta : ObservableObject
    {
        public vm_Principal Principal { get; set; }

        //Cierra la ventana, ejecuta la accion en Revit y vuelve a abrir la ventana
        protected void Ejecutar(Action accion) => Principal.Ejecutar(accion);

        //Mensaje en la barra de estado
        protected void Aviso(string texto) => Principal.Estado = texto;
    }
}
