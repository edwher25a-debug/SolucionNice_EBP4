using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Nice3point.Revit.Extensions.Runtime;
using ProyectoNice.Utils;
using ProyectoNice.Views;
using System.Text;
namespace ProyectoNice.ViewModels
{
    public sealed class TrabajoFinal : ObservableObject
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;
        public StringBuilder sb = new StringBuilder();
        //categorias
        public List<Category> ListaCagoriasCB {  get; set; }
        public Category CategoriasSeleccCB { get; set; }
        //texto Codigo del proyecto
        public String CodigoProyect { get; set; }
        //Botones
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarBT { get; set; }
        //Propiedad:View
        public v_TrabajoFinal v_TrabajoFinal { get; set; }
        //Constructor
        public TrabajoFinal(Document doc,Selection seleccion)
        {
            this.doc = doc;
            this.seleccion =  seleccion;
            ObtenerPreData();
        }
        public void ObtenerPreData()
        {
            //Obtener las listas de Categorias
            //categorias
            ListaCagoriasCB = doc.Settings.Categories. Cast<Category>().OrderBy(x => x.Name).ToList();
            CategoriasSeleccCB = ListaCagoriasCB.FirstOrDefault();
            //Accion del Boton
            AceptarBT = new RelayCommand(Aceptar);
            CancelarBT = new RelayCommand(Cancelar);
        }
        public void Cancelar()
        {
            //Cerrar Ventana
            v_TrabajoFinal.Close();
        }
        public void Aceptar()
        {
            v_TrabajoFinal.Close();
            //obtener elementos que estan en la categoria seleccionada con los ID
            var ElementosCategorias = new FilteredElementCollector(doc).OfCategoryId(CategoriasSeleccCB.Id).WhereElementIsNotElementType();
            //Enumera las familias
            int secuencia = 1;
            //Transaccion
            using (Transaction transaccion = new Transaction(doc, "Generar Codigo en CodigoBIM"))
            {
                transaccion.Start();
                foreach (Element Categoria_elemento in ElementosCategorias) 
                {
                     Parameter param_comentario = Categoria_elemento.LookupParameter("CodigoBIM");
                    string CodigoProyecto = CodigoProyect.ToUpper();
                    string Categoria = CategoriasSeleccCB.Name.ToUpper();
                    string Id = Categoria_elemento.Id.Value.ToString();
                    string Consecutivo = secuencia.ToString();
                    string codigoBIM = $"{CodigoProyecto}-{Categoria}-{Id}-{Consecutivo}";
                    param_comentario.Set(codigoBIM);
                    secuencia++;
                }
                transaccion.Commit();
            }

        }
    }
}