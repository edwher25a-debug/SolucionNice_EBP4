namespace ProyectoNice.Acotado
{
    /// <summary>Las piezas y los ejes de una vista, con las consultas que usan las reglas.</summary>
    public sealed class Conjunto
    {
        /// <summary>Estilos de linea que marcan ejes (trazado, tablero, pila...).</summary>
        private static readonly string[] EstilosEje = { "EJE", "CENTER" };

        /// <summary>
        ///     Lo que delimita el dibujo para apilar las cotas. No cuentan los pilotes (se cortan con lineas de
        ///     rotura), los puntos de coordenadas ni los boxes, que pueden extenderse mas alla de la vista.
        /// </summary>
        private static readonly Rol[] RolesDelContenido = { Rol.Dado, Rol.Columna, Rol.Viga, Rol.Apoyo };

        private readonly Lazy<Caja2D> contenido;
        private readonly Lazy<List<(double X, double Y, Reference Referencia, Eje Mide)>> ejes;

        public Conjunto(Lienzo lienzo)
        {
            Lienzo = lienzo;
            Piezas = Pieza.DeLaVista(lienzo);
            contenido = new Lazy<Caja2D>(() => Piezas
                .Where(p => RolesDelContenido.Contains(p.Rol))
                .Aggregate(Caja2D.Vacia, (caja, p) => caja.Unir(p.Caja)));
            ejes = new Lazy<List<(double, double, Reference, Eje)>>(LineasDeEje);
        }

        public Lienzo Lienzo { get; }
        public IReadOnlyList<Pieza> Piezas { get; }

        public Caja2D Contenido => contenido.Value;

        public IEnumerable<Pieza> De(Rol rol) => Piezas.Where(p => p.Rol == rol);
        public bool Hay(Rol rol) => Piezas.Any(p => p.Rol == rol);

        /// <summary>Base de una planta: el dado si lo hay; si no, el cuerpo del estribo o la viga.</summary>
        public Rol Base => Hay(Rol.Dado) ? Rol.Dado : Rol.Viga;

        /// <summary>Caras extremas del conjunto de piezas de ese papel: la primera y la ultima en el eje.</summary>
        public IEnumerable<Estacion> Extremos(Rol rol, Eje eje)
        {
            List<Pieza> piezas = De(rol).ToList();
            if (piezas.Count == 0) return Enumerable.Empty<Estacion>();
            return new[]
            {
                piezas.Select(p => p.Extremo(eje, false)).OrderBy(e => e.Posicion).First(),
                piezas.Select(p => p.Extremo(eje, true)).OrderBy(e => e.Posicion).Last()
            };
        }

        public IEnumerable<Estacion> Centros(Rol rol, Eje eje) => De(rol).Select(p => p.Centro(eje));

        /// <summary>Ejes dibujados que cortan a lo que se acota, perpendiculares a la cota.</summary>
        public IEnumerable<Estacion> Ejes(Eje eje) =>
            ejes.Value
                .Where(e => e.Mide == eje)
                .Select(e => new Estacion(eje == Eje.X ? e.X : e.Y, e.Referencia))
                .Where(e => e.Posicion > Contenido.Min(eje) && e.Posicion < Contenido.Max(eje));

        /// <summary>Cara horizontal superior o inferior del papel indicado (la mas alta o la mas baja de sus piezas) y donde va su nivel.</summary>
        public (Reference Cara, XYZ Punto)? Nivel(Rol rol, bool superior)
        {
            List<CaraPlana> caras = De(rol)
                .Select(p => p.CaraExtrema(Eje.Y, superior))
                .OfType<CaraPlana>()
                .OrderBy(c => c.En(Eje.Y))
                .ToList();
            CaraPlana? elegida = superior ? caras.LastOrDefault() : caras.FirstOrDefault();
            return elegida?.Referencia is Reference referencia ? (referencia, elegida.PuntoParaNivel) : ((Reference, XYZ)?)null;
        }

        private List<(double, double, Reference, Eje)> LineasDeEje() =>
            new FilteredElementCollector(Lienzo.Doc, Lienzo.Vista.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                .Where(c => c.LineStyle != null && EstilosEje.Any(c.LineStyle.Name.ToUpperInvariant().Contains) && c.GeometryCurve is Line)
                .Select(c => (Line)c.GeometryCurve)
                .Select(l => (Punto: Lienzo.AVista(l.Origin), Direccion: Lienzo.VectorAVista(l.Direction), l.Reference))
                .Where(c => Math.Abs(c.Direccion.X) < 0.01 || Math.Abs(c.Direccion.Y) < 0.01)
                .Select(c => (c.Punto.X, c.Punto.Y, c.Reference, Math.Abs(c.Direccion.X) < 0.01 ? Eje.X : Eje.Y))
                .ToList();
    }
}
