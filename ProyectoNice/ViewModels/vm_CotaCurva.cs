using System.Globalization;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Cota dibujada que sigue una linea curva y muestra su longitud medida sobre la curva.
    ///     Revit solo acota en recta, asi que se dibuja con lineas de detalle y un texto:
    ///     no se actualiza si la linea cambia.
    /// </summary>
    public sealed class vm_CotaCurva : vm_Herramienta
    {
        private const string MismoEstilo = "(el de la linea)";

        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Lado hacia el que se separa la cota
        public List<string> ListaLadosCB { get; } = new List<string> { "Arriba / derecha", "Abajo / izquierda" };
        public string LadoSeleccCB { get; set; }

        //Medidas en milimetros de papel
        public double SeparacionTB { get; set; } = 9;
        public double HolguraTB { get; set; } = 2;
        public double MarcaTB { get; set; } = 3;
        public int DecimalesTB { get; set; } = 2;
        public bool AgruparCK { get; set; } = true;

        //Estilo de linea
        private readonly List<GraphicsStyle> estilos;
        public List<string> ListaEstilosCB { get; }
        public string EstiloSeleccCB { get; set; }

        //Tipo de texto
        public List<TextNoteType> ListaTiposTextoCB { get; }
        public TextNoteType TipoTextoSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }

        //Constructor
        public vm_CotaCurva(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            LadoSeleccCB = ListaLadosCB[0];

            estilos = MetodoDesarrollo.EstilosDeLinea(doc);
            ListaEstilosCB = new List<string> { MismoEstilo };
            ListaEstilosCB.AddRange(estilos.Select(e => e.Name));
            EstiloSeleccCB = MismoEstilo;

            ListaTiposTextoCB = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .OrderBy(t => t.Name).ToList();
            ElementId porDefecto = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            TipoTextoSeleccCB = ListaTiposTextoCB.FirstOrDefault(t => t.Id == porDefecto) ?? ListaTiposTextoCB.FirstOrDefault();

            AceptarBT = new RelayCommand(Validar);
        }

        public void Validar()
        {
            if (!MetodoDesarrollo.AdmiteDetalle(doc.ActiveView))
            {
                Aviso("Cota en curva: abra una planta, seccion, alzado o vista de dibujo.");
                return;
            }

            if (TipoTextoSeleccCB == null || SeparacionTB <= 0 || MarcaTB <= 0 || HolguraTB < 0 || DecimalesTB < 0 || DecimalesTB > 6)
            {
                Aviso("Cota en curva: revise el tipo de texto, las medidas y los decimales.");
                return;
            }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            View vista = doc.ActiveView;

            //01_Lineas: las que ya esten seleccionadas o las que se elijan ahora
            List<CurveElement> lineas = seleccion.GetElementIds().Select(id => doc.GetElement(id)).OfType<CurveElement>().ToList();
            if (lineas.Count == 0)
            {
                try
                {
                    lineas = seleccion.PickObjects(ObjectType.Element, new FiltroLinea(), "Seleccione las lineas a acotar y pulse Finalizar")
                        .Select(r => doc.GetElement(r)).OfType<CurveElement>().ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    Aviso("Cota en curva: cancelado.");
                    return;
                }
            }

            if (lineas.Count == 0)
            {
                Aviso("Cota en curva: no se selecciono ninguna linea.");
                return;
            }

            //02_Dibujar una cota por linea
            int hechas = 0;
            var fallos = new List<string>();
            using (Transaction transaccion = new Transaction(doc, "Cota en curva"))
            {
                transaccion.Start();

                foreach (CurveElement linea in lineas)
                {
                    using (SubTransaction paso = new SubTransaction(doc))
                    {
                        paso.Start();
                        try
                        {
                            Dibujar(vista, linea);
                            paso.Commit();
                            hechas++;
                        }
                        catch (Exception ex)
                        {
                            if (!paso.HasEnded()) paso.RollBack();
                            fallos.Add(ex.Message);
                        }
                    }
                }

                if (hechas > 0) transaccion.Commit();
                else transaccion.RollBack();
            }

            Aviso(fallos.Count == 0
                ? $"Cota en curva: {hechas} cota(s) dibujada(s)."
                : $"Cota en curva: {hechas} dibujada(s), {fallos.Count} con error. {fallos[0]}");
        }

        private void Dibujar(View vista, CurveElement linea)
        {
            int escala = Math.Max(1, vista.Scale);
            double separacion = MetodoDesarrollo.PapelAModelo(SeparacionTB, escala);
            double holgura = MetodoDesarrollo.PapelAModelo(HolguraTB, escala);
            double marca = MetodoDesarrollo.PapelAModelo(MarcaTB, escala) / 2;
            double textoSobre = MetodoDesarrollo.PapelAModelo(1, escala);

            //La cota se dibuja en el plano de la vista
            XYZ normal = vista.ViewDirection;
            XYZ origen = linea is DetailCurve ? linea.GeometryCurve.GetEndPoint(0) : (vista.SketchPlane?.GetPlane().Origin ?? vista.Origin);
            XYZ AlPlano(XYZ p) => p - normal * (p - origen).DotProduct(normal);

            Curve curva = linea.GeometryCurve;
            XYZ Punto(double t) => AlPlano(curva.Evaluate(t, true));
            XYZ Tangente(double t)
            {
                XYZ delta = Punto(Math.Min(1, t + 0.005)) - Punto(Math.Max(0, t - 0.005));
                if (delta.GetLength() < 1e-9) throw new InvalidOperationException("La linea se ve de punta en esta vista.");
                return delta.Normalize();
            }

            //Lado: se decide una vez, en el punto medio, para que no cambie a lo largo de la curva
            XYZ lateralMedio = normal.CrossProduct(Tangente(0.5));
            double haciaArriba = lateralMedio.DotProduct(vista.UpDirection);
            double referencia = Math.Abs(haciaArriba) > 0.2 ? haciaArriba : lateralMedio.DotProduct(vista.RightDirection);
            double signo = (referencia >= 0 ? 1 : -1) * (LadoSeleccCB == ListaLadosCB[0] ? 1 : -1);
            XYZ Lateral(double t) => normal.CrossProduct(Tangente(t)) * signo;

            GraphicsStyle estilo = EstiloSeleccCB == MismoEstilo
                ? linea.LineStyle as GraphicsStyle
                : estilos.FirstOrDefault(e => e.Name == EstiloSeleccCB);

            var creados = new List<ElementId>();
            void Agregar(Curve geometria)
            {
                DetailCurve detalle = doc.Create.NewDetailCurve(vista, geometria)
                    ?? throw new InvalidOperationException("Revit no pudo dibujar la curva de cota en esta vista.");
                if (estilo != null) detalle.LineStyle = estilo;
                creados.Add(detalle.Id);
            }

            //Curva de cota, paralela a la linea
            if (curva is Line)
            {
                Agregar(Line.CreateBound(Punto(0) + Lateral(0) * separacion, Punto(1) + Lateral(1) * separacion));
            }
            else
            {
                const int tramos = 16;
                var puntos = new List<XYZ>();
                for (int i = 0; i <= tramos; i++)
                {
                    double t = (double)i / tramos;
                    puntos.Add(Punto(t) + Lateral(t) * separacion);
                }

                //NewDetailCurve no acepta una HermiteSpline: hay que pasarla a NurbSpline
                Agregar(NurbSpline.CreateCurve(HermiteSpline.Create(puntos, false)));
            }

            //Lineas de referencia y marcas oblicuas en los extremos
            foreach (double t in new[] { 0.0, 1.0 })
            {
                XYZ p = Punto(t);
                XYZ lateral = Lateral(t);
                Agregar(Line.CreateBound(p + lateral * holgura, p + lateral * (separacion + holgura)));

                XYZ enCota = p + lateral * separacion;
                XYZ diagonal = (Tangente(t) + lateral).Normalize();
                Agregar(Line.CreateBound(enCota - diagonal * marca, enCota + diagonal * marca));
            }

            //Texto con la longitud medida sobre la curva, legible de izquierda a derecha
            XYZ tangenteTexto = Tangente(0.5);
            double haciaDerecha = tangenteTexto.DotProduct(vista.RightDirection);
            if (haciaDerecha < -1e-6 || (Math.Abs(haciaDerecha) <= 1e-6 && tangenteTexto.DotProduct(vista.UpDirection) < 0))
                tangenteTexto = -tangenteTexto;

            var opciones = new TextNoteOptions(TipoTextoSeleccCB.Id)
            {
                HorizontalAlignment = HorizontalTextAlignment.Center,
                VerticalAlignment = VerticalTextAlignment.Bottom,
                Rotation = Math.Atan2(tangenteTexto.DotProduct(vista.UpDirection), tangenteTexto.DotProduct(vista.RightDirection))
            };

            double metros = UnitUtils.ConvertFromInternalUnits(curva.Length, UnitTypeId.Meters);
            string valor = metros.ToString("F" + DecimalesTB, CultureInfo.InvariantCulture);
            XYZ sobreTexto = normal.CrossProduct(tangenteTexto);
            XYZ posicion = Punto(0.5) + Lateral(0.5) * separacion + sobreTexto * textoSobre;
            creados.Add(TextNote.Create(doc, vista.Id, posicion, valor, opciones).Id);

            if (!AgruparCK) return;
            try
            {
                doc.Regenerate();
                doc.Create.NewGroup(creados);
            }
            catch (Exception)
            {
                //Si Revit no deja agrupar, la cota queda como elementos sueltos
            }
        }
    }

    //Filtro: lineas de modelo y de detalle
    public class FiltroLinea : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is CurveElement;

        public bool AllowReference(Reference referencia, XYZ punto) => false;
    }
}
