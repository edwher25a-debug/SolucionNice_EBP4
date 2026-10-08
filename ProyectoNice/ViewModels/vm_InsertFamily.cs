using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;
using ProyectoNice.Views;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_InsertFamily : ObservableObject
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

        //Refuerzo transversal (estribos circulares)
        private bool crearEstribos = true;
        public bool CrearEstribos
        {
            get => crearEstribos;
            set => SetProperty(ref crearEstribos, value);
        }
        public RebarBarType EstriboSeleccCB { get; set; }
        public double SeparacionEstribosCm { get; set; } = 15;
        public double TraslapoEstribosCm { get; set; } = 15;

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
            //Tipos de barra
            ListaBarrasCB = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().OrderBy(x => x.Name).ToList();
            BarraSeleccCB = ListaBarrasCB.FirstOrDefault();
            EstriboSeleccCB = ListaBarrasCB.FirstOrDefault();

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

            if (BarraSeleccCB == null || NumeroBarras < 1) return;
            if (CrearEstribos && (EstriboSeleccCB == null || SeparacionEstribosCm <= 0 || TraslapoEstribosCm < 0)) return;

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

            double dbEstribo = CrearEstribos ? EstriboSeleccCB.BarModelDiameter : 0;
            double separacion = UnitUtils.ConvertToInternalUnits(SeparacionEstribosCm, UnitTypeId.Centimeters);

            double radioEstribo = radioCara - recubrimiento - dbEstribo / 2;
            double radioBarras = radioCara - recubrimiento - dbEstribo - db / 2;
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

                //05_Crear estribos: un anillo en la base y arreglo a lo largo del eje del pilote
                if (CrearEstribos)
                {
                    double zIni = -hBajo + dbEstribo;
                    double zFin = hSobre - BarraSeleccCB.StandardBendDiameter / 2 - db - dbEstribo / 2;
                    XYZ c0 = centro + normal * zIni;

                    //Fleje: vuelta completa mas traslapo, con ganchos a 135° hacia el nucleo
                    double traslapo = UnitUtils.ConvertToInternalUnits(TraslapoEstribosCm, UnitTypeId.Centimeters) / radioEstribo;
                    double a0 = Math.PI / 2 - traslapo / 2;
                    var anillo = new List<Curve>
                    {
                        Arc.Create(c0, radioEstribo, a0, a0 + Math.PI, ejeU, ejeV),
                        Arc.Create(c0, radioEstribo, a0 + Math.PI, a0 + 2 * Math.PI + traslapo, ejeU, ejeV)
                    };

                    RebarHookType gancho135 = ObtenerGancho135();

                    Rebar estribo = Rebar.CreateFromCurves(doc, RebarStyle.StirrupTie, EstriboSeleccCB, gancho135, gancho135, pilote,
                        normal, anillo,
                        RebarHookOrientation.Left, RebarHookOrientation.Left, true, true);

                    if (zFin - zIni > separacion)
                        estribo.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(separacion, zFin - zIni, true, true, true);
                }

                transaccion.Commit();
            }
        }

        private RebarHookType ObtenerGancho135()
        {
            double angulo = 135 * Math.PI / 180;

            return new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>()
                .Where(h => Math.Abs(h.HookAngle - angulo) < 0.01)
                .OrderBy(h => h.Style == RebarStyle.StirrupTie ? 0 : 1)
                .FirstOrDefault()
                ?? RebarHookType.Create(doc, angulo, 6);
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
