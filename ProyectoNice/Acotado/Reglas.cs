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
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(Rol.Viga, Eje.X), c.Centros(Rol.Apoyo, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(Rol.Viga, Eje.X), c.Ejes(Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Abajo, c.Extremos(Rol.Viga, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Izquierda, c.Extremos(Rol.Viga, Eje.Y), c.Centros(Rol.Apoyo, Eje.Y), c.Ejes(Eje.Y)));
            plan.Filas.Add(Fila.Total(Lado.Izquierda, c.Extremos(Rol.Viga, Eje.Y)));

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

        //10_ALZADO, 20_SECCION y VISTA POR A/B: dado (o cuerpo del estribo) y pilotes abajo, viga y apoyos arriba, alturas a la izquierda
        private static PlanDeAcotado Alzado(Conjunto c)
        {
            var plan = new PlanDeAcotado();
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(c.Base, Eje.X), c.Extremos(Rol.Columna, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(c.Base, Eje.X), c.Centros(Rol.Pilote, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Abajo, c.Extremos(c.Base, Eje.X), c.Ejes(Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Abajo, c.Extremos(c.Base, Eje.X)));

            plan.Filas.Add(Fila.Cadena(Lado.Arriba, c.Extremos(Rol.Viga, Eje.X), c.Centros(Rol.Apoyo, Eje.X)));
            plan.Filas.Add(Fila.Cadena(Lado.Arriba, c.Extremos(Rol.Viga, Eje.X), c.Ejes(Eje.X)));
            plan.Filas.Add(Fila.Total(Lado.Arriba, c.Extremos(Rol.Viga, Eje.X)));

            IEnumerable<Estacion>[] alturas = { c.Extremos(c.Base, Eje.Y), c.Extremos(Rol.Viga, Eje.Y), c.Extremos(Rol.Apoyo, Eje.Y) };
            plan.Filas.Add(Fila.Cadena(Lado.Izquierda, alturas));
            plan.Filas.Add(Fila.Total(Lado.Izquierda, alturas));

            plan.Niveles.AddRange(new[]
                {
                    c.Nivel(c.Base, false), c.Nivel(c.Base, true),
                    c.Nivel(Rol.Viga, false), c.Nivel(Rol.Viga, true),
                    c.Nivel(Rol.Apoyo, true)
                }
                .OfType<(Reference, XYZ)>()
                .Distinct());

            plan.Etiquetas.AddRange(c.De(Rol.Pilote).Take(1));
            return plan;
        }
    }
}
