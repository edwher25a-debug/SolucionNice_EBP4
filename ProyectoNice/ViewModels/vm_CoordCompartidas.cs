using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB.Structure;
using Microsoft.Win32;
using ProyectoNice.Models;
using ProyectoNice.Views;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_CoordCompartidas : ObservableObject
    {
        //Datos de entrada
        public Document doc;

        //Tipos de familia de un punto (basadas en nivel o plano de trabajo)
        private List<FamilySymbol> tiposUnPunto;

        //Categorias
        public List<Category> ListaCategoriasCB { get; set; }
        private Category categoriaSeleccCB;
        public Category CategoriaSeleccCB
        {
            get => categoriaSeleccCB;
            set
            {
                if (!SetProperty(ref categoriaSeleccCB, value)) return;
                ListaFamiliasCB = tiposUnPunto.Where(t => t.Category.Id == value?.Id)
                    .Select(t => t.Family).GroupBy(f => f.Id).Select(g => g.First())
                    .OrderBy(f => f.Name).ToList();
                FamiliaSeleccCB = ListaFamiliasCB.FirstOrDefault();
            }
        }

        //Familias
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
            set
            {
                if (!SetProperty(ref familiaSeleccCB, value)) return;
                ListaTiposCB = tiposUnPunto.Where(t => t.Family.Id == value?.Id).OrderBy(t => t.Name).ToList();
                TipoSeleccCB = ListaTiposCB.FirstOrDefault();
            }
        }

        //Tipos
        private List<FamilySymbol> listaTiposCB = new List<FamilySymbol>();
        public List<FamilySymbol> ListaTiposCB
        {
            get => listaTiposCB;
            set => SetProperty(ref listaTiposCB, value);
        }
        private FamilySymbol tipoSeleccCB;
        public FamilySymbol TipoSeleccCB
        {
            get => tipoSeleccCB;
            set => SetProperty(ref tipoSeleccCB, value);
        }

        //Niveles
        public List<Level> ListaNivelesCB { get; set; }
        public Level NivelSeleccCB { get; set; }

        //Puntos (Nombre, Este, Norte, Elevacion en metros)
        public ObservableCollection<m_PuntoCompartido> Puntos { get; } = new ObservableCollection<m_PuntoCompartido>();

        //Botones
        public RelayCommand ImportarBT { get; set; }
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarBT { get; set; }

        //Propiedad:View
        public v_CoordCompartidas v_CoordCompartidas { get; set; }

        //Constructor
        public vm_CoordCompartidas(Document doc)
        {
            this.doc = doc;

            ObtenerPreData();
        }

        public void ObtenerPreData()
        {
            //Tipos de un punto
            tiposUnPunto = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(t => t.Category != null &&
                            (t.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased ||
                             t.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased))
                .ToList();

            //Niveles
            ListaNivelesCB = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation).ToList();
            NivelSeleccCB = ListaNivelesCB.FirstOrDefault();

            //Categorias (dispara la carga de familias y tipos)
            ListaCategoriasCB = tiposUnPunto.Select(t => t.Category).GroupBy(c => c.Id).Select(g => g.First())
                .OrderBy(c => c.Name).ToList();
            CategoriaSeleccCB = ListaCategoriasCB.FirstOrDefault();

            //Accion del Boton
            ImportarBT = new RelayCommand(Importar);
            AceptarBT = new RelayCommand(Aceptar);
            CancelarBT = new RelayCommand(Cancelar);
        }

        public void Cancelar()
        {
            //Cerrar Ventana
            v_CoordCompartidas.Close();
        }

        public void Importar()
        {
            //CSV o TXT: Nombre;Este;Norte;Elevacion (metros). Se omiten filas no numericas (encabezado)
            var dialogo = new OpenFileDialog { Filter = "Coordenadas (*.csv;*.txt)|*.csv;*.txt" };
            if (dialogo.ShowDialog() != true) return;

            foreach (string linea in File.ReadAllLines(dialogo.FileName))
            {
                char separador = linea.Contains(";") ? ';' : linea.Contains("\t") ? '\t' : ',';
                string[] c = linea.Split(separador);
                if (c.Length < 4) continue;

                if (LeerNumero(c[1], out double este) && LeerNumero(c[2], out double norte) && LeerNumero(c[3], out double elevacion))
                    Puntos.Add(new m_PuntoCompartido { Nombre = c[0].Trim(), Este = este, Norte = norte, Elevacion = elevacion });
            }
        }

        public void Aceptar()
        {
            v_CoordCompartidas.Close();

            if (TipoSeleccCB == null || NivelSeleccCB == null || Puntos.Count == 0) return;

            //01_Transformacion compartidas -> internas
            ProjectPosition origen = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            Transform internaACompartida = Transform.CreateTranslation(new XYZ(origen.EastWest, origen.NorthSouth, origen.Elevation))
                .Multiply(Transform.CreateRotation(XYZ.BasisZ, origen.Angle));
            Transform compartidaAInterna = internaACompartida.Inverse;

            //02_Colocar familias
            using (Transaction transaccion = new Transaction(doc, "Familias en coordenadas compartidas"))
            {
                transaccion.Start();

                if (!TipoSeleccCB.IsActive) TipoSeleccCB.Activate();

                foreach (m_PuntoCompartido p in Puntos)
                {
                    XYZ compartida = new XYZ(
                        UnitUtils.ConvertToInternalUnits(p.Este, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(p.Norte, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(p.Elevacion, UnitTypeId.Meters));

                    FamilyInstance instancia = doc.Create.NewFamilyInstance(compartidaAInterna.OfPoint(compartida),
                        TipoSeleccCB, NivelSeleccCB, StructuralType.NonStructural);

                    if (!string.IsNullOrEmpty(p.Nombre))
                        instancia.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(p.Nombre);
                }

                transaccion.Commit();
            }
        }

        private static bool LeerNumero(string texto, out double valor) =>
            double.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out valor);
    }
}
