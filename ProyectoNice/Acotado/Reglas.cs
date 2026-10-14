namespace ProyectoNice.Acotado
{
    public enum TipoVista
    {
        Automatico,
        Planta,
        PlantaViga,
        SeccionColumna,
        Alzado,
        Box,
        Ninguno
    }

    /// <summary>Lo que hay que crear en una vista: cotas, niveles, etiquetas, diametros y dibujos extra.</summary>
    public sealed class PlanDeAcotado
    {
        public List<Fila> Filas { get; } = new List<Fila>();
        public List<(Reference Cara, XYZ Punto)> Niveles { get; } = new List<(Reference, XYZ)>();
        public List<Pieza> Etiquetas { get; } = new List<Pieza>();
        public List<Reference> Diametros { get; } = new List<Reference>();
        public Action<Lienzo>? Dibujo { get; set; }
    }

    /// <summary>
    ///     Reglas de acotado aprendidas de los planos de geometria del proyecto IDU-1739
    ///     (ver reglas-planos-proyecto.md). En cada lado, las filas se apilan desde el elemento
    ///     hacia afuera en el orden de mas tramos a menos: la cota total queda siempre afuera.
    /// </summary>
    public static class Reglas
    {
        /// <summary>Prefijos de nombre de vista usados en el proyecto.</summary>
        private static readonly (string Prefijo, TipoVista Tipo)[] PorNombre =
        {
            ("00_", TipoVista.Planta),
            ("01_", TipoVista.PlantaViga),
            ("02_", TipoVista.SeccionColumna),
            ("10_", TipoVista.Alzado),
            ("11_", TipoVista.Ninguno),
            ("20_", TipoVista.Alzado),
            ("VISTA POR", TipoVista.Alzado)
        };

        public static TipoVista Detectar(Conjunto c, bool cortaUnBox)
        {
            if (cortaUnBox) return TipoVista.Box;

            string nombre = c.Lienzo.Vista.Name.ToUpperInvariant();
            TipoVista? porNombre = PorNombre.Where(p => nombre.StartsWith(p.Prefijo)).Select(p => (TipoVista?)p.Tipo).FirstOrDefault();
            if (porNombre.HasValue) return porNombre.Value;

            if (c.Lienzo.Vista is ViewPlan)
                return c.Hay(Rol.Dado) || c.Hay(Rol.Pilote) ? TipoVista.Planta
                    : c.Hay(Rol.Viga) ? TipoVista.PlantaViga
                    : c.Hay(Rol.Columna) ? TipoVista.SeccionColumna
                    : TipoVista.Ninguno;

            return c.Piezas.Count > 0 ? TipoVista.Alzado : TipoVista.Ninguno;
        }

        public static PlanDeAcotado? Plan(TipoVista tipo, Conjunto c)
        {
            switch (tipo)
            {
                case TipoVista.Planta: return Planta(c);
                case TipoVista.PlantaViga: return PlantaViga(c);
                case TipoVista.SeccionColumna: return SeccionColumna(c);
                case TipoVista.Alzado: return Alzado(c);
                default: return null;
            }
        }

        //00_PLANTA: dado (o cuerpo del estribo) con la posicion de los pilotes y los ejes, en los dos sentidos
        private static PlanDeAcotado Planta(Conjunto c)
        {
            var plan = new PlanDeAcotado();
            foreach (var (lado, eje) in new[] { (Lado.Abajo, Eje.X), (Lado.Izquierda, Eje.Y) })
            {
                plan.Filas.Add(Fila.Cadena(lado, c.Extremos(c.Base, eje), c.Centros(Rol.Pilote, eje)));
                plan.Filas.Add(Fila.Cadena(lado, c.Extremos(c.Base, eje), c.Ejes(eje)));
                plan.Filas.Add(Fila.Total(lado, c.Extremos(c.Base, eje)));
            }

            plan.Etiquetas.AddRange(c.De(Rol.Punto).Concat(c.De(Rol.Apoyo)));
            plan.Diametros.AddRange(c.De(Rol.Pilote).Concat(c.De(Rol.Columna)).SelectMany(p => p.Arcos.Take(1)));
            return plan;
        }

        //01_PLANTA VIGA CABEZAL: viga con los apoyos y los ejes
        private static PlanDeAcotado PlantaViga(Conjunto c)
        {
            var plan = new PlanDeAcotado();
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(c.Cabezal, Eje.X), c.Centros(Rol.Apoyo, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(c.Cabezal, Eje.X), c.Ejes(Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Abajo, c.Extremos(c.Cabezal, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Izquierda, c.Extremos(c.Cabezal, Eje.Y), c.Centros(Rol.Apoyo, Eje.Y), c.Ejes(Eje.Y)));
            plan.Filas.Add(Fila.Total(Lado.Izquierda, c.Extremos(c.Cabezal, Eje.Y)));

            plan.Etiquetas.AddRange(c.De(Rol.Apoyo));
            plan.Diametros.AddRange(c.De(Rol.Columna).SelectMany(p => p.Arcos.Take(1)));
            return plan;
        }

        //02_SECCION PILA: corte horizontal de la columna
        private static PlanDeAcotado SeccionColumna(Conjunto c)
        {
            var plan = new PlanDeAcotado();
            plan.Filas.Add(Fila.Total(Lado.Abajo, c.Extremos(Rol.Columna, Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Izquierda, c.Extremos(Rol.Columna, Eje.Y)));
            plan.Diametros.AddRange(c.De(Rol.Columna).SelectMany(p => p.Arcos.Take(1)));
            return plan;
        }

        //10_ALZADO, 20_SECCION y VISTA POR A/B, segun el ejemplo de Edwin (reglas-planos-proyecto.md):
        //abajo bordes de pilotes, centros y total; dentro de la viga los apoyos; arriba borde-eje-borde;
        //alturas en cada borde y en el eje, con el total por fuera
        private static PlanDeAcotado Alzado(Conjunto c)
        {
            var plan = new PlanDeAcotado();
            Lienzo l = c.Lienzo;
            IReadOnlyList<Pieza> superior = c.Cabezal;
            List<Estacion> bordesBase = c.Extremos(c.Base, Eje.X).ToList();
            List<Estacion> bordesSuperior = c.Extremos(superior, Eje.X).ToList();
            if (bordesSuperior.Count < 2) return plan;

            //Abajo, de adentro hacia afuera
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, bordesBase, c.Bordes(Rol.Pilote, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, bordesBase, c.Centros(Rol.Pilote, Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Abajo, bordesBase));

            //Dentro de la viga, bajo su cara superior: bordes y luego centros de los apoyos
            double coronaViga = c.Extremos(superior, Eje.Y).Last().Posicion;
            if (c.Hay(Rol.Apoyo))
            {
                plan.Filas.Add(Fila.En(Eje.X, coronaViga - l.Papel(4), bordesSuperior.Concat(c.Bordes(Rol.Apoyo, Eje.X))));
                plan.Filas.Add(Fila.En(Eje.X, coronaViga - l.Papel(11), bordesSuperior.Concat(c.Centros(Rol.Apoyo, Eje.X))));
            }

            //Pilas: la columna sobre el dado
            if (c.Hay(Rol.Columna) && c.Hay(Rol.Dado))
                plan.Filas.Add(Fila.En(Eje.X, c.Extremos(Rol.Dado, Eje.Y).Last().Posicion + l.Papel(4), bordesBase.Concat(c.Extremos(Rol.Columna, Eje.X))));

            //Alturas en los bordes y en el eje: fondo, caras de la base y de la viga, y la corona en ese punto
            double paso = l.Papel(0.5);
            double xIzq = bordesSuperior[0].Posicion + paso, xDer = bordesSuperior[1].Posicion - paso;
            //Eje dibujado en la vista; si no hay ninguno, el centro de la viga
            List<Estacion> eje = c.EjeCentral(Eje.X)
                .DefaultIfEmpty(new Estacion((bordesSuperior[0].Posicion + bordesSuperior[1].Posicion) / 2, null))
                .ToList();
            IEnumerable<Estacion> Alturas(double x) =>
                c.Extremos(c.Base, Eje.Y).Concat(c.Extremos(superior, Eje.Y)).Concat(c.Corona(x) is Estacion corona ? new[] { corona } : Array.Empty<Estacion>());

            plan.Filas.Add(Fila.Cadena(Lado.Izquierda, Alturas(xIzq)));
            plan.Filas.Add(Fila.Total(Lado.Izquierda, Alturas(xIzq)));
            plan.Filas.Add(Fila.Cadena(Lado.Derecha, Alturas(xDer)));
            plan.Filas.Add(Fila.Total(Lado.Derecha, Alturas(xDer)));
            plan.Filas.AddRange(eje.Select(e => Fila.En(Eje.Y, e.Posicion + l.Papel(1.5), Alturas(e.Posicion))));

            //Arriba de todo, sobre la corona: borde-eje-borde
            double techo = new[] { xIzq, xDer }.Concat(eje.Select(e => e.Posicion))
                .Select(x => c.Corona(x)?.Posicion ?? coronaViga)
                .Concat(new[] { c.Contenido.Y1 })
                .Max();
            plan.Filas.Add(Fila.En(Eje.X, techo + l.Papel(l.Opciones.PrimeraFila + l.Opciones.EntreFilas), bordesSuperior.Concat(eje)));

            //Pendiente de la corona a cada lado del eje, si hay tablero sobre la viga
            List<XYZ> corona = new[] { xIzq }.Concat(eje.Select(e => e.Posicion)).Concat(new[] { xDer })
                .Select(x => c.Corona(x) is Estacion e ? new XYZ(x, e.Posicion, 0) : null)
                .OfType<XYZ>()
                .Where(p => p.Y > coronaViga + l.Papel(1))
                .ToList();
            if (corona.Count == 3) plan.Dibujo = lienzo => { lienzo.Pendiente(corona[0], corona[1]); lienzo.Pendiente(corona[1], corona[2]); };

            //Niveles a la derecha: fondo, cara superior de la base y del cabezal
            plan.Niveles.AddRange(new[] { c.Nivel(c.Base, false, true), c.Nivel(c.Base, true, true), c.Nivel(superior, true, true) }
                .OfType<(Reference, XYZ)>()
                .Distinct());

            plan.Etiquetas.AddRange(c.De(Rol.Pilote).Take(1));
            return plan;
        }
    }
}
