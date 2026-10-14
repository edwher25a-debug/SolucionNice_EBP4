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
        }

        public View Vista { get; }
        public Document Doc => Vista.Document;
        public OpcionesAcotado Opciones { get; }
        public Caja2D Recorte { get; }

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

        /// <summary>Esquinas de una caja de Revit, en coordenadas de la vista.</summary>
        public IEnumerable<XYZ> EsquinasDe(BoundingBoxXYZ caja) =>
            from x in new[] { caja.Min.X, caja.Max.X }
            from y in new[] { caja.Min.Y, caja.Max.Y }
            from z in new[] { caja.Min.Z, caja.Max.Z }
            select AVista(caja.Transform.OfPoint(new XYZ(x, y, z)));

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
