using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_InsertFamily : vm_Herramienta
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Tipos de dos puntos: basadas en linea, vigas y adaptativas de 2 puntos
        private List<FamilySymbol> tiposDosPuntos;

        //Categorias
        public List<Category> ListaCategoriasCB { get; set; }
        private Category categoriaSeleccCB;
        public Category CategoriaSeleccCB
        {
            get => categoriaSeleccCB;
            set
            {
                if (!SetProperty(ref categoriaSeleccCB, value)) return;
                ListaFamiliasCB = tiposDosPuntos.Where(t => t.Category.Id == value?.Id)
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
                ListaTiposCB = tiposDosPuntos.Where(t => t.Family.Id == value?.Id).OrderBy(t => t.Name).ToList();
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

        //Niveles (familias basadas en linea y vigas)
        public List<Level> ListaNivelesCB { get; set; }
        public Level NivelSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }

        //Constructor
        public vm_InsertFamily(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            ObtenerPreData();
        }

        public void ObtenerPreData()
        {
            //Tipos de dos puntos
            tiposDosPuntos = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(t => t.Category != null && EsDosPuntos(t.Family)).ToList();

            //Niveles
            ListaNivelesCB = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation).ToList();
            NivelSeleccCB = ListaNivelesCB.FirstOrDefault();

            //Categorias (dispara la carga de familias y tipos)
            ListaCategoriasCB = tiposDosPuntos.Select(t => t.Category).GroupBy(c => c.Id).Select(g => g.First())
                .OrderBy(c => c.Name).ToList();
            CategoriaSeleccCB = ListaCategoriasCB.FirstOrDefault();

            //Accion del Boton
            AceptarBT = new RelayCommand(Validar);
        }

        public void Validar()
        {
            if (TipoSeleccCB == null ||
                (NivelSeleccCB == null && !AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(TipoSeleccCB.Family)))
            {
                Aviso("Insertar entre puntos: elija un tipo y un nivel.");
                return;
            }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            bool esAdaptativa = AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(TipoSeleccCB.Family);

            //01_Seleccionar familias en orden y guardar sus puntos de insercion
            var puntos = new List<XYZ>();
            while (true)
            {
                try
                {
                    string rol = puntos.Count == 0 ? "INICIO" : "FIN / INICIO siguiente";
                    Reference referencia = seleccion.PickObject(ObjectType.Element, new FiltroFamiliaPunto(),
                        $"Punto {puntos.Count + 1} ({rol}) - Esc para terminar");
                    puntos.Add(((LocationPoint)doc.GetElement(referencia).Location).Point);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            //02_Crear familia de dos puntos en cadena: cada FIN es el INICIO del siguiente tramo
            StructuralType tipoEstructural = TipoSeleccCB.Family.FamilyPlacementType == FamilyPlacementType.CurveDrivenStructural
                ? StructuralType.Beam
                : StructuralType.NonStructural;

            using (Transaction transaccion = new Transaction(doc, "Insertar familias entre puntos"))
            {
                transaccion.Start();

                if (!TipoSeleccCB.IsActive) TipoSeleccCB.Activate();

                for (int i = 0; i + 1 < puntos.Count; i++)
                {
                    if (puntos[i].DistanceTo(puntos[i + 1]) < doc.Application.ShortCurveTolerance) continue;

                    if (esAdaptativa)
                    {
                        FamilyInstance instancia = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, TipoSeleccCB);
                        IList<ElementId> ids = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instancia);
                        ((ReferencePoint)doc.GetElement(ids[0])).Position = puntos[i];
                        ((ReferencePoint)doc.GetElement(ids[1])).Position = puntos[i + 1];
                    }
                    else
                    {
                        Line linea = Line.CreateBound(puntos[i], puntos[i + 1]);
                        doc.Create.NewFamilyInstance(linea, TipoSeleccCB, NivelSeleccCB, tipoEstructural);
                    }
                }

                transaccion.Commit();
            }
        }

        private static bool EsDosPuntos(Family familia) =>
            familia.FamilyPlacementType == FamilyPlacementType.CurveBased ||
            familia.FamilyPlacementType == FamilyPlacementType.CurveDrivenStructural ||
            (AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(familia) &&
             AdaptiveComponentFamilyUtils.GetNumberOfPlacementPoints(familia) == 2);
    }

    //Filtro: solo familias con punto de insercion
    public class FiltroFamiliaPunto : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FamilyInstance && elem.Location is LocationPoint;

        public bool AllowReference(Reference referencia, XYZ punto) => false;
    }
}
