namespace ProyectoNice.Utils
{
    /// <summary>
    ///     Curva guia vista en planta. Convierte un punto del modelo en abscisa
    ///     (longitud medida sobre la curva) y distancia lateral a la curva.
    /// </summary>
    public sealed class EjeDesarrollo
    {
        private readonly List<XYZ> puntos = new List<XYZ>();
        private readonly List<double> acumulado = new List<double>();

        /// <summary>Longitud de la curva en planta (pies).</summary>
        public double Longitud { get; }

        public EjeDesarrollo(Curve guia)
        {
            int tramos = Math.Max(50, Math.Min(600, (int)Math.Ceiling(guia.Length / 0.3)));
            for (int i = 0; i <= tramos; i++)
            {
                XYZ p = guia.Evaluate((double)i / tramos, true);
                puntos.Add(new XYZ(p.X, p.Y, 0));
                acumulado.Add(i == 0 ? 0 : acumulado[i - 1] + puntos[i].DistanceTo(puntos[i - 1]));
            }

            Longitud = acumulado[tramos];
        }

        /// <summary>
        ///     Abscisa y distancia lateral de un punto. La lateral es positiva a la derecha del
        ///     sentido de avance. Fuera de los extremos la curva se prolonga recta.
        /// </summary>
        public void Proyectar(XYZ punto, out double abscisa, out double lateral)
        {
            var p = new XYZ(punto.X, punto.Y, 0);
            double mejor = double.MaxValue;
            abscisa = 0;
            lateral = 0;

            int ultimo = puntos.Count - 2;
            for (int i = 0; i <= ultimo; i++)
            {
                XYZ a = puntos[i];
                XYZ ab = puntos[i + 1] - a;
                double largo = ab.GetLength();
                if (largo < 1e-9) continue;

                XYZ direccion = ab / largo;
                double t = (p - a).DotProduct(direccion);
                if (i > 0 && t < 0) t = 0;
                if (i < ultimo && t > largo) t = largo;

                XYZ pie = a + direccion * t;
                double distancia = pie.DistanceTo(p);
                if (distancia >= mejor) continue;

                mejor = distancia;
                abscisa = acumulado[i] + t;
                lateral = (p - pie).DotProduct(new XYZ(direccion.Y, -direccion.X, 0));
            }
        }
    }

    public static class MetodoDesarrollo
    {
        /// <summary>Solidos de un elemento en coordenadas del modelo, incluida la geometria de familias.</summary>
        public static List<Solid> ObtenerSolidos(Element elemento)
        {
            var solidos = new List<Solid>();
            GeometryElement geometria = elemento.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometria != null) Recolectar(geometria, solidos);
            return solidos;
        }

        private static void Recolectar(GeometryElement geometria, List<Solid> solidos)
        {
            foreach (GeometryObject objeto in geometria)
            {
                if (objeto is Solid solido && solido.Faces.Size > 0 && solido.Volume > 1e-6)
                    solidos.Add(solido);
                else if (objeto is GeometryInstance instancia)
                    Recolectar(instancia.GetInstanceGeometry(), solidos);
            }
        }

        /// <summary>
        ///     Corta los solidos con la superficie vertical que sigue al eje, desplazada
        ///     <paramref name="lateral" />. Devuelve polilineas en el plano desarrollado:
        ///     X = abscisa sobre el eje, Y = cota Z del modelo.
        /// </summary>
        public static List<List<XYZ>> Cortar(IEnumerable<Solid> solidos, EjeDesarrollo eje, double lateral,
            double abscisaMin, double abscisaMax, double tolerancia)
        {
            XYZ Desarrollar(XYZ punto)
            {
                eje.Proyectar(punto, out double abscisa, out double distancia);
                return new XYZ(abscisa, punto.Z, distancia - lateral);
            }

            return CorteSolidos.Cortar(solidos, Desarrollar, p => p.X >= abscisaMin && p.X <= abscisaMax)
                .Select(traza => Simplificar(traza.Puntos, tolerancia))
                .ToList();
        }

        /// <summary>Quita los puntos intermedios que quedan alineados dentro de la tolerancia.</summary>
        public static List<XYZ> Simplificar(List<XYZ> puntos, double tolerancia)
        {
            var resultado = new List<XYZ> { puntos[0] };
            for (int i = 1; i < puntos.Count - 1; i++)
            {
                XYZ a = resultado[resultado.Count - 1];
                XYZ ab = puntos[i + 1] - a;
                double largo = ab.GetLength();
                if (largo < 1e-9) continue;
                if (ab.CrossProduct(puntos[i] - a).GetLength() / largo > tolerancia) resultado.Add(puntos[i]);
            }

            resultado.Add(puntos[puntos.Count - 1]);
            return resultado;
        }

        /// <summary>Milimetros en papel convertidos a pies de modelo segun la escala de la vista.</summary>
        public static double PapelAModelo(double milimetros, int escala) =>
            UnitUtils.ConvertToInternalUnits(milimetros * escala / 1000.0, UnitTypeId.Meters);

        /// <summary>Estilos de linea del proyecto, ordenados por nombre.</summary>
        public static List<GraphicsStyle> EstilosDeLinea(Document doc)
        {
            var estilos = new List<GraphicsStyle>();
            foreach (Category subcategoria in doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories)
            {
                GraphicsStyle estilo = subcategoria.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (estilo != null) estilos.Add(estilo);
            }

            return estilos.OrderBy(e => e.Name).ToList();
        }

        /// <summary>Las vistas 3D y las tablas no admiten lineas de detalle ni textos.</summary>
        public static bool AdmiteDetalle(View vista) =>
            vista != null && !vista.IsTemplate &&
            (vista.ViewType == ViewType.FloorPlan || vista.ViewType == ViewType.CeilingPlan ||
             vista.ViewType == ViewType.EngineeringPlan || vista.ViewType == ViewType.AreaPlan ||
             vista.ViewType == ViewType.Section || vista.ViewType == ViewType.Elevation ||
             vista.ViewType == ViewType.Detail || vista.ViewType == ViewType.DraftingView);
    }
}
