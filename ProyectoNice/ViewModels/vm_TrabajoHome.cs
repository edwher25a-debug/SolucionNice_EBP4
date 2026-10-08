using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;
using System.Text;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_TrabajoHome : vm_Herramienta
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;
        public StringBuilder sb = new StringBuilder();
        //categorias
        public List<Category> ListaCagoriasCB {  get; set; }
        public Category CategoriasSeleccCB { get; set; }
        //Botones
        public RelayCommand AceptarBT { get; set; }
        //Constructor
        public vm_TrabajoHome(Document doc,Selection seleccion)
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
            AceptarBT = new RelayCommand(() => Ejecutar(Aceptar));
        }
        public void Aceptar()
        {
            //obtener elementos que estan en la categoria seleccionada con los ID
            var ElementosCategorias = new FilteredElementCollector(doc).OfCategoryId(CategoriasSeleccCB.Id).WhereElementIsNotElementType().ToList();

            //Transaccion
            using (Transaction transaccion =
      new Transaction(doc, "Adquirir coordenadas de los elementos"))
            {
                transaccion.Start();

                foreach (Element elemento in ElementosCategorias)
                {
                    // Obtener punto de inserción
                    LocationPoint Ubicacion =
                        elemento.Location as LocationPoint;

                    if (Ubicacion == null)
                        continue;

                    XYZ punto =
                        Ubicacion.Point;

                    // Obtener coordenadas compartidas
                    ProjectPosition posicion =
                        doc.ActiveProjectLocation.GetProjectPosition(punto);

                    // Convertir pies a metros
                    double X =
                        MetodoUnidades.PiesAMetros(posicion.EastWest);

                    double Y =
                        MetodoUnidades.PiesAMetros(posicion.NorthSouth);

                    double Z =
                        MetodoUnidades.PiesAMetros(posicion.Elevation);

                    // Asignar coordenadas
                    Parameter parametroX =
                        elemento.LookupParameter("X");

                    Parameter parametroY =
                        elemento.LookupParameter("Y");

                    Parameter parametroZ =
                        elemento.LookupParameter("Z");

                    if (parametroX != null)
                        parametroX.Set(X);

                    if (parametroY != null)
                        parametroY.Set(Y);

                    if (parametroZ != null)
                        parametroZ.Set(Z);
                }

                transaccion.Commit();
            }
        }
    }
}

