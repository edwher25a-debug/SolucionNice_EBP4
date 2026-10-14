using System.Globalization;
using ProyectoNice.Utils;

namespace ProyectoNice.Acotado
{
    /// <summary>
    ///     Corte transversal de un box de dos celdas y su regla de acotado, confirmada por Edwin
    ///     (ver reglas-acotado.md). Las caras de un box en curva no sirven de referencia, asi que
    ///     todas las cotas se apoyan en marcas.
    /// </summary>
    public sealed class PerfilBox
    {
        /// <summary>Separaciones de la regla, en mm de papel.</summary>
        private const double FilaAbajo1 = 9, FilaAbajo2 = 16, FilaArriba1 = 24, EntreFilas = 7, AlLado = 8;
        private const double EjeCadena = 8, EjeTotal = 14.5, Cartela = 6;

        private readonly Lienzo lienzo;
        private readonly List<(XYZ Normal, List<XYZ> Puntos)> trazas;
        private readonly List<double> muros;

        private PerfilBox(Lienzo lienzo, List<(XYZ Normal, List<XYZ> Puntos)> trazas, List<double> muros)
        {
            this.lienzo = lienzo;
            this.trazas = trazas;
            this.muros = muros;
        }

        /// <summary>Perfil del primer box de dos celdas que corta la vista; nulo si no corta ninguno.</summary>
        public static PerfilBox? De(Conjunto c) =>
            c.De(Rol.Box).Select(p => DeUnBox(c.Lienzo, p)).OfType<PerfilBox>().FirstOrDefault();

        private static PerfilBox? DeUnBox(Lienzo lienzo, Pieza box)
        {
            var detrasDelCorte = new XYZ(0, 0, -UnitUtils.ConvertToInternalUnits(0.002, UnitTypeId.Meters));
            List<(XYZ Normal, List<XYZ> Puntos)> trazas = CorteSolidos
                .Cortar(MetodoDesarrollo.ObtenerSolidos(box.Elemento), p => lienzo.AVista(p) - detrasDelCorte, lienzo.Recorte.Contiene)
                .Select(t => (lienzo.VectorAVista(Normal(t.Cara)), t.Puntos))
                .ToList();

            List<double> muros = trazas.Where(t => Math.Abs(t.Normal.X) > 0.999).Select(t => t.Puntos[0].X).OrderBy(x => x).ToList();
            return muros.Count == 6 ? new PerfilBox(lienzo, trazas, muros) : null;
        }

        public PlanDeAcotado Plan()
        {
            double Papel(double milimetros) => lienzo.Papel(milimetros);
            IEnumerable<Estacion> Marcas(IEnumerable<double> posiciones) => posiciones.Select(p => new Estacion(p, null));

            double xIzq = muros[0], xDer = muros[5];
            double paso = UnitUtils.ConvertToInternalUnits(0.002, UnitTypeId.Meters), cerca = UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Meters);
            double yBase = trazas.Min(t => t.Puntos.Min(p => p.Y));
            double yTapa = trazas.Max(t => t.Puntos.Max(p => p.Y));
            List<(XYZ Normal, List<XYZ> Puntos)> tapas = trazas
                .Where(t => t.Normal.Y > 0.95 && EsLaMasAlta(t.Puntos))
                .OrderBy(t => t.Puntos[0].X)
                .ToList();
            double xQuiebre = tapas.Count >= 2 ? tapas[0].Puntos.Last().X : (xIzq + xDer) / 2;

            var plan = new PlanDeAcotado();

            //Abajo: muros y celdas; luego el total
            plan.Filas.Add(Fila.En(Eje.X, yBase - Papel(FilaAbajo1), Marcas(muros)));
            plan.Filas.Add(Fila.En(Eje.X, yBase - Papel(FilaAbajo2), Marcas(new[] { xIzq, xDer })));

            //Arriba: borde-quiebre-borde y borde-eje-borde
            plan.Filas.Add(Fila.En(Eje.X, yTapa + Papel(FilaArriba1), Marcas(new[] { xIzq, xQuiebre, xDer })));
            plan.Filas.Add(Fila.En(Eje.X, yTapa + Papel(FilaArriba1 + EntreFilas), Marcas(new[] { xIzq, (xIzq + xDer) / 2, xDer })));

            //Lados: altura total
            plan.Filas.Add(Fila.En(Eje.Y, xIzq - Papel(AlLado), Marcas(Extremas(CotasEn(xIzq + paso)))));
            plan.Filas.Add(Fila.En(Eje.Y, xDer + Papel(AlLado), Marcas(Extremas(CotasEn(xDer - paso)))));

