using ProyectoNice.Utils;

namespace ProyectoNice.Acotado
{
    /// <summary>
    ///     Una vista vista como plano de dibujo: pasa puntos del modelo a coordenadas de la vista
    ///     (X a la derecha, Y hacia arriba, Z hacia el observador) y dibuja en ella.
    /// </summary>
    public sealed class Lienzo
    {
        private readonly XYZ origen;
        private readonly XYZ derecha;
        private readonly XYZ arriba;
        private readonly XYZ haciaObservador;

        public Lienzo(View vista, OpcionesAcotado opciones)
        {
            Vista = vista;
            Opciones = opciones;
            derecha = vista.RightDirection;
            arriba = vista.UpDirection;
            haciaObservador = vista.ViewDirection;

            //En las plantas el plano de dibujo es el del nivel; en cortes y alzados, el del corte
            origen = vista is ViewPlan planta && planta.GenLevel != null
                ? new XYZ(0, 0, planta.GenLevel.ProjectElevation)
                : vista.Origin;

            Recorte = vista.CropBoxActive ? Caja2D.De(EsquinasDe(vista.CropBox)) : Caja2D.Infinita;

            List<XYZ> esquinas = EsquinasEnModelo(vista.CropBox).ToList();
            Volumen = vista.CropBoxActive
                ? new Outline(new XYZ(esquinas.Min(p => p.X), esquinas.Min(p => p.Y), esquinas.Min(p => p.Z)),
                    new XYZ(esquinas.Max(p => p.X), esquinas.Max(p => p.Y), esquinas.Max(p => p.Z)))
                : new Outline(new XYZ(-1e6, -1e6, -1e6), new XYZ(1e6, 1e6, 1e6));
        }

        public View Vista { get; }
        public Document Doc => Vista.Document;
        public OpcionesAcotado Opciones { get; }
        public Caja2D Recorte { get; }

        /// <summary>Volumen que abarca la vista (recorte y profundidad), en coordenadas del modelo.</summary>
        public Outline Volumen { get; }

        public XYZ Direccion(Eje eje) => eje == Eje.X ? derecha : arriba;

        public XYZ AVista(XYZ punto)
        {
            XYZ d = punto - origen;
            return new XYZ(d.DotProduct(derecha), d.DotProduct(arriba), d.DotProduct(haciaObservador));
        }

        public XYZ VectorAVista(XYZ vector) =>
            new XYZ(vector.DotProduct(derecha), vector.DotProduct(arriba), vector.DotProduct(haciaObservador));

        public XYZ AModelo(double x, double y) => origen + derecha * x + arriba * y;

        public double Papel(double milimetros) => MetodoDesarrollo.PapelAModelo(milimetros, Vista.Scale);

        /// <summary>Trazas que dejan los solidos 2 mm detras del plano de la vista, dentro del recorte.</summary>
        public List<Traza> Cortar(IEnumerable<Solid> solidos)
        {
            var detrasDelCorte = new XYZ(0, 0, -UnitUtils.ConvertToInternalUnits(0.002, UnitTypeId.Meters));
            return CorteSolidos.Cortar(solidos, p => AVista(p) - detrasDelCorte, Recorte.Contiene);
        }

        /// <summary>Esquinas de una caja de Revit, en coordenadas de la vista.</summary>
        public IEnumerable<XYZ> EsquinasDe(BoundingBoxXYZ caja) => EsquinasEnModelo(caja).Select(AVista);

        private static IEnumerable<XYZ> EsquinasEnModelo(BoundingBoxXYZ caja) =>
            from x in new[] { caja.Min.X, caja.Max.X }
            from y in new[] { caja.Min.Y, caja.Max.Y }
            from z in new[] { caja.Min.Z, caja.Max.Z }
            select caja.Transform.OfPoint(new XYZ(x, y, z));

