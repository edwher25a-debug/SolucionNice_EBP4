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
        //Familias con punto de insercion visibles en la vista activa
        private List<FamilyInstance> instanciasVista;

        //categorias
        public List<Category> ListaCagoriasCB {  get; set; }
        private Category categoriasSeleccCB;
        public Category CategoriasSeleccCB
        {
            get => categoriasSeleccCB;
            set
            {
                if (!SetProperty(ref categoriasSeleccCB, value)) return;
                ListaFamiliasCB = instanciasVista.Where(i => i.Category.Id == value?.Id)
                    .Select(i => i.Symbol.Family).GroupBy(f => f.Id).Select(g => g.First())
                    .OrderBy(f => f.Name).ToList();
                FamiliaSeleccCB = ListaFamiliasCB.FirstOrDefault();
            }
        }

        //familias
        private List<Family> listaFamiliasCB = new List<Family>();
        public List<Family> ListaFamiliasCB
        {
            get => listaFamiliasCB;
            set => SetProperty(ref listaFamiliasCB, value);
        }
        private Family familiaSeleccCB;
        public Family FamiliaSeleccCB
        {
            get => familiaSeleccCB;
            set => SetProperty(ref familiaSeleccCB, value);
        }
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
            //Familias con punto de insercion visibles en la vista activa
            instanciasVista = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(i => i.Category != null && i.Location is LocationPoint).ToList();

            //categorias de esas familias (dispara la carga de familias)
            ListaCagoriasCB = instanciasVista.Select(i => i.Category).GroupBy(c => c.Id).Select(g => g.First())
                .OrderBy(c => c.Name).ToList();
            CategoriasSeleccCB = ListaCagoriasCB.FirstOrDefault();
            //Accion del Boton
            AceptarBT = new RelayCommand(Validar);
        }
        public void Validar()
        {
            if (FamiliaSeleccCB == null)
            {
                Aviso("Coordenadas a parametros: no hay familias visibles en la vista activa.");
                return;
            }
            Ejecutar(Aceptar);
        }
        public void Aceptar()
        {
            //elementos de la familia seleccionada visibles en la vista activa
            var ElementosCategorias = instanciasVista.Where(i => i.Symbol.Family.Id == FamiliaSeleccCB.Id).ToList();

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