            //Eje de cada celda: losa inferior + luz libre + losa superior, y al lado la altura total
            List<double> ejesCelda = new[] { (muros[1] + muros[2]) / 2, (muros[3] + muros[4]) / 2 }.ToList();
            foreach (double xEje in ejesCelda)
            {
                List<double> celda = CotasEn(xEje);
                plan.Filas.Add(Fila.En(Eje.Y, xEje - Papel(EjeCadena), Marcas(celda)));
                plan.Filas.Add(Fila.En(Eje.Y, xEje - Papel(EjeTotal), Marcas(Extremas(celda))));
            }

            //Cartelas: todas en horizontal; en vertical las que tocan el muro central
            List<double> celdaIzq = CotasEn(ejesCelda[0]);
            double yMedio = celdaIzq.Count == 4 ? (celdaIzq[1] + celdaIzq[2]) / 2 : (yBase + yTapa) / 2;
            foreach (List<XYZ> cartela in trazas.Where(t => EsCartela(t.Normal)).Select(t => t.Puntos))
            {
                double x0 = cartela[0].X, x1 = cartela.Last().X, y0 = cartela.Min(p => p.Y), y1 = cartela.Max(p => p.Y);
                plan.Filas.Add(Fila.En(Eje.X, y0 > yMedio ? y0 - Papel(Cartela) : y1 + Papel(Cartela), Marcas(new[] { x0, x1 })));

                bool centralPorIzquierda = Math.Abs(x1 - muros[2]) < cerca, centralPorDerecha = Math.Abs(x0 - muros[3]) < cerca;
                if (centralPorIzquierda || centralPorDerecha)
                    plan.Filas.Add(Fila.En(Eje.Y, centralPorIzquierda ? x0 - Papel(Cartela) : x1 + Papel(Cartela), Marcas(new[] { y0, y1 })));
            }

            plan.Dibujo = l => tapas.ForEach(t => Pendiente(l, t.Puntos[0], t.Puntos.Last()));
            return plan;
        }

        private static XYZ Normal(Face cara)
        {
            BoundingBoxUV caja = cara.GetBoundingBox();
            return cara.ComputeNormal((caja.Min + caja.Max) / 2);
        }

        private static bool EsCartela(XYZ normal) =>
            Math.Abs(normal.X) > 0.3 && Math.Abs(normal.X) < 0.95 && Math.Abs(normal.Y) > 0.3 && Math.Abs(normal.Y) < 0.95;

        private static IEnumerable<double> Extremas(List<double> cotas) => cotas.Count < 2 ? cotas : new[] { cotas[0], cotas.Last() };

        private bool EsLaMasAlta(List<XYZ> puntos)
        {
            XYZ medio = (puntos[0] + puntos.Last()) / 2;
            List<double> cotas = CotasEn(medio.X);
            return cotas.Count > 0 && Math.Abs(cotas.Last() - medio.Y) < UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Meters);
        }

        //Cotas de las caras casi horizontales que pasan por x, de abajo arriba
        private List<double> CotasEn(double x) =>
            trazas.Where(t => Math.Abs(t.Normal.Y) >= 0.95 && x >= t.Puntos[0].X - 1e-6 && x <= t.Puntos.Last().X + 1e-6)
                .Select(t => Interpolar(t.Puntos, x))
                .OrderBy(y => y)
                .ToList();

        private static double Interpolar(List<XYZ> puntos, double x)
        {
            XYZ antes = puntos.LastOrDefault(p => p.X <= x) ?? puntos[0];
            XYZ despues = puntos.FirstOrDefault(p => p.X >= x) ?? puntos.Last();
            return Math.Abs(despues.X - antes.X) < 1e-9 ? antes.Y : antes.Y + (despues.Y - antes.Y) * (x - antes.X) / (despues.X - antes.X);
        }

        //Texto con el porcentaje y flecha hacia donde baja, paralelos a la tapa
        private static void Pendiente(Lienzo l, XYZ a, XYZ b)
        {
            if (Math.Abs(b.X - a.X) < 1e-9) return;
            double pendiente = (b.Y - a.Y) / (b.X - a.X);
            double baja = pendiente > 0 ? -1 : 1;
            XYZ medio = (a + b) / 2;
            double y0 = medio.Y + l.Papel(9);
            double Y(double x, double y) => y + (x - medio.X) * pendiente;

            double cola = medio.X - baja * l.Papel(14), punta = medio.X + baja * l.Papel(14), aleta = punta - baja * l.Papel(5);
            l.Linea(cola, Y(cola, y0), punta, Y(punta, y0));
            l.Linea(punta, Y(punta, y0), aleta, Y(aleta, y0 + l.Papel(1.6)));
            l.Linea(punta, Y(punta, y0), aleta, Y(aleta, y0 - l.Papel(1.6)));

            double xTexto = medio.X - l.Papel(8);
            l.Texto((Math.Abs(pendiente) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", xTexto, Y(xTexto, y0 + l.Papel(8)));
        }
    }
}
