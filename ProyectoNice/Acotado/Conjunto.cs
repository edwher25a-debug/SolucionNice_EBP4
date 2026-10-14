using ProyectoNice.Utils;

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
        private readonly Lazy<List<Traza>> perfil;
        private readonly Lazy<List<Pieza>> vigaPrincipal;

        public Conjunto(Lienzo lienzo)
        {
            Lienzo = lienzo;
            Piezas = Pieza.DeLaVista(lienzo);
            contenido = new Lazy<Caja2D>(() => Piezas
                .Where(p => RolesDelContenido.Contains(p.Rol))
                .Aggregate(Caja2D.Vacia, (caja, p) => caja.Unir(p.Caja)));
            ejes = new Lazy<List<(double, double, Reference, Eje)>>(LineasDeEje);
            perfil = new Lazy<List<Traza>>(PerfilDelCorte);
            vigaPrincipal = new Lazy<List<Pieza>>(() => (lienzo.Vista is ViewPlan
                    ? De(Rol.Viga).OrderByDescending(p => (p.Caja.X1 - p.Caja.X0) * (p.Caja.Y1 - p.Caja.Y0))
                    : De(Rol.Viga).OrderBy(p => p.Caja.Y0))
                .Take(1).ToList());
        }

        public Lienzo Lienzo { get; }
        public IReadOnlyList<Pieza> Piezas { get; }

        public Caja2D Contenido => contenido.Value;

        public IEnumerable<Pieza> De(Rol rol) => Piezas.Where(p => p.Rol == rol);
        public bool Hay(Rol rol) => Piezas.Any(p => p.Rol == rol);

        /// <summary>
        ///     La viga que manda: en plantas la mayor; en cortes y alzados la mas baja, que es la viga cabezal
        ///     o el cuerpo del estribo, no el espaldar que va encima.
        /// </summary>
        public IReadOnlyList<Pieza> VigaPrincipal => vigaPrincipal.Value;

        /// <summary>Lo que se apoya en los pilotes: el dado si lo hay; si no, la viga principal.</summary>
        public IReadOnlyList<Pieza> Base => Hay(Rol.Dado) ? De(Rol.Dado).ToList() : VigaPrincipal;

        /// <summary>Lo que recibe los apoyos: la viga principal; si no hay, la base.</summary>
        public IReadOnlyList<Pieza> Cabezal => VigaPrincipal.Count > 0 ? VigaPrincipal : Base;

        public IEnumerable<Estacion> Extremos(Rol rol, Eje eje) => Extremos(De(rol), eje);

        /// <summary>Caras extremas de un conjunto de piezas: la primera y la ultima en el eje.</summary>
        public IEnumerable<Estacion> Extremos(IEnumerable<Pieza> piezas, Eje eje)
        {
            List<Pieza> lista = piezas.ToList();
            if (lista.Count == 0) return Enumerable.Empty<Estacion>();
            return new[]
            {
                lista.Select(p => p.Extremo(eje, false)).OrderBy(e => e.Posicion).First(),
                lista.Select(p => p.Extremo(eje, true)).OrderBy(e => e.Posicion).Last()
            };
        }

        public IEnumerable<Estacion> Centros(Rol rol, Eje eje) => De(rol).Select(p => p.Centro(eje));

        /// <summary>Los dos bordes de cada pieza de ese papel, por ejemplo los de cada pilote.</summary>
        public IEnumerable<Estacion> Bordes(Rol rol, Eje eje) =>
            De(rol).SelectMany(p => new[] { p.Extremo(eje, false), p.Extremo(eje, true) });

        /// <summary>El eje dibujado mas cercano al centro de lo que se acota (eje de trazado o de tablero).</summary>
        public IEnumerable<Estacion> EjeCentral(Eje eje) =>
            Ejes(eje).OrderBy(e => Math.Abs(e.Posicion - Contenido.Centro(eje))).Take(1);

        /// <summary>
        ///     Punto mas alto de lo que corta la vista en x: la corona. Sale del corte de todo lo que hay en el
        ///     volumen de la vista, tablero incluido, asi que se acota con una marca.
        /// </summary>
        public Estacion? Corona(double x) =>
            perfil.Value.Where(t => t.CubreX(x)).Select(t => (double?)t.YEn(x)).Max() is double y
                ? new Estacion(y, null)
                : (Estacion?)null;

        /// <summary>Ejes dibujados que cortan a lo que se acota, perpendiculares a la cota.</summary>
        public IEnumerable<Estacion> Ejes(Eje eje) =>
            ejes.Value
                .Where(e => e.Mide == eje)
                .Select(e => new Estacion(eje == Eje.X ? e.X : e.Y, e.Referencia))
                .Where(e => e.Posicion > Contenido.Min(eje) && e.Posicion < Contenido.Max(eje));

        /// <summary>
        ///     Cara horizontal superior o inferior de esas piezas (la mas alta o la mas baja) y el punto de esa
        ///     cara, a la izquierda o a la derecha, donde va su nivel.
        /// </summary>
        public (Reference Cara, XYZ Punto)? Nivel(IEnumerable<Pieza> piezas, bool superior, bool derecha)
        {
            List<CaraPlana> caras = piezas
                .Select(p => p.CaraExtrema(Eje.Y, superior))
                .OfType<CaraPlana>()
                .OrderBy(c => c.En(Eje.Y))
                .ToList();
            CaraPlana? elegida = superior ? caras.LastOrDefault() : caras.FirstOrDefault();
            return elegida?.Referencia is Reference referencia ? (referencia, elegida.PuntoParaNivel(derecha)) : ((Reference, XYZ)?)null;
        }

        //Corte de todo lo que hay dentro del volumen de la vista salvo pilotes y puntos, aunque la plantilla lo oculte:
        //el tablero suele ir en otra vista superpuesta, pero las alturas se acotan hasta su corona
        private List<Traza> PerfilDelCorte()
        {
            var excluidos = new HashSet<ElementId>(Piezas.Where(p => p.Rol == Rol.Pilote || p.Rol == Rol.Punto).Select(p => p.Elemento.Id));
            IEnumerable<Solid> solidos = new FilteredElementCollector(Lienzo.Doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(Lienzo.Volumen))
                .Where(e => e.Category?.CategoryType == CategoryType.Model && !(e is CurveElement) && !excluidos.Contains(e.Id))
                .SelectMany(MetodoDesarrollo.ObtenerSolidos);
            return Lienzo.Cortar(solidos).Where(t => t.EsHorizontal).ToList();
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
