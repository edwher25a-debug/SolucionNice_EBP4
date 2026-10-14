namespace ProyectoNice.Acotado
{
    /// <summary>Eje de la vista: X hacia la derecha, Y hacia arriba.</summary>
    public enum Eje
    {
        X,
        Y
    }

    public static class EjeExtensiones
    {
        /// <summary>Coordenada de un punto de la vista en ese eje.</summary>
        public static double En(this XYZ punto, Eje eje) => eje == Eje.X ? punto.X : punto.Y;
    }

    /// <summary>Lado del contenido en que se coloca una fila de cotas.</summary>
    public enum Lado
    {
        Abajo,
        Arriba,
        Izquierda,
        Derecha
    }

    /// <summary>Rectangulo en coordenadas de la vista (pies).</summary>
    public readonly struct Caja2D
    {
        public Caja2D(double x0, double y0, double x1, double y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public double X0 { get; }
        public double Y0 { get; }
        public double X1 { get; }
        public double Y1 { get; }

        public static Caja2D Infinita => new Caja2D(double.MinValue, double.MinValue, double.MaxValue, double.MaxValue);
        public static Caja2D Vacia => new Caja2D(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

        public bool EsVacia => X0 > X1 || Y0 > Y1;

        public static Caja2D De(IEnumerable<XYZ> puntos) =>
            puntos.Aggregate(Vacia, (caja, p) => new Caja2D(Math.Min(caja.X0, p.X), Math.Min(caja.Y0, p.Y), Math.Max(caja.X1, p.X), Math.Max(caja.Y1, p.Y)));

        public Caja2D Unir(Caja2D otra) =>
            new Caja2D(Math.Min(X0, otra.X0), Math.Min(Y0, otra.Y0), Math.Max(X1, otra.X1), Math.Max(Y1, otra.Y1));

        public Caja2D Recortar(Caja2D limite) =>
            new Caja2D(Math.Max(X0, limite.X0), Math.Max(Y0, limite.Y0), Math.Min(X1, limite.X1), Math.Min(Y1, limite.Y1));

        public double Min(Eje eje) => eje == Eje.X ? X0 : Y0;
        public double Max(Eje eje) => eje == Eje.X ? X1 : Y1;
        public double Centro(Eje eje) => (Min(eje) + Max(eje)) / 2;

        /// <summary>Coordenada del borde de ese lado, medida en el eje perpendicular a las cotas.</summary>
        public double Borde(Lado lado) => lado switch
        {
            Lado.Abajo => Y0,
            Lado.Arriba => Y1,
            Lado.Izquierda => X0,
            _ => X1
        };

        public bool Contiene(XYZ p) => p.X >= X0 && p.X <= X1 && p.Y >= Y0 && p.Y <= Y1;
    }

    /// <summary>
    ///     Punto que se acota a lo largo de un eje. Si no hay referencia de Revit, se dibuja una
    ///     marca invisible en esa posicion y la cota se apoya en ella.
    /// </summary>
    public readonly struct Estacion
    {
        public Estacion(double posicion, Reference? referencia)
        {
            Posicion = posicion;
            Referencia = referencia;
        }

        public double Posicion { get; }
        public Reference? Referencia { get; }
    }

    /// <summary>Una cota simple o en cadena: sus estaciones y donde va.</summary>
    public sealed class Fila
    {
        /// <summary>Estaciones a menos de 5 mm se consideran la misma: con dos decimales darian un tramo de 0.00.</summary>
        private const double Coincidente = 5.0 / 304.8;

        private Fila(Eje medida, Lado? lado, double? coordenada, IEnumerable<Estacion> estaciones)
        {
            Medida = medida;
            Lado = lado;
            Coordenada = coordenada;
            Estaciones = Unir(estaciones);
        }

        /// <summary>Eje a lo largo del cual mide la cota.</summary>
        public Eje Medida { get; }

        /// <summary>Lado del contenido donde se apila; nulo si la fila tiene coordenada fija.</summary>
        public Lado? Lado { get; }

        /// <summary>Posicion de la linea de cota en el eje perpendicular; nula si la decide el apilado.</summary>
        public double? Coordenada { get; }

        public IReadOnlyList<Estacion> Estaciones { get; }

        public bool EsValida => Estaciones.Count >= 2;

        /// <summary>Identifica filas repetidas: mismo eje, mismo lado y mismas posiciones.</summary>
        public string Firma => $"{Medida}|{Lado}|{Coordenada:F3}|" + string.Join(";", Estaciones.Select(e => e.Posicion.ToString("F3")));

        public static Fila Cadena(Lado lado, params IEnumerable<Estacion>[] grupos) =>
            new Fila(MedidaDe(lado), lado, null, grupos.SelectMany(g => g));

        /// <summary>Solo los extremos de todas las estaciones: la cota total.</summary>
        public static Fila Total(Lado lado, params IEnumerable<Estacion>[] grupos)
        {
            List<Estacion> todas = grupos.SelectMany(g => g).OrderBy(e => e.Posicion).ToList();
            return new Fila(MedidaDe(lado), lado, null, todas.Count < 2 ? todas : new[] { todas[0], todas[todas.Count - 1] });
        }

        public static Fila En(Eje medida, double coordenada, IEnumerable<Estacion> estaciones) =>
            new Fila(medida, null, coordenada, estaciones);

        public Fila Colocada(double coordenada) => new Fila(Medida, null, coordenada, Estaciones);

        private static Eje MedidaDe(Lado lado) => lado == Acotado.Lado.Abajo || lado == Acotado.Lado.Arriba ? Eje.X : Eje.Y;

        //Ordena y funde las estaciones coincidentes; si una de ellas tiene referencia, se queda esa
        private static List<Estacion> Unir(IEnumerable<Estacion> estaciones) =>
            estaciones.OrderBy(e => e.Posicion)
                .Aggregate(new List<List<Estacion>>(), (grupos, e) =>
                {
                    if (grupos.Count > 0 && e.Posicion - grupos[grupos.Count - 1][0].Posicion < Coincidente) grupos[grupos.Count - 1].Add(e);
                    else grupos.Add(new List<Estacion> { e });
                    return grupos;
                })
                .Select(g => g.Where(e => e.Referencia != null).DefaultIfEmpty(g[0]).First())
                .ToList();
    }
}
