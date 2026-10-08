using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Views;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_InsertFamily : ObservableObject
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Familias de dos puntos (basadas en linea y vigas)
        public List<FamilySymbol> ListaFamiliasCB { get; set; }
        public FamilySymbol FamiliaSeleccCB { get; set; }

        //Niveles
        public List<Level> ListaNivelesCB { get; set; }
        public Level NivelSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarBT { get; set; }

        //Propiedad:View
        public v_InsertFamily v_InsertFamily { get; set; }

        //Constructor
        public vm_InsertFamily(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            ObtenerPreData();
        }

        public void ObtenerPreData()
        {
            //Familias de dos puntos
            ListaFamiliasCB = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(fs => fs.Family.FamilyPlacementType == FamilyPlacementType.CurveBased ||
                             fs.Family.FamilyPlacementType == FamilyPlacementType.CurveDrivenStructural)
                .OrderBy(fs => fs.FamilyName).ThenBy(fs => fs.Name).ToList();
            FamiliaSeleccCB = ListaFamiliasCB.FirstOrDefault();

            //Niveles
            ListaNivelesCB = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation).ToList();
            NivelSeleccCB = ListaNivelesCB.FirstOrDefault();

            //Accion del Boton
            AceptarBT = new RelayCommand(Aceptar);
            CancelarBT = new RelayCommand(Cancelar);
        }

        public void Cancelar()
        {
            //Cerrar Ventana
            v_InsertFamily.Close();
        }

        public void Aceptar()
        {
            v_InsertFamily.Close();

            if (FamiliaSeleccCB == null || NivelSeleccCB == null) return;

            //01_Seleccionar familias en orden y guardar sus puntos de insercion
            var puntos = new List<XYZ>();
            while (true)
            {
                try
                {
                    string rol = puntos.Count % 2 == 0 ? "INICIO" : "FIN";
                    Reference referencia = seleccion.PickObject(ObjectType.Element, new FiltroFamiliaPunto(),
                        $"Punto {puntos.Count + 1} ({rol}) - Esc para terminar");
                    puntos.Add(((LocationPoint)doc.GetElement(referencia).Location).Point);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            //02_Crear familia de dos puntos: INICIO - FIN / INICIO - FIN ...
            StructuralType tipoEstructural = FamiliaSeleccCB.Family.FamilyPlacementType == FamilyPlacementType.CurveDrivenStructural
                ? StructuralType.Beam
                : StructuralType.NonStructural;

            using (Transaction transaccion = new Transaction(doc, "Insertar familias entre puntos"))
            {
                transaccion.Start();

                if (!FamiliaSeleccCB.IsActive) FamiliaSeleccCB.Activate();

                for (int i = 0; i + 1 < puntos.Count; i += 2)
                {
                    if (puntos[i].DistanceTo(puntos[i + 1]) < doc.Application.ShortCurveTolerance) continue;

                    Line linea = Line.CreateBound(puntos[i], puntos[i + 1]);
                    doc.Create.NewFamilyInstance(linea, FamiliaSeleccCB, NivelSeleccCB, tipoEstructural);
                }

                transaccion.Commit();
            }
        }
    }

    //Filtro: solo familias con punto de insercion
    public class FiltroFamiliaPunto : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FamilyInstance && elem.Location is LocationPoint;

        public bool AllowReference(Reference referencia, XYZ punto) => false;
    }
}
