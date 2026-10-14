using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Corte a lo largo de una linea guia curva, desenrollado en una vista de dibujo:
    ///     X = longitud medida sobre la guia, Y = cota del modelo.
    ///     Revit no tiene secciones curvas, asi que el resultado es un dibujo
    ///     y no se actualiza si cambia el modelo.
    /// </summary>
    public sealed class vm_CorteDesarrollado : vm_Herramienta
    {
        private const string EstiloPorDefecto = "(por defecto)";

        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Vista de salida
        public string NombreTB { get; set; } = "Corte desarrollado";
        public int EscalaTB { get; set; }
        public bool InvertirCK { get; set; }

        //Superficie de corte, en metros
        public double ProlongarInicioTB { get; set; }
        public double ProlongarFinTB { get; set; }
        public double DesfaseTB { get; set; }

        //Segundo corte, paralelo, para lo que se ve mas alla del corte
        public bool SegundoCorteCK { get; set; }
        public double DesfaseSegundoTB { get; set; } = 0.35;

        //Cotas
        public bool AcotarCK { get; set; } = true;

        //Estilos de linea
        private readonly List<GraphicsStyle> estilos;
        public List<string> ListaEstilosCB { get; }
        public string EstiloCorteSeleccCB { get; set; }
        public string EstiloVistoSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }

        //Constructor
        public vm_CorteDesarrollado(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            EscalaTB = Math.Max(1, doc.ActiveView?.Scale ?? 50);

            estilos = MetodoDesarrollo.EstilosDeLinea(doc);
            ListaEstilosCB = new List<string> { EstiloPorDefecto };
            ListaEstilosCB.AddRange(estilos.Select(e => e.Name));
            EstiloCorteSeleccCB = EstiloPorDefecto;
            EstiloVistoSeleccCB = EstiloPorDefecto;

            AceptarBT = new RelayCommand(Validar);
        }

        public void Validar()
        {
            if (string.IsNullOrWhiteSpace(NombreTB) || EscalaTB < 1 || ProlongarInicioTB < 0 || ProlongarFinTB < 0)
            {
                Aviso("Corte desarrollado: revise el nombre, la escala y las prolongaciones.");
                return;
            }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            //01_Linea guia y elementos a cortar
            CurveElement guia;
            List<Element> elementos;
            try
            {
                List<Element> previos = seleccion.GetElementIds().Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
                List<CurveElement> lineasPrevias = previos.OfType<CurveElement>().ToList();

                guia = lineasPrevias.Count == 1
                    ? lineasPrevias[0]
                    : doc.GetElement(seleccion.PickObject(ObjectType.Element, new FiltroLinea(), "Seleccione la linea guia del corte")) as CurveElement;

                elementos = previos.Where(EsCortable).ToList();
                if (lineasPrevias.Count != 1 || elementos.Count == 0)
                {
                    elementos = seleccion.PickObjects(ObjectType.Element, new FiltroCortable(), "Seleccione los elementos a cortar y pulse Finalizar")
                        .Select(r => doc.GetElement(r)).Where(e => e != null).ToList();
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                Aviso("Corte desarrollado: cancelado.");
                return;
            }

            if (guia == null || elementos.Count == 0)
            {
                Aviso("Corte desarrollado: hace falta una linea guia y al menos un elemento.");
                return;
            }

            //02_Eje de desarrollo: por defecto de izquierda a derecha segun la vista activa
            Curve curva = guia.GeometryCurve;
            XYZ derecha = doc.ActiveView?.RightDirection ?? XYZ.BasisX;
            bool alReves = (curva.GetEndPoint(1) - curva.GetEndPoint(0)).DotProduct(derecha) < 0;
            if (alReves != InvertirCK) curva = curva.CreateReversed();

            var eje = new EjeDesarrollo(curva);
            if (eje.Longitud < doc.Application.ShortCurveTolerance)
            {
                Aviso("Corte desarrollado: la linea guia es vertical o demasiado corta.");
                return;
            }

            double Pies(double metros) => UnitUtils.ConvertToInternalUnits(metros, UnitTypeId.Meters);
            double abscisaMin = -Pies(ProlongarInicioTB);
            double abscisaMax = eje.Longitud + Pies(ProlongarFinTB);
            double tolerancia = Pies(0.0005);

            //03_Cortes
            List<Solid> solidos = elementos.SelectMany(MetodoDesarrollo.ObtenerSolidos).ToList();
            List<List<XYZ>> corte = MetodoDesarrollo.Cortar(solidos, eje, Pies(DesfaseTB), abscisaMin, abscisaMax, tolerancia);
            List<List<XYZ>> visto = SegundoCorteCK
                ? MetodoDesarrollo.Cortar(solidos, eje, Pies(DesfaseTB + DesfaseSegundoTB), abscisaMin, abscisaMax, tolerancia)
                : new List<List<XYZ>>();

            if (corte.Count == 0)
            {
                Aviso("Corte desarrollado: la linea guia no atraviesa ninguno de los elementos elegidos.");
                return;
            }

            //04_Vista de dibujo
            ViewDrafting vista;
            int lineasCorte, lineasVistas, cotas = 0;
            using (Transaction transaccion = new Transaction(doc, "Corte desarrollado"))
            {
                transaccion.Start();

                ViewFamilyType tipoVista = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(t => t.ViewFamily == ViewFamily.Drafting);
                vista = ViewDrafting.Create(doc, tipoVista.Id);
                vista.Name = NombreLibre(NombreTB.Trim());
                vista.Scale = EscalaTB;

                var dibujadas = new HashSet<string>();
                var verticales = new List<DetailCurve>();
                lineasCorte = Dibujar(vista, corte, Estilo(EstiloCorteSeleccCB), dibujadas, verticales);
                lineasVistas = Dibujar(vista, visto, Estilo(EstiloVistoSeleccCB), dibujadas, null);

                if (AcotarCK) cotas = Acotar(vista, verticales);

                transaccion.Commit();
            }

            new UIDocument(doc).ActiveView = vista;

            Aviso($"Corte desarrollado: vista \"{vista.Name}\" con {lineasCorte} lineas de corte, {lineasVistas} vistas y {cotas} cotas. " +
                  $"Longitud de la guia: {UnitUtils.ConvertFromInternalUnits(eje.Longitud, UnitTypeId.Meters):F2} m.");
        }

        private GraphicsStyle Estilo(string nombre) => estilos.FirstOrDefault(e => e.Name == nombre);

        private string NombreLibre(string nombre)
        {
            var usados = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name));
            string libre = nombre;
            for (int i = 2; usados.Contains(libre); i++) libre = $"{nombre} {i}";
            return libre;
        }

        //Dibuja las polilineas sin repetir tramos. Guarda los tramos verticales largos para acotar
        private int Dibujar(ViewDrafting vista, List<List<XYZ>> polilineas, GraphicsStyle estilo, HashSet<string> dibujadas, List<DetailCurve> verticales)
        {
            double corta = doc.Application.ShortCurveTolerance;
            double casiVertical = UnitUtils.ConvertToInternalUnits(0.002, UnitTypeId.Meters);
            double largoMinimo = UnitUtils.ConvertToInternalUnits(0.3, UnitTypeId.Meters);
            int cuenta = 0;

            foreach (List<XYZ> polilinea in polilineas)
            {
                for (int i = 0; i + 1 < polilinea.Count; i++)
                {
                    XYZ a = polilinea[i];
                    XYZ b = polilinea[i + 1];
                    if (a.DistanceTo(b) < corta) continue;

                    string claveA = $"{Math.Round(a.X * 300)},{Math.Round(a.Y * 300)}";
                    string claveB = $"{Math.Round(b.X * 300)},{Math.Round(b.Y * 300)}";
                    string clave = string.CompareOrdinal(claveA, claveB) < 0 ? $"{claveA}|{claveB}" : $"{claveB}|{claveA}";
                    if (!dibujadas.Add(clave)) continue;

                    DetailCurve detalle = doc.Create.NewDetailCurve(vista, Line.CreateBound(a, b));
                    if (estilo != null) detalle.LineStyle = estilo;
                    cuenta++;

                    if (verticales != null && Math.Abs(a.X - b.X) < casiVertical && a.DistanceTo(b) > largoMinimo)
                        verticales.Add(detalle);
                }
            }

            return cuenta;
        }

        //Abajo: cadena entre los tramos verticales del corte y longitud total. A los lados: altura en cada extremo
        private int Acotar(ViewDrafting vista, List<DetailCurve> verticales)
        {
            double junto = UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Meters);
            var unicos = new List<DetailCurve>();
            foreach (DetailCurve tramo in verticales.OrderBy(v => v.GeometryCurve.GetEndPoint(0).X))
            {
                if (unicos.Count == 0 || tramo.GeometryCurve.GetEndPoint(0).X - unicos[unicos.Count - 1].GeometryCurve.GetEndPoint(0).X > junto)
                    unicos.Add(tramo);
            }

            if (unicos.Count < 2) return 0;

            List<Curve> todas = new FilteredElementCollector(doc, vista.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                .Select(c => c.GeometryCurve).ToList();
            double yMin = todas.Min(c => Math.Min(c.GetEndPoint(0).Y, c.GetEndPoint(1).Y));
            double xMin = todas.Min(c => Math.Min(c.GetEndPoint(0).X, c.GetEndPoint(1).X));
            double xMax = todas.Max(c => Math.Max(c.GetEndPoint(0).X, c.GetEndPoint(1).X));

            DetailCurve primero = unicos[0];
            DetailCurve ultimo = unicos[unicos.Count - 1];
            double x0 = primero.GeometryCurve.GetEndPoint(0).X;
            double x1 = ultimo.GeometryCurve.GetEndPoint(0).X;
            double fila1 = yMin - MetodoDesarrollo.PapelAModelo(9, vista.Scale);
            double fila2 = yMin - MetodoDesarrollo.PapelAModelo(16, vista.Scale);
            double alLado = MetodoDesarrollo.PapelAModelo(8, vista.Scale);
            int cuenta = 0;

            if (unicos.Count > 2 && unicos.Count <= 12)
            {
                var cadena = new ReferenceArray();
                foreach (DetailCurve tramo in unicos) cadena.Append(tramo.GeometryCurve.Reference);
                doc.Create.NewDimension(vista, Line.CreateBound(new XYZ(x0, fila1, 0), new XYZ(x1, fila1, 0)), cadena);
                cuenta++;
            }

            var total = new ReferenceArray();
            total.Append(primero.GeometryCurve.Reference);
            total.Append(ultimo.GeometryCurve.Reference);
            double filaTotal = cuenta > 0 ? fila2 : fila1;
            doc.Create.NewDimension(vista, Line.CreateBound(new XYZ(x0, filaTotal, 0), new XYZ(x1, filaTotal, 0)), total);
            cuenta++;

            foreach (DetailCurve tramo in new[] { primero, ultimo })
            {
                Curve geometria = tramo.GeometryCurve;
                var altura = new ReferenceArray();
                altura.Append(geometria.GetEndPointReference(0));
                altura.Append(geometria.GetEndPointReference(1));
                double x = tramo == primero ? xMin - alLado : xMax + alLado;
                doc.Create.NewDimension(vista, Line.CreateBound(new XYZ(x, geometria.GetEndPoint(0).Y, 0), new XYZ(x, geometria.GetEndPoint(1).Y, 0)), altura);
                cuenta++;
            }

            return cuenta;
        }

        private static bool EsCortable(Element elemento) =>
            !(elemento is CurveElement) && elemento.Category != null &&
            elemento.Category.CategoryType == CategoryType.Model && elemento.get_Geometry(new Options()) != null;
    }

    //Filtro: elementos de modelo con geometria
    public class FiltroCortable : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            !(elem is CurveElement) && elem.Category != null && elem.Category.CategoryType == CategoryType.Model;

        public bool AllowReference(Reference referencia, XYZ punto) => false;
    }
}
