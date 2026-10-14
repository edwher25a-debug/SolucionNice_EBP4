namespace ProyectoNice.Utils
{
    /// <summary>Linea que deja una cara al atravesar una superficie de corte, en el plano de salida.</summary>
    public sealed class Traza
    {
        public Traza(Face cara, List<XYZ> puntos)
        {
            Cara = cara;
            Puntos = puntos;
        }

        public Face Cara { get; }

        /// <summary>Puntos (X, Y, 0) ordenados en la direccion en que mas se extiende la traza.</summary>
        public List<XYZ> Puntos { get; }
    }

    /// <summary>
    ///     Corta solidos con una superficie cualquiera. Quien llama da una funcion que lleva cada
    ///     punto del modelo al plano de salida (X, Y) y pone en Z su distancia con signo a la
    ///     superficie: la traza es donde esa distancia vale cero.
    /// </summary>
    public static class CorteSolidos
    {
        public static List<Traza> Cortar(IEnumerable<Solid> solidos, Func<XYZ, XYZ> aSalida, Func<XYZ, bool> admitir) =>
            solidos.SelectMany(s => s.Faces.Cast<Face>())
                .Select(cara => CortarCara(cara, aSalida, admitir))
                .OfType<Traza>()
                .ToList();

        private static Traza? CortarCara(Face cara, Func<XYZ, XYZ> aSalida, Func<XYZ, bool> admitir)
        {
            Mesh? malla = cara.Triangulate();
            if (malla == null) return null;

            List<XYZ> vertices = malla.Vertices.Select(aSalida).ToList();
            List<XYZ> cruces = Enumerable.Range(0, malla.NumTriangles)
                .Select(malla.get_Triangle)
                .SelectMany(Aristas)
                .Select(arista => Cruce(vertices[arista.A], vertices[arista.B]))
                .OfType<XYZ>()
                .Where(admitir)
                .ToList();
            if (cruces.Count < 2) return null;

            //Una cara plana o reglada se corta en una sola linea: basta ordenarla por su eje mas largo
            bool horizontal = cruces.Max(p => p.X) - cruces.Min(p => p.X) >= cruces.Max(p => p.Y) - cruces.Min(p => p.Y);
            return new Traza(cara, (horizontal ? cruces.OrderBy(p => p.X) : cruces.OrderBy(p => p.Y)).ToList());
        }

        private static IEnumerable<(int A, int B)> Aristas(MeshTriangle triangulo) =>
            Enumerable.Range(0, 3).Select(k => ((int)triangulo.get_Index(k), (int)triangulo.get_Index((k + 1) % 3)));

        private static XYZ? Cruce(XYZ a, XYZ b)
        {
            if (a.Z * b.Z > 0 || Math.Abs(a.Z - b.Z) < 1e-12) return null;
            double t = a.Z / (a.Z - b.Z);
            return new XYZ(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0);
        }
    }
}
