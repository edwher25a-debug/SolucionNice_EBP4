using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;
using ProyectoNice.Views;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_ACEROS_PILOTES : ObservableObject
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Tipos de barra
        public List<RebarBarType> ListaBarrasCB { get; set; }
        public RebarBarType BarraSeleccCB { get; set; }

        //Parametros del refuerzo
        public int NumeroBarras { get; set; } = 16;
        public double RecubrimientoCm { get; set; } = 7.5;
        public double LongitudSobreCaraM { get; set; } = 1.0;
        public double LongitudBajoCaraM { get; set; } = 1.0;

        //Botones
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarBT { get; set; }

        //Propiedad:View
        public v_ACEROS_PILOTES v_ACEROS_PILOTES { get; set; }

        //Constructor
        public vm_ACEROS_PILOTES(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            ObtenerPreData();
        }

        public void ObtenerPreData()
        {
            //Tipos de barra
            ListaBarrasCB = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().OrderBy(x => x.Name).ToList();
            BarraSeleccCB = ListaBarrasCB.FirstOrDefault();

            //Accion del Boton
            AceptarBT = new RelayCommand(Aceptar);
            CancelarBT = new RelayCommand(Cancelar);
        }

        public void Cancelar()
        {
            //Cerrar Ventana
            v_ACEROS_PILOTES.Close();
        }

        public void Aceptar()
        {
            v_ACEROS_PILOTES.Close();

            if (BarraSeleccCB == null || NumeroBarras < 1) return;

            //01_Seleccionar cara superior del pilote
            Reference referencia;
            try
            {
                referencia = seleccion.PickObject(ObjectType.Face, new FiltroCaraPlanar(doc), "Seleccione la cara superior del pilote");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return;
            }

            Element pilote = doc.GetElement(referencia);
            if (!RebarHostData.IsValidHost(pilote))
            {
                TaskDialog.Show("Aceros Pilotes", "El elemento seleccionado no admite armadura.");
                return;
            }

            //02_Geometria de la cara (coordenadas del modelo)
            PlanarFace cara = MetodoGeometria.ObtenerCaraPlanarEnPunto(pilote, referencia.GlobalPoint);
            BoundingBoxUV bb = cara.GetBoundingBox();
            XYZ centro = cara.Evaluate((bb.Min + bb.Max) / 2);
            XYZ normal = cara.FaceNormal;
            XYZ ejeU = cara.XVector;
            XYZ ejeV = normal.CrossProduct(ejeU);

            double radioCara = cara.GetEdgesAsCurveLoops()
                .SelectMany(cl => cl)
                .SelectMany(c => c.Tessellate())
                .Min(p => p.DistanceTo(centro));

            //03_Dimensiones en unidades internas
            double db = BarraSeleccCB.BarModelDiameter;
            double recubrimiento = UnitUtils.ConvertToInternalUnits(RecubrimientoCm, UnitTypeId.Centimeters);
            double hSobre = UnitUtils.ConvertToInternalUnits(LongitudSobreCaraM, UnitTypeId.Meters);
            double hBajo = UnitUtils.ConvertToInternalUnits(LongitudBajoCaraM, UnitTypeId.Meters);

            double radioBarras = radioCara - recubrimiento - db / 2;
            double radioPunta = Math.Max(NumeroBarras * db / (2 * Math.PI), db);

            //04_Crear barras
            using (Transaction transaccion = new Transaction(doc, "Aceros Pilotes"))
            {
                transaccion.Start();

                for (int i = 0; i < NumeroBarras; i++)
                {
                    double angulo = 2 * Math.PI * i / NumeroBarras;
                    XYZ dir = ejeU * Math.Cos(angulo) + ejeV * Math.Sin(angulo);

                    XYZ p0 = centro + dir * radioBarras - normal * hBajo;
                    XYZ p1 = centro + dir * radioBarras + normal * hSobre;
                    XYZ p2 = centro + dir * radioPunta + normal * hSobre;

                    var curvas = new List<Curve> { Line.CreateBound(p0, p1), Line.CreateBound(p1, p2) };

                    Rebar.CreateFromCurves(doc, RebarStyle.Standard, BarraSeleccCB, null, null, pilote,
                        normal.CrossProduct(dir), curvas,
                        RebarHookOrientation.Left, RebarHookOrientation.Left, true, true);
                }

                transaccion.Commit();
            }
        }
    }

    //Filtro: solo caras planas
    public class FiltroCaraPlanar : ISelectionFilter
    {
        private readonly Document doc;

        public FiltroCaraPlanar(Document doc)
        {
            this.doc = doc;
        }

        public bool AllowElement(Element elem) => true;

        public bool AllowReference(Reference referencia, XYZ punto) =>
            doc.GetElement(referencia).GetGeometryObjectFromReference(referencia) is PlanarFace;
    }
}