        /// <summary>Cota lineal sobre una fila ya colocada. Las estaciones sin referencia se apoyan en marcas.</summary>
        public Dimension Cota(Fila fila)
        {
            double linea = fila.Coordenada ?? throw new ArgumentException("La fila no esta colocada.");
            var referencias = new ReferenceArray();
            foreach (Estacion estacion in fila.Estaciones)
                referencias.Append(estacion.Referencia ?? Marca(fila.Medida, estacion.Posicion, linea));

            double desde = fila.Estaciones[0].Posicion, hasta = fila.Estaciones[fila.Estaciones.Count - 1].Posicion;
            Line trazo = fila.Medida == Eje.X
                ? Line.CreateBound(AModelo(desde, linea), AModelo(hasta, linea))
                : Line.CreateBound(AModelo(linea, desde), AModelo(linea, hasta));

            return Opciones.TipoCota != null
                ? Doc.Create.NewDimension(Vista, trazo, referencias, Opciones.TipoCota)
                : Doc.Create.NewDimension(Vista, trazo, referencias);
        }

        /// <summary>Nivel sobre una cara horizontal, en un punto de esa cara (coordenadas del modelo).</summary>
        public SpotDimension Nivel(Reference cara, XYZ punto)
        {
            SpotDimension nivel = Doc.Create.NewSpotElevation(Vista, cara, punto, punto, punto, punto, false);
            if (Opciones.TipoNivel != null) nivel.ChangeTypeId(Opciones.TipoNivel.Id);
            return nivel;
        }

        public DetailCurve Linea(double x0, double y0, double x1, double y1, GraphicsStyle? estilo = null)
        {
            DetailCurve linea = Doc.Create.NewDetailCurve(Vista, Line.CreateBound(AModelo(x0, y0), AModelo(x1, y1)));
            if (estilo != null) linea.LineStyle = estilo;
            return linea;
        }

        public void Texto(string texto, double x, double y) =>
            TextNote.Create(Doc, Vista.Id, AModelo(x, y), texto, Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));

        /// <summary>Texto con el porcentaje y flecha hacia donde baja, paralelos al tramo de a a b (coordenadas de la vista).</summary>
        public void Pendiente(XYZ a, XYZ b)
        {
            if (Math.Abs(b.X - a.X) < 1e-9) return;
            double pendiente = (b.Y - a.Y) / (b.X - a.X);
            double baja = pendiente > 0 ? -1 : 1;
            XYZ medio = (a + b) / 2;
            double y0 = medio.Y + Papel(9);
            double Y(double x, double y) => y + (x - medio.X) * pendiente;

            double cola = medio.X - baja * Papel(14), punta = medio.X + baja * Papel(14), aleta = punta - baja * Papel(5);
            Linea(cola, Y(cola, y0), punta, Y(punta, y0));
            Linea(punta, Y(punta, y0), aleta, Y(aleta, y0 + Papel(1.6)));
            Linea(punta, Y(punta, y0), aleta, Y(aleta, y0 - Papel(1.6)));

            double xTexto = medio.X - Papel(8);
            Texto((Math.Abs(pendiente) * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%", xTexto, Y(xTexto, y0 + Papel(8)));
        }

        //Marca corta y perpendicular a la cota. Se mantiene dentro del recorte: fuera de el Revit la oculta y borra la cota
        private Reference Marca(Eje medida, double posicion, double linea)
        {
            double mitad = Papel(1.5);
            DetailCurve marca;
            if (medida == Eje.X)
            {
                double y = Math.Max(Recorte.Y0 + mitad, Math.Min(Recorte.Y1 - mitad, linea));
                marca = Linea(posicion, y - mitad, posicion, y + mitad, Opciones.EstiloMarcas);
            }
            else
            {
                double x = Math.Max(Recorte.X0 + mitad, Math.Min(Recorte.X1 - mitad, linea));
                marca = Linea(x - mitad, posicion, x + mitad, posicion, Opciones.EstiloMarcas);
            }

            return marca.GeometryCurve.Reference;
        }
    }
}
